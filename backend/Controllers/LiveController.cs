using System;
using System.IO;
using System.Threading.Tasks;
using DahuaAttendanceAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace DahuaAttendanceAPI.Controllers
{
    [ApiController]
    [Route("api/live")]
    public class LiveController : ControllerBase
    {
        private readonly LiveFaceService _live;

        public LiveController(LiveFaceService live)
        {
            _live = live;
        }

        [HttpPost("start")]
        public IActionResult Start([FromForm] string rtspUrl)
        {
            if (string.IsNullOrWhiteSpace(rtspUrl)) return BadRequest(new { success = false, message = "rtspUrl is required" });
            try
            {
                _live.Start(rtspUrl);
                return Ok(new { success = true, message = "started" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        [HttpPost("stop")]
        public IActionResult Stop()
        {
            _live.Stop();
            return Ok(new { success = true, message = "stopped" });
        }

        [HttpGet("stream")]
        public async Task Stream()
        {
            // MJPEG multipart response headers
            Response.Headers["Cache-Control"] = "no-cache";
            Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

            var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            Console.WriteLine($"[LiveFace] MJPEG client connected: {remote}");

            var ct = HttpContext.RequestAborted;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var frame = _live.GetLatestFrame();
                        if (frame != null)
                        {
                            var header = System.Text.Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                            await Response.Body.WriteAsync(header, 0, header.Length, ct);
                            await Response.Body.WriteAsync(frame, 0, frame.Length, ct);
                            var tail = System.Text.Encoding.ASCII.GetBytes("\r\n");
                            await Response.Body.WriteAsync(tail, 0, tail.Length, ct);
                            await Response.Body.FlushAsync(ct);
                            Console.WriteLine("[LiveFace] MJPEG frame written");
                        }
                        else
                        {
                            await Task.Delay(50, ct);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[LiveFace] MJPEG stream write error: " + ex.Message);
                        break;
                    }
                }
            }
            finally
            {
                Console.WriteLine($"[LiveFace] MJPEG client disconnected: {remote}");
            }
        }
    }
}
