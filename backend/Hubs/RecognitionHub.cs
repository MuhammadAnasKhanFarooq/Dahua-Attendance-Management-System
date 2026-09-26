using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace DahuaAttendanceAPI.Hubs
{
    public class RecognitionEventDto
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty; // DAHUA or SERVER
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public int EventId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class RecognitionHub : Hub
    {
        public const string HubUrl = "/recognitionHub";

        public Task SendRecognition(RecognitionEventDto ev)
        {
            return Clients.All.SendAsync("Recognition", ev);
        }
    }
}
