using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using DahuaAttendanceAPI.Hubs;
using OpenCvSharp;
using OpenCvSharp.Face;

namespace DahuaAttendanceAPI.Services
{
    public class LiveFaceService : IDisposable
    {
        private readonly Services.DahuaSdkService? _dahuaSdk;

        // Concurrent store for latest Dahua device recognitions
        private readonly System.Collections.Concurrent.ConcurrentQueue<Services.DahuaSdkService.DeviceRecognitionEvent> _deviceEvents =
            new System.Collections.Concurrent.ConcurrentQueue<Services.DahuaSdkService.DeviceRecognitionEvent>();
        private VideoCapture? _capture;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private readonly string _cascadePath;
        private readonly string _enrolledDir;
        private readonly int _processIntervalMs;
        private readonly double _recognitionThreshold;
        private readonly CascadeClassifier _faceCascade;
        private readonly LBPHFaceRecognizer _recognizer;
        private readonly object _recognizerLock = new object();
        private readonly ConcurrentDictionary<int, DateTime> _recentRecognitions = new();
        // Per-face tracker
        private class FaceTrack
        {
            public int TrackId { get; set; }
            public Rect BBox { get; set; }
            public DateTime LastSeen { get; set; }
            public string Source { get; set; } = ""; // DAHUA or SERVER or UNKNOWN
            public string UserId { get; set; } = "";
            public string Name { get; set; } = "";
            public int Confidence { get; set; }
            public int EventId { get; set; }
        }



        private readonly ConcurrentDictionary<int, FaceTrack> _tracks = new();
        private int _nextTrackId = 1;

        // Duplicate suppression: key => last sent timestamp
        private readonly ConcurrentDictionary<string, DateTime> _duplicateSuppression = new();
        // Hold the latest encoded JPEG frame for MJPEG clients. Use a reference swap so multiple
        // clients can read the latest frame without dequeuing.
        private byte[]? _latestFrame;
        private readonly IConfiguration _config;
        private readonly Data.AppDbContext _db;

        public bool IsRunning { get; private set; }

        // IoU threshold for matching Dahua rects to OpenCV detections (configurable)
        private readonly double _iouThreshold;

        // Duplicate suppression window (seconds)
        private readonly int _duplicateWindowSeconds;

        // SignalR hub context for metadata streaming (injected)
        private readonly IHubContext<RecognitionHub> _hubContext;

        public LiveFaceService(Services.DahuaSdkService? dahuaSdk, IConfiguration config, IHubContext<RecognitionHub> hubContext, Data.AppDbContext db)
        {
            _config = config;
            _dahuaSdk = dahuaSdk;
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            if (_dahuaSdk != null)
            {
                _dahuaSdk.OnDeviceRecognition += ev => _deviceEvents.Enqueue(ev);
            }
            var configured = config["LiveFace:FaceCascadePath"] ?? "Data/haarcascade_frontalface_default.xml";
            _cascadePath = Services.CascadeProvisioner.GetCascadePath(configured);

            // Ensure cascade file exists (provision on first run)
            try
            {
                var t = Services.CascadeProvisioner.EnsureCascadeExistsAsync(_cascadePath);
                t.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Provide a clear message but do not crash — throw a FileNotFoundException to be consistent
                Console.WriteLine("[LiveFace] Cascade provisioning failed: " + ex.Message);
                throw new FileNotFoundException($"Face cascade not found and provisioning failed: {_cascadePath}", ex);
            }
            _enrolledDir = config["LiveFace:EnrolledDir"] ?? "wwwroot/enrolled";
            _processIntervalMs = int.TryParse(config["LiveFace:ProcessIntervalMs"], out var iv) ? iv : 300;
            _recognitionThreshold = double.TryParse(config["LiveFace:RecognitionThreshold"], out var th) ? th : 70.0;
            _iouThreshold = double.TryParse(config["LiveFace:IoUThreshold"], out var iou) ? iou : 0.3;
            _duplicateWindowSeconds = int.TryParse(config["LiveFace:DuplicateWindowSeconds"], out var ds) ? ds : 5;

            // _hubContext injected via DI

            if (!File.Exists(_cascadePath))
            {
                throw new FileNotFoundException($"Face cascade not found at {_cascadePath} even after provisioning.");
            }

            Directory.CreateDirectory(_enrolledDir);

            _faceCascade = new CascadeClassifier(_cascadePath);
            if (_faceCascade.Empty())
            {
                throw new Exception($"Failed to load Haar cascade from {_cascadePath}");
            }

            _recognizer = LBPHFaceRecognizer.Create();

            // initial train from enrolled images
            TrainRecognizer();
        }

