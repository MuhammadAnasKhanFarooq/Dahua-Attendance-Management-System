using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace DahuaAttendanceAPI.Services
{
    public static class CascadeProvisioner
    {
        private const string CascadeFileName = "haarcascade_frontalface_default.xml";
        private static readonly string OfficialUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml";

        public static string GetCascadePath(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                // If configured path is already absolute, return it. Otherwise, resolve relative to base dir
                if (Path.IsPathRooted(configuredPath))
                    return configuredPath;

                return Path.Combine(AppContext.BaseDirectory, configuredPath.Replace('/', Path.DirectorySeparatorChar));
            }

            // Default to AppContext.BaseDirectory/Data/<name>
            return Path.Combine(AppContext.BaseDirectory, "Data", CascadeFileName);
        }

        public static async Task EnsureCascadeExistsAsync(string cascadeFullPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(cascadeFullPath) ?? AppContext.BaseDirectory;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(cascadeFullPath))
                    return; // already present

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                using var res = await http.GetAsync(OfficialUrl);
                if (!res.IsSuccessStatusCode)
                    throw new Exception($"Failed to download cascade: {res.StatusCode}");

                var data = await res.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(cascadeFullPath, data);
            }
            catch (Exception ex)
            {
                // bubble up with context
                throw new Exception("Cascade provisioning failed: " + ex.Message, ex);
            }
        }
    }
}
