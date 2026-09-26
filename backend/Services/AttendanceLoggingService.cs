using DahuaAttendanceAPI.Data;
using DahuaAttendanceAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DahuaAttendanceAPI.Services;

public sealed class AttendanceLoggingService : IHostedService
{
    private readonly DahuaSdkService _sdk;
    private readonly IServiceScopeFactory _scopeFactory;

    public AttendanceLoggingService(
        DahuaSdkService sdk,
        IServiceScopeFactory scopeFactory)
    {
        _sdk = sdk;
        _scopeFactory = scopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sdk.OnDeviceRecognition += HandleRecognition;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sdk.OnDeviceRecognition -= HandleRecognition;
        return Task.CompletedTask;
    }

    private void HandleRecognition(DahuaSdkService.DeviceRecognitionEvent ev)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (ev == null || string.IsNullOrWhiteSpace(ev.UserId))
                {
                    return;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var log = new AttendanceLogEntity
                {
                    UserId = ev.UserId.Trim(),
                    UserName = string.IsNullOrWhiteSpace(ev.Name) ? null : ev.Name.Trim(),
                    EventTime = ev.Timestamp,
                    Confidence = ev.Confidence,
                    ChannelId = ev.ChannelId,
                    EventId = ev.EventId,
                    Source = ev.Source,
                    RecordedAt = DateTime.UtcNow
                };

                await db.AttendanceLogs.AddAsync(log);
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AttendanceLoggingService] Failed to save attendance log for user '{ev?.UserId}': {ex.Message}");
            }
        });
    }
}