        public void Start(string rtspUrl)
        {
            if (IsRunning) return;
            // Protect against null/empty
            if (string.IsNullOrWhiteSpace(rtspUrl))
                throw new ArgumentException("rtspUrl is required", nameof(rtspUrl));

            // Mask password in logs
            string safeUrl = rtspUrl;
            try
            {
                var uri = new Uri(rtspUrl);
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var user = uri.UserInfo.Split(':')[0];
                    var builder = new UriBuilder(uri)
                    {
                        Password = "****",
                        UserName = user
                    };
                    safeUrl = builder.Uri.ToString();
                }
            }
            catch { /* ignore URI parse errors; fall back to raw */ }

            Console.WriteLine($"[LiveFace] Starting RTSP capture for: {safeUrl}");

            // Diagnostics: OpenCV build info
            try
            {
                var info = OpenCvSharp.Cv2.GetBuildInformation();
                Console.WriteLine("[LiveFace] OpenCV build info:");
                Console.WriteLine(info);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LiveFace] Failed to get OpenCV build info: " + ex.Message);
            }

            // Try opening VideoCapture with several backend options and detailed logging
            VideoCapture? cap = null;
            Exception? lastEx = null;

            // Try FFMPEG first
            try
            {
                cap = new VideoCapture();
                Console.WriteLine("[LiveFace] Attempting VideoCapture.Open with FFMPEG backend...");
                if (cap.Open(rtspUrl, OpenCvSharp.VideoCaptureAPIs.FFMPEG))
                {
                    Console.WriteLine("[LiveFace] VideoCapture opened with FFMPEG backend.");
                }
                else
                {
                    Console.WriteLine("[LiveFace] VideoCapture Open returned false for FFMPEG backend.");
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine("[LiveFace] Exception while opening FFMPEG backend: " + ex.Message);
            }

            // If not opened, try default/open-any
            if (cap == null || !cap.IsOpened())
            {
                try
                {
                    cap?.Release();
                    cap = new VideoCapture();
                    Console.WriteLine("[LiveFace] Attempting VideoCapture.Open with any backend (default)...");
                    if (cap.Open(rtspUrl))
                    {
                        Console.WriteLine("[LiveFace] VideoCapture opened with default backend.");
                    }
                    else
                    {
                        Console.WriteLine("[LiveFace] VideoCapture Open returned false for default backend.");
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Console.WriteLine("[LiveFace] Exception while opening default backend: " + ex.Message);
                }
            }

            // If still not opened, try GStreamer if available
            if (cap == null || !cap.IsOpened())
            {
                try
                {
                    cap?.Release();
                    cap = new VideoCapture();
                    Console.WriteLine("[LiveFace] Attempting VideoCapture.Open with GSTREAMER backend...");
                    if (cap.Open(rtspUrl, OpenCvSharp.VideoCaptureAPIs.GSTREAMER))
                    {
                        Console.WriteLine("[LiveFace] VideoCapture opened with GStreamer backend.");
                    }
                    else
                    {
                        Console.WriteLine("[LiveFace] VideoCapture Open returned false for GStreamer backend.");
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Console.WriteLine("[LiveFace] Exception while opening GStreamer backend: " + ex.Message);
                }
            }

            if (cap == null || !cap.IsOpened())
            {
                // Attempt FFmpeg fallback in background
                Console.WriteLine("[LiveFace] VideoCapture failed, starting FFmpeg fallback (background)...");
                _cts = new CancellationTokenSource();
                _captureTask = StartFfmpegCaptureAsync(rtspUrl, _cts.Token);
                IsRunning = true;
                return;
            }

            // Optionally set some capture properties: prefer TCP for RTSP (if backend honors it)
            try
            {
                // Some OpenCV builds support setting RTSP transport via property or backend-specific options.
                // Try common property name via numeric id as best-effort; if not available, ignore.
                const int CAP_PROP_RTSP_TRANSPORT = 100; // best-effort; not guaranteed
                // noop guard removed; directly try numeric property set below
                // Attempt to set via reflection fallback if API exposes RtspTransport property name
                try
                {
                    cap.Set((VideoCaptureProperties)CAP_PROP_RTSP_TRANSPORT, 1);
                }
                catch { }
            }
            catch { }

            _capture = cap;
            if (!_capture.IsOpened())
            {
                throw new InvalidOperationException("Cannot open RTSP stream.");
            }

            _cts = new CancellationTokenSource();
            _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
            IsRunning = true;
        }

        // FFmpeg-based capture fallback: start ffmpeg process to decode RTSP to raw BGR24 frames and feed into ProcessFrame
        private async Task StartFfmpegCaptureAsync(string rtspUrl, CancellationToken ct)
        {
            // ensure ffmpeg available
            try
            {
                await FfmpegProvisioner.EnsureFfmpegExistsAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LiveFace] FFmpeg provisioning failed: " + ex.Message);
                return;
            }

            var ffmpegExe = FfmpegProvisioner.GetFfmpegExePath();
            if (!File.Exists(ffmpegExe))
            {
                Console.WriteLine("[LiveFace] ffmpeg.exe not found at " + ffmpegExe);
                return;
            }

            // Build arguments: use rtsp transport tcp, decode to rawvideo bgr24, write to stdout
            // Mask userinfo in logs
            string safeUrl = rtspUrl;
            try { var u = new Uri(rtspUrl); if (!string.IsNullOrEmpty(u.UserInfo)) { var b = new UriBuilder(u) { Password = "****", UserName = u.UserInfo.Split(':')[0] }; safeUrl = b.Uri.ToString(); } } catch { }

            Console.WriteLine($"[LiveFace] RTSP backend: FFmpeg. Launching ffmpeg for {safeUrl}");

            // FFmpeg arguments as ArgumentList to avoid escaping issues
            // -rtsp_transport tcp ensures TCP
            // -i <url> -f rawvideo -pix_fmt bgr24 -
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Use ArgumentList (available in .NET) to pass arguments without shell escaping
            psi.ArgumentList.Add("-rtsp_transport");
            psi.ArgumentList.Add("tcp");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(rtspUrl); // pass raw URL (with credentials) directly
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("bgr24");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-");

            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine("[LiveFace] Failed to start ffmpeg process.");
                return;
            }

            _ = Task.Run(async () =>
            {
                // read stderr for diagnostics
                var reader = proc.StandardError;
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    // do not log passwords; ffmpeg stderr will not contain URL userinfo normally
                    Console.WriteLine("[ffmpeg] " + line);
                }
            });

