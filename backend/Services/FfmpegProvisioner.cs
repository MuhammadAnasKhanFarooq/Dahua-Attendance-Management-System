using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DahuaAttendanceAPI.Services
{
    public static class FfmpegProvisioner
    {
        // Reliable Windows builds: use Gyan Dev builds (automated builds widely used).
        private const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string FfmpegExeRelative = "ffmpeg.exe";

        public static string GetFfmpegExePath()
        {
            var baseDir = AppContext.BaseDirectory;
            var tools = Path.Combine(baseDir, "tools", "ffmpeg");
            var bin = Path.Combine(tools, "bin");
            var exe = Path.Combine(bin, FfmpegExeRelative);
            return exe;
        }

        public static async Task EnsureFfmpegExistsAsync()
        {
            var exe = GetFfmpegExePath();
            if (File.Exists(exe))
                return;

            var tools = Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");
            Directory.CreateDirectory(tools);

            var zipPath = Path.Combine(tools, "ffmpeg.zip");
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(60);
                using var res = await http.GetAsync(FfmpegZipUrl);
                res.EnsureSuccessStatusCode();
                using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await res.Content.CopyToAsync(fs);
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to download FFmpeg binary: " + ex.Message, ex);
            }

            try
            {
                // Extract zip to tools folder
                ZipFile.ExtractToDirectory(zipPath, tools, true);

                // Many zips contain a top-level folder such as ffmpeg-*-essentials_build\bin\ffmpeg.exe
                // Search for ffmpeg.exe under tools
                var found = Directory.GetFiles(tools, "ffmpeg.exe", SearchOption.AllDirectories);
                if (found.Length == 0)
                    throw new Exception("ffmpeg.exe not found inside downloaded archive.");

                var targetBin = Path.Combine(tools, "bin");
                Directory.CreateDirectory(targetBin);
                var first = found[0];
                var dest = Path.Combine(targetBin, "ffmpeg.exe");
                File.Copy(first, dest, true);

                // Make executable permission (not needed on Windows)
            }
            catch (Exception ex)
            {
                throw new Exception("Failed to extract FFmpeg: " + ex.Message, ex);
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }
    }
}
