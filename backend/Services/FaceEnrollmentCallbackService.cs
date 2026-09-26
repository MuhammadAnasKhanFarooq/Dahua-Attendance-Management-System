using System;
using System.Threading;
using System.Threading.Tasks;
using DahuaAttendanceAPI.Data;
using DahuaAttendanceAPI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DahuaAttendanceAPI.Services
{
    /// <summary>
    /// Hosted background service that listens for DH_ALARM_FACEINFO_COLLECT (0x3240)
    /// events fired by the Dahua SDK and conditionally updates a user's
    /// EnrollmentStatus to "FaceEnrolled" in the database.
    ///
    /// DESIGN CONSTRAINTS:
    ///
    /// 1. The DahuaSdkService is a singleton whose native callbacks fire on
    ///    native SDK threads. AppDbContext is scoped. This service bridges the
    ///    two lifetimes safely: it holds a singleton reference to
    ///    IServiceScopeFactory and opens a short-lived scope per DB write.
    ///
    /// 2. The Dahua SDK has NO per-user face-capture command. The
    ///    DH_ALARM_FACEINFO_COLLECT event fires when the device's self-service
    ///    collection mode runs. szUserID in ALARM_FACEINFO_COLLECT_INFO is set
    ///    by the device/terminal — typically from what the user types on the
    ///    device screen. It is NOT set by any server-side registration call.
    ///
    /// 3. A DB update is made ONLY when ALL of the following are true:
    ///      a. nAction == 2  (stop/completed — not just started)
    ///      b. UserId is non-empty after trimming
    ///      c. A user with that UserId exists in AttendanceUsers
    ///      d. That user's current EnrollmentStatus is "PendingFaceEnrollment"
    ///
    ///    Condition (d) prevents accidentally overwriting a user that is already
    ///    FaceEnrolled or in another state if the device fires a spurious event.
    ///
    /// 4. This service does NOT retrain the LBPH model because it has no photo
    ///    bytes — the device captured the face internally. The LBPH model is
    ///    only retrained when a JPEG is available (via Register or EnrollFace).
    ///    The device-side face feature is stored on the Dahua device itself and
    ///    will be used for access-control recognition there.
    ///
    /// 5. Concurrent safety: the event handler queues events into a
    ///    System.Threading.Channels.Channel (unbounded, single-reader). DB
    ///    writes are serialized through that channel, preventing concurrent
    ///    SaveChangesAsync calls on the same userId from the same rapid-fire
    ///    event burst.
    /// </summary>
    public sealed class FaceEnrollmentCallbackService : BackgroundService
    {
        private readonly DahuaSdkService _sdk;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FaceEnrollmentCallbackService> _logger;

        // Unbounded channel: the SDK event handler enqueues, this service dequeues.
        // Using a channel keeps the native callback non-blocking and ensures
        // DB writes are serialized without holding a lock in the callback.
        private readonly System.Threading.Channels.Channel<DahuaSdkService.DeviceFaceCollectedEvent>
            _queue = System.Threading.Channels.Channel.CreateUnbounded<
                DahuaSdkService.DeviceFaceCollectedEvent>(
                    new System.Threading.Channels.UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false   // SDK callback + any future writers
                    });

        public FaceEnrollmentCallbackService(
            DahuaSdkService sdk,
            IServiceScopeFactory scopeFactory,
            ILogger<FaceEnrollmentCallbackService> logger)
        {
            _sdk         = sdk;
            _scopeFactory = scopeFactory;
            _logger      = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "[FaceEnrollmentCallback] Service started. Subscribing to OnDeviceFaceCollected.");

            // Subscribe to the SDK event. The handler is non-blocking:
            // it only writes to the channel and returns immediately.
            _sdk.OnDeviceFaceCollected += EnqueueEvent;

            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            finally
            {
                _sdk.OnDeviceFaceCollected -= EnqueueEvent;
                _logger.LogInformation(
                    "[FaceEnrollmentCallback] Service stopped. Unsubscribed from OnDeviceFaceCollected.");
            }
        }

        // ---------------------------------------------------------------
        // Non-blocking enqueue — called on the native SDK callback thread.
        // ---------------------------------------------------------------
        private void EnqueueEvent(DahuaSdkService.DeviceFaceCollectedEvent ev)
        {
            // TryWrite never blocks on an unbounded channel.
            _queue.Writer.TryWrite(ev);
        }

        // ---------------------------------------------------------------
        // Sequential processor — runs on the background service thread.
        // ---------------------------------------------------------------
        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            await foreach (var ev in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleCollectionEventAsync(ev, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log but do not crash the processing loop.
                    _logger.LogError(ex,
                        "[FaceEnrollmentCallback] Unhandled error processing event for userId='{UserId}'.",
                        ev.UserId);
                }
            }
        }

        // ---------------------------------------------------------------
        // Core handler — validates and conditionally updates the DB.
        // ---------------------------------------------------------------
        private async Task HandleCollectionEventAsync(
            DahuaSdkService.DeviceFaceCollectedEvent ev,
            CancellationToken ct)
        {
            // Only act on "stop" (action == 2 means collection completed).
            // Action == 1 is "start" — collection just began; ignore it.
            if (ev.Action != 2)
            {
                _logger.LogDebug(
                    "[FaceEnrollmentCallback] Ignoring action={Action} (not a completion event).",
                    ev.Action);
                return;
            }

            // Guard: UserId must be non-empty — the SDK populates this
            // from whatever the user entered on the device terminal.
            string userId = (ev.UserId ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning(
                    "[FaceEnrollmentCallback] DH_ALARM_FACEINFO_COLLECT fired (action=2) "
                    + "with empty szUserID — cannot associate face with any user. "
                    + "This happens when the device does not know which user was enrolled "
                    + "(e.g., self-service mode without user ID entry). No DB update made.");
                return;
            }

            _logger.LogInformation(
                "[FaceEnrollmentCallback] Collection completed for userId='{UserId}'. Checking DB.",
                userId);

            // Open a short-lived scope for the scoped AppDbContext.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var user = await db.AttendanceUsers
                .FirstOrDefaultAsync(u => u.UserId == userId, ct);

            if (user == null)
            {
                // The device reported a UserId that does not exist in our DB.
                // This can happen if the device's own user list drifts from ours,
                // or if someone typed a userId on the device that was never registered.
                // Do NOT create a phantom record.
                _logger.LogWarning(
                    "[FaceEnrollmentCallback] UserId='{UserId}' not found in database. "
                    + "No EnrollmentStatus update made.",
                    userId);
                return;
            }

            // Only update if the user is currently pending enrollment.
            // This prevents overwriting FaceEnrolled, FaceEnrollmentFailed, or
            // DuplicateFace statuses if the device fires a spurious or late event.
            if (user.EnrollmentStatus != "PendingFaceEnrollment")
            {
                _logger.LogInformation(
                    "[FaceEnrollmentCallback] UserId='{UserId}' has EnrollmentStatus='{Status}' "
                    + "(not PendingFaceEnrollment). No update made.",
                    userId, user.EnrollmentStatus);
                return;
            }

            // All guards passed. Mark as enrolled and persist a device-only face record
            // if the user has available face slots.
            //
            // NOTE: We set "FaceEnrolled" here because:
            //   - The device fired action=2 (collection completed)
            //   - The UserId exists in our DB as PendingFaceEnrollment
            //   - The device manages the face feature internally
            //
            // We cannot independently verify the face was stored correctly
            // on the device from this callback alone. If you need stronger
            // verification, call CLIENT_StartFindFaceInfo (not yet bound)
            // after this update to confirm the face record exists on device.
            // Determine next available face index
            var used = await db.AttendanceUserFaces
                .Where(f => f.AttendanceUserId == user.Id && f.IsActive)
                .Select(f => f.FaceIndex)
                .ToListAsync(ct);

            int nextIndex = !used.Contains(1) ? 1 : (!used.Contains(2) ? 2 : -1);

            if (nextIndex == -1)
            {
                _logger.LogWarning("[FaceEnrollmentCallback] Device collected face for userId='{UserId}' but both face slots are occupied. No DB face created.", userId);
                // Still update EnrollmentStatus to FaceEnrolled to reflect device state
                user.EnrollmentStatus = "FaceEnrolled";
                user.UpdatedAt = DateTime.Now;
                await db.SaveChangesAsync(ct);
                return;
            }

            var faceEntity = new AttendanceUserFaceEntity
            {
                AttendanceUserId = user.Id,
                UserId = user.UserId,
                FaceIndex = nextIndex,
                PhotoFileName = null,
                PhotoLength = 0,
                CreatedAt = DateTime.Now,
                UpdatedAt = null,
                IsActive = true
            };

            await db.AttendanceUserFaces.AddAsync(faceEntity, ct);

            user.EnrollmentStatus = "FaceEnrolled";
            user.UpdatedAt = DateTime.Now;

            await db.SaveChangesAsync(ct);

            _logger.LogInformation("[FaceEnrollmentCallback] Created device-only AttendanceUserFace slot {Index} for userId='{UserId}' and set EnrollmentStatus=FaceEnrolled.", nextIndex, userId);
        }
    }
}