            try
            {
                var stdout = proc.StandardOutput.BaseStream;
                var stderr = proc.StandardError;

                // Attempt to detect width and height from ffmpeg stderr output
                int width = 0, height = 0;
                var detectDeadline = DateTime.UtcNow.AddSeconds(10);
                while ((width == 0 || height == 0) && DateTime.UtcNow < detectDeadline)
                {
                    var line = await stderr.ReadLineAsync();
                    if (line == null) break;
                    Console.WriteLine("[ffmpeg] " + line);
                    // Look for patterns like "... 1920x1080 [SAR 1:1 DAR 16:9] ..."
                    try
                    {
                        var tokens = line.Split(' ');
                        foreach (var t in tokens)
                        {
                            if (t.Contains('x'))
                            {
                                var parts = t.Split('x');
                                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                                {
                                    // Basic sanity
                                    if (w > 0 && h > 0 && w < 10000 && h < 10000)
                                    {
                                        width = w; height = h; break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (width == 0 || height == 0)
                {
                    // fallback
                    width = 640; height = 480;
                    Console.WriteLine($"[LiveFace] Failed to detect frame size; falling back to {width}x{height}");
                }

                Console.WriteLine($"[LiveFace] RTSP stream detected: {width}x{height}");
                int frameSize = width * height * 3;
                Console.WriteLine($"[LiveFace] Expected raw frame size: {frameSize} bytes");

                var buffer = new byte[frameSize];

                Console.WriteLine("[LiveFace] FFmpeg process started, reading raw frames...");
                bool firstFrameLogged = false;
                while (!ct.IsCancellationRequested && !proc.HasExited)
                {
                    int read = 0;
                    while (read < frameSize)
                    {
                        int n = await stdout.ReadAsync(buffer, read, frameSize - read, ct);
                        if (n == 0) break;
                        read += n;
                    }

                    if (read < frameSize)
                    {
                        Console.WriteLine("[LiveFace] FFmpeg short read, read=" + read);
                        await Task.Delay(500, ct);
                        continue;
                    }

                    using var mat = new Mat(height, width, MatType.CV_8UC3, buffer);
                    using var frame = mat.Clone();
                    // Log first-frame only once per successful FFmpeg connection
                    if (!firstFrameLogged)
                    {
                        Console.WriteLine($"[LiveFace] First frame received: {width}x{height}");
                        firstFrameLogged = true;
                    }
                    ProcessFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("[LiveFace] FFmpeg capture loop error: " + ex.Message);
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                proc.Dispose();
            }
        }

        // Reconnect loop with exponential backoff that calls StartFfmpegCaptureAsync
        private async Task StartFfmpegCaptureWithReconnectLoop(string rtspUrl, CancellationToken ct)
        {
            int backoffSeconds = 1;
            const int maxBackoff = 30;
            int attempt = 0;

            while (!ct.IsCancellationRequested)
            {
                attempt++;
                Console.WriteLine($"[LiveFace] FFmpeg reconnect attempt {attempt}");

                try
                {
                    // Run single ffmpeg capture attempt; returns when process exits or cancellation requested
                    await StartFfmpegCaptureAsync(rtspUrl, ct);

                    if (ct.IsCancellationRequested)
                        break;

                    // If capture ended without cancellation, log and reconnect
                    Console.WriteLine("[LiveFace] FFmpeg exited unexpectedly. Restarting...");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[LiveFace] FFmpeg attempt failed: " + ex.Message);
                }

                // Wait with exponential backoff before reconnecting
                Console.WriteLine($"[LiveFace] Waiting {backoffSeconds} seconds before reconnect");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
                }
                catch (OperationCanceledException) { break; }

                backoffSeconds = Math.Min(maxBackoff, backoffSeconds * 2);
            }

            Console.WriteLine("[LiveFace] FFmpeg reconnect loop exiting");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cts.Cancel();
            _captureTask?.Wait(2000);
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            IsRunning = false;
        }

        private void CaptureLoop(CancellationToken ct)
        {
            var mat = new Mat();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_capture.Read(mat) || mat.Empty())
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // operate at throttled interval
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    using var frame = mat.Clone();
                    ProcessFrame(frame);

                    sw.Stop();
                    var wait = Math.Max(0, _processIntervalMs - (int)sw.ElapsedMilliseconds);
                    Thread.Sleep(wait);
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
            }
        }

        private void ProcessFrame(Mat frame)
        {
            // convert to gray
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);

            // detect faces
            var rects = _faceCascade.DetectMultiScale(gray, 1.1, 4).ToArray();

            // Logging
            Console.WriteLine($"[LiveFace] Detected {rects.Length} faces");

            // Fetch dahua events into a list for matching
            var dahuaEvents = new List<Services.DahuaSdkService.DeviceRecognitionEvent>();
            while (_deviceEvents.TryDequeue(out var ev))
            {
                dahuaEvents.Add(ev);
                Console.WriteLine($"[LiveFace] Dahua event received: user={ev.UserId} name={ev.Name} evt={ev.EventId}");
            }

            // For each detected face, either match to an existing track or create a new one
            var unmatchedTracks = new HashSet<int>(_tracks.Keys);

            for (int i = 0; i < rects.Length; i++)
            {
                var r = rects[i];

                // Find best matching track by IoU
                int bestTrackId = -1;
                double bestIou = 0.0;
                foreach (var kv in _tracks)
                {
                    var tid = kv.Key;
                    var t = kv.Value;
                    double iou = ComputeIoU(r, t.BBox);
                    Console.WriteLine($"[LiveFace] IoU track {tid} = {iou:F3}");
                    if (iou > bestIou)
                    {
                        bestIou = iou;
                        bestTrackId = tid;
                    }
                }

                FaceTrack track = null;
                if (bestTrackId != -1 && bestIou >= _iouThreshold)
                {
                    // update existing track
                    track = _tracks[bestTrackId];
                    track.BBox = r;
                    track.LastSeen = DateTime.UtcNow;
                    unmatchedTracks.Remove(bestTrackId);
                }
                else
                {
                    // create new track
                    var id = Interlocked.Increment(ref _nextTrackId);
                    track = new FaceTrack
                    {
                        TrackId = id,
                        BBox = r,
                        LastSeen = DateTime.UtcNow,
                        Source = "UNKNOWN",
                        Name = string.Empty,
                        UserId = string.Empty,
                        Confidence = 0,
                        EventId = 0
                    };
                    _tracks[track.TrackId] = track;
                    Console.WriteLine($"[LiveFace] Created track {track.TrackId}");
                }

                // Try to match dahua events to this detection: pick best event IoU
                Services.DahuaSdkService.DeviceRecognitionEvent? matchedEvent = null;
                double bestEventIou = 0.0;
                foreach (var ev in dahuaEvents)
                {
                    var devRect = new Rect(ev.X, ev.Y, ev.Width, ev.Height);
                    double iou = ComputeIoU(r, devRect);
                    Console.WriteLine($"[LiveFace] IoU event {ev.EventId} -> detection {i} = {iou:F3}");
                    if (iou > bestEventIou)
                    {
                        bestEventIou = iou;
                        matchedEvent = ev;
                    }
                }

                // Associate event if above threshold and not duplicate
                if (matchedEvent != null && bestEventIou >= _iouThreshold)
                {
                    var key = $"{matchedEvent.UserId}:{matchedEvent.EventId}:{track.TrackId}";
                    var now = DateTime.UtcNow;
                    if (_duplicateSuppression.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < (_duplicateWindowSeconds * 1000))
                    {
                        Console.WriteLine($"[LiveFace] Suppressing duplicate event {matchedEvent.EventId} for track {track.TrackId}");
                    }
                    else
                    {
                        // assign DAHUA identity
                        track.Source = "DAHUA";
                        track.UserId = matchedEvent.UserId;
                        track.Name = matchedEvent.Name;
                        track.Confidence = matchedEvent.Confidence;
                        track.EventId = matchedEvent.EventId;
                        _duplicateSuppression[key] = now;

                        // Send SignalR update
                        SendRecognitionUpdate(track);
                    }
                }
                else
                {
                    // fallback to server recognition
                    var face = new Mat(gray, r);
                    Cv2.Resize(face, face, new Size(200, 200));
                    var label = RecognizeFace(face, out var conf);
                    if (label != null)
                    {
                        track.Source = "SERVER";
                        track.UserId = label;
                        track.Name = label;
                        track.Confidence = (int)conf;
                        track.EventId = 0;
                        SendRecognitionUpdate(track);
                    }
                    else
                    {
                        track.Source = "UNKNOWN";
                        track.UserId = string.Empty;
                        track.Name = string.Empty;
                        track.Confidence = 0;
                        SendRecognitionUpdate(track);
                    }
                }

                // draw rectangle and label
                Cv2.Rectangle(frame, track.BBox, Scalar.Green, 2);
                var labelText = track.Source == "UNKNOWN" ? "UNKNOWN" : $"[{track.Source}] {track.Name} ({track.UserId})";
                Cv2.PutText(frame, labelText, new Point(track.BBox.X, Math.Max(0, track.BBox.Y - 10)), HersheyFonts.HersheySimplex, 0.6, Scalar.Yellow, 2);
            }

            // expire old tracks
            var expireAfter = TimeSpan.FromMilliseconds(int.Parse(_config["LiveFace:TrackTimeoutMs"] ?? "1500"));
            var toRemove = _tracks.Where(kv => DateTime.UtcNow - kv.Value.LastSeen > expireAfter).Select(kv => kv.Key).ToList();
            foreach (var tid in toRemove)
            {
                _tracks.TryRemove(tid, out var _);
                Console.WriteLine($"[LiveFace] Removed expired track {tid}");
            }

            // encode to jpeg and publish as latest frame for MJPEG clients
            Cv2.ImEncode(".jpg", frame, out byte[] jpeg);
            // swap latest frame reference atomically
            System.Threading.Interlocked.Exchange(ref _latestFrame, jpeg);
        }

        /// <summary>
        /// Recognize a face Mat (grayscale, 200x200). Returns user id and sets confidence.
        /// </summary>
        private string RecognizeFace(Mat faceGray, out double confidence)
        {
            confidence = double.MaxValue;
            lock (_recognizerLock)
            {
                try
                {
                    if (!_recognizer.Empty)
                    {
                        int label;
                        double dist;
                        _recognizer.Predict(faceGray, out label, out dist);
                        confidence = dist;
                        if (label >= 0 && dist <= _recognitionThreshold)
                        {
                            return label.ToString();
                        }
                    }
                }
                catch (Exception)
                {
                    // ignore recognition errors
                }
            }
            return null;
        }

        private double ComputeIoU(Rect a, Rect b)
        {
            int x1 = Math.Max(a.X, b.X);
            int y1 = Math.Max(a.Y, b.Y);
            int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            int interW = Math.Max(0, x2 - x1);
            int interH = Math.Max(0, y2 - y1);
            int interArea = interW * interH;
            int areaA = a.Width * a.Height;
            int areaB = b.Width * b.Height;
            int union = areaA + areaB - interArea;
            if (union <= 0) return 0.0;
            return (double)interArea / union;
        }

        private void SendRecognitionUpdate(FaceTrack track)
        {
            try
            {
                var dto = new Hubs.RecognitionEventDto
                {
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Source = track.Source,
                    UserId = track.UserId,
                    Name = track.Name,
                    Confidence = track.Confidence,
                    EventId = track.EventId,
                    X = track.BBox.X,
                    Y = track.BBox.Y,
                    Width = track.BBox.Width,
                    Height = track.BBox.Height
                };

                // Fire-and-forget SignalR send using injected hub context and protected from exceptions
                try
                {
                    var clients = _hubContext.Clients.All;
                    // Use SendCoreAsync to send the event; protect from exceptions so they don't bubble into processing loop
                    clients.SendCoreAsync("Recognition", new object[] { dto }).ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Console.WriteLine("SignalR send error: " + t.Exception?.GetBaseException());
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to queue SignalR send: " + ex);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to send recognition update: " + ex);
            }
        }

        public byte[] GetLatestFrame()
        {
            // Return the latest frame reference (may be null). Do not dequeue so multiple clients
            // can consume the same image stream.
            return System.Threading.Volatile.Read(ref _latestFrame);
        }

        public bool TryDetectExistingFace(
            byte[] imageBytes,
            string? excludeUserId,
            out string matchedUserId,
            out double confidence)
        {
            matchedUserId = string.Empty;
            confidence = double.MaxValue;

            try
            {
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    return false;
                }

                using var ms = new MemoryStream(imageBytes);
                using var color = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
                if (color.Empty())
                {
                    return false;
                }

                using var gray = new Mat();
                Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                var faces = _faceCascade.DetectMultiScale(
                    gray,
                    1.1,
                    3,
                    HaarDetectionTypes.ScaleImage,
                    new Size(30, 30),
                    new Size());

                if (faces.Length == 0)
                {
                    return false;
                }

                TrainRecognizer();

                using var faceMat = new Mat(gray, faces[0]);
                Cv2.Resize(faceMat, faceMat, new Size(200, 200));

                var match = RecognizeFace(faceMat, out var dist);
                if (string.IsNullOrWhiteSpace(match))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(excludeUserId) &&
                    string.Equals(match, excludeUserId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                matchedUserId = match;
                confidence = dist;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveFace] Existing-face duplicate check failed: {ex.Message}");
                return false;
            }
        }

        public void TrainRecognizer()
        {
            // Load enrolled images from AttendanceUserFaces where IsActive and PhotoFileName exists
            var images = new List<Mat>();
            var labels = new List<int>();

            try
            {
                // Project only required columns to avoid EF materializing any
                // confusing shadow FK properties that may exist in the model
                // metadata. Selecting a lightweight anonymous type reduces the
                // chance of EF generating SQL that references non-existent
                // shadow columns in the database.
                // Use raw SQL via the DbConnection to avoid EF model mapping issues
                // (shadow FK columns) when materializing the full entity. We only
                // need PhotoFileName and AttendanceUserId here.
                var faces = new List<(string PhotoFileName, int AttendanceUserId)>();
                var conn = _db.Database.GetDbConnection();
                try
                {
                    if (conn.State != System.Data.ConnectionState.Open) conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT PhotoFileName, AttendanceUserId FROM AttendanceUserFaces WHERE IsActive = 1 AND PhotoFileName IS NOT NULL AND PhotoFileName <> '' ORDER BY FaceIndex";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var fname = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var auid = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                        if (!string.IsNullOrEmpty(fname)) faces.Add((fname, auid));
                    }
                }
                finally
                {
                    try { if (conn.State == System.Data.ConnectionState.Open) conn.Close(); } catch { }
                }

                // Build mapping from AttendanceUserId (int) -> label
                foreach (var face in faces)
                {
                    try
                    {
                        var filePath = Path.Combine(_enrolledDir, face.PhotoFileName!);
                        if (!File.Exists(filePath))
                            continue;

                        var img = Cv2.ImRead(filePath, ImreadModes.Grayscale);
                        if (img.Empty()) continue;
                        Cv2.Resize(img, img, new Size(200, 200));
                        images.Add(img);
                        labels.Add(face.AttendanceUserId);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveFace] TrainRecognizer DB load failed: {ex.Message}");
            }

            if (images.Count > 0)
            {
                lock (_recognizerLock)
                {
                    _recognizer.Train(images.ToArray(), labels.ToArray());
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _faceCascade?.Dispose();
            _recognizer?.Dispose();
        }
    }

    public class FfmpegReader : IDisposable
    {
        private Process _process;
        private StreamReader _stdout;
        private StreamWriter _stdin;
        private bool _isRunning;

        public FfmpegReader(string ffmpegPath, string rtspUrl)
        {
            // Initialize and start the FFmpeg process
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-i \"{rtspUrl}\" -f rawvideo -pix_fmt bgr24 pipe:1",
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            _process.Start();
            _stdout = new StreamReader(_process.StandardOutput.BaseStream);
            _stdin = new StreamWriter(_process.StandardInput.BaseStream) { AutoFlush = true };
            _isRunning = true;
        }

        public void ReadLoop(Action<Mat> frameAction, CancellationToken cancellationToken)
        {
            try
            {
                var width = 640; // Default width
                var height = 480; // Default height
                var bytesPerPixel = 3; // BGR24 format
                var frameSize = new OpenCvSharp.Size(width, height);

                // Buffer for raw frame data
                var buffer = new byte[width * height * bytesPerPixel];

                while (!cancellationToken.IsCancellationRequested && _isRunning)
                {
                    // Read raw BGR24 frame from FFmpeg
                    int bytesRead = 0;
                    do
                    {
                        int result = _stdout.BaseStream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                        if (result == 0) return; // End of stream
                        bytesRead += result;
                    } while (bytesRead < buffer.Length);

                    // Convert raw data to Mat
                    using var mat = new Mat(height, width, MatType.CV_8UC3, buffer);
                    frameAction(mat);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FFmpeg reader error: " + ex.Message);
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _process?.Kill();
        }

        public void Dispose()
        {
            Stop();
            _stdout?.Dispose();
            _stdin?.Dispose();
            _process?.Dispose();
        }
    }
}
