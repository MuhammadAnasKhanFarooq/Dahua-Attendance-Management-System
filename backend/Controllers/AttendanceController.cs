using DahuaAttendanceAPI.Data;
using DahuaAttendanceAPI.Models;
using DahuaAttendanceAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;

namespace DahuaAttendanceAPI.Controllers;

[ApiController]
[Route("api/Attendance")]
[Authorize]
public class AttendanceController : ControllerBase
{
    private readonly DahuaSdkService _sdk;
    private readonly AppDbContext _db;
    private readonly LiveFaceService _live;
    private readonly IServiceProvider _services;
    private readonly ILogger<AttendanceController> _logger;

    public AttendanceController(
        DahuaSdkService sdk,
        AppDbContext db,
        LiveFaceService live,
        IServiceProvider services,
        ILogger<AttendanceController> logger)
    {
        _sdk = sdk;
        _db = db;
        _live = live;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteAttendanceUser([FromRoute] int id)
    {
        if (id <= 0) return BadRequest(new { Success = false, Message = "Invalid id." });

        var dbUser = await _db.AttendanceUsers.FindAsync(id);
        if (dbUser == null) return NotFound(new { Success = false, Message = "User not found." });

        string userId = dbUser.UserId;
        // Ensure device is logged in
        if (_sdk == null || !_sdk.IsLoggedIn)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Success = false, Message = "Dahua device is not connected. Cannot perform delete." });
        }

        // Attempt device-side deletion and only proceed to DB removal if successful
        DeleteUserResult delRes;
        try
        {
            delRes = _sdk.DeleteAccessControlUser(userId);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Device deletion exception.", Error = ex.Message });
        }

        if (delRes == null || !delRes.Success)
        {
            string msg = delRes == null ? "Device deletion failed." : delRes.Message;
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Device deletion failed.", DeviceMessage = msg, FailCode = delRes?.FailCode, SdkError = delRes?.SdkError });
        }

        // Device deletion succeeded; remove DB rows inside a transaction
        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Delete related faces
            var faces = await _db.AttendanceUserFaces.Where(f => f.AttendanceUserId == dbUser.Id).ToListAsync();
            if (faces.Count > 0) _db.AttendanceUserFaces.RemoveRange(faces);

            // Delete linked application users (only those linked to this attendance user)
            var appUsers = await _db.ApplicationUsers.Where(a => a.AttendanceUserId == dbUser.Id).ToListAsync();
            if (appUsers.Count > 0) _db.ApplicationUsers.RemoveRange(appUsers);

            // Remove attendance user
            _db.AttendanceUsers.Remove(dbUser);

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { }
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to delete user from database after device deletion.", Error = ex.Message });
        }

        try { _live.TrainRecognizer(); } catch { }
        try { var live = _services.GetService(typeof(LiveFaceService)) as LiveFaceService; live?.TrainRecognizer(); } catch { }

        return Ok(new
        {
            Success = true,
            Message = "Attendance user deleted from device and database.",
            DeviceMessage = delRes.Message
        });
    }

    // Ownership helper: verifies that the authenticated application user (from JWT 'sub') is
    // mapped to the same AttendanceUsers.Id as the requested attendance user identifier (UserId string).
    // Returns true when the caller is owner; false otherwise. If caller has no application mapping
    // or the mapping does not match, returns false.
    private async Task<bool> IsOwnerAsync(string requestedAttendanceUserId)
    {
        if (string.IsNullOrWhiteSpace(requestedAttendanceUserId)) return false;

        // Get 'sub' claim which contains ApplicationUserEntity.Id
        var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        if (!int.TryParse(sub, out var appUserId)) return false;

        var appUser = await _db.ApplicationUsers.FindAsync(appUserId);
        if (appUser == null) return false;

        // If the application user has no AttendanceUserId mapping, they cannot be treated as owner
        if (!appUser.AttendanceUserId.HasValue) return false;

        // Lookup attendance user by public UserId (string)
        var attendanceUser = await _db.AttendanceUsers.FirstOrDefaultAsync(u => u.UserId == requestedAttendanceUserId);
        if (attendanceUser == null) return false;

        return attendanceUser.Id == appUser.AttendanceUserId.Value;
    }



    // ----------------------------
    // Faces API
    // ----------------------------

    [HttpGet("{userId}/faces")]
    public async Task<IActionResult> GetUserFaces([FromRoute] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { Success = false, Message = "userId is required." });

        // Resolve the requested attendance user. The route parameter may be either
        // the public UserId (string) or the numeric database Id. Support both for
        // compatibility with callers that pass the numeric Id.
        AttendanceUserEntity? user = null;

        if (int.TryParse(userId, out var numericId))
        {
            user = await _db.AttendanceUsers.FindAsync(numericId);
        }

        if (user == null)
        {
            user = await _db.AttendanceUsers.FirstOrDefaultAsync(u => u.UserId == userId);
        }

        if (user == null) return NotFound(new { Success = false, Message = "User not found." });

        // Ownership: if caller is a normal User role, they may only access their own attendance record.
        if (User.Identity != null && User.IsInRole("User"))
        {
            if (!await IsOwnerAsync(user.UserId)) return Forbid();
        }

        var dbFaces = await _db.AttendanceUserFaces
            .Where(f => f.AttendanceUserId == user.Id)
            .ToListAsync();

        // Map DB faces into dictionary by FaceIndex
        var faceMap = new Dictionary<int, dynamic>();
        foreach (var f in dbFaces)
        {
            if (f.FaceIndex != 1 && f.FaceIndex != 2) continue;

            faceMap[f.FaceIndex] = new
            {
                Id = f.Id,
                UserId = f.UserId,
                FaceIndex = f.FaceIndex,
                PhotoFileName = f.PhotoFileName,
                PhotoLength = f.PhotoLength,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt,
                IsActive = f.IsActive,
                DeviceEnrolled = false,
                PhotoUrl = string.IsNullOrEmpty(f.PhotoFileName) ? null : $"/enrolled/{f.PhotoFileName}"
            };
        }

        // NOTE: Do NOT query the physical Dahua device when listing database face records.
        // Device login/listen/subscription (StartListen / StartListenEx / recognition subscription)
        // is potentially unsafe from this code path and may trigger native callbacks.
        // The Faces API MUST return database-backed face metadata only.

        // Return deterministic list ordered by FaceIndex (1 then 2)
        var merged = new List<object>();
        for (int idx = 1; idx <= 2; idx++)
        {
            if (faceMap.TryGetValue(idx, out var entry)) merged.Add(entry);
        }

        return Ok(new { Success = true, Faces = merged });
    }

    [HttpPost("{userId}/faces")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = "Admin,Security")]
    public async Task<IActionResult> CreateUserFace([FromRoute] string userId, [FromForm] IFormFile? photo)
    {
        if (User.Identity != null && User.IsInRole("User"))
        {
            if (!await IsOwnerAsync(userId)) return Forbid();
        }
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest(new { Success = false, Message = "userId is required." });
        if (photo == null || photo.Length == 0) return BadRequest(new { Success = false, Message = "photo is required." });

        var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(u => u.UserId == userId);
        if (dbUser == null) return NotFound(new { Success = false, Message = "User not found." });

        byte[] photoBytes;
        await using (var ms = new MemoryStream()) { await photo.CopyToAsync(ms); photoBytes = ms.ToArray(); }

        // check existing active faces
        var used = await _db.AttendanceUserFaces.Where(f => f.AttendanceUserId == dbUser.Id && f.IsActive).Select(f => f.FaceIndex).ToListAsync();
        int nextIndex = !used.Contains(1) ? 1 : (!used.Contains(2) ? 2 : -1);
        if (nextIndex == -1)
        {
            return Conflict(new { Success = false, Message = "User already has maximum allowed faces (2). Use update/replace to change a slot or delete one first." });
        }

        // follow same enrollment flow as EnrollFace
        bool initialized = _sdk.Initialize();
        if (!initialized) return Ok(new { Success = false, Message = "Dahua SDK initialization failed." });

        try
        {
            CreateUserResult createUserResult = _sdk.CreateAccessControlUser(userId, dbUser.UserName ?? userId);
            if (!createUserResult.Success && createUserResult.FailCode != 52)
            {
                return Ok(new { Success = false, Message = "Device user creation failed.", CreateUserResult = createUserResult });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Error while ensuring device user exists.", Error = ex.Message });
        }

        FaceEnrollResult enrollResult = _sdk.EnrollFace(userId, photoBytes);
        if (!enrollResult.Success)
        {
            return Ok(new { Success = false, Message = "Device enrollment failed.", FaceResult = enrollResult });
        }

        // persist file and DB record
        string savedFileName = null;
        try
        {
            var enrolledDir = Path.Combine("wwwroot", "enrolled"); Directory.CreateDirectory(enrolledDir);
            var safeName = MakeSafeName(dbUser.UserName ?? dbUser.UserId);
            var fileName = $"{userId}_{safeName}_f{nextIndex}.jpg";
            var filePath = Path.Combine(enrolledDir, fileName);
            await System.IO.File.WriteAllBytesAsync(filePath, photoBytes);
            savedFileName = Path.GetFileName(filePath);

            var faceEntity = new AttendanceUserFaceEntity { AttendanceUserId = dbUser.Id, UserId = dbUser.UserId, FaceIndex = nextIndex, PhotoFileName = savedFileName, PhotoLength = photoBytes.Length, CreatedAt = DateTime.Now, IsActive = true };
            await _db.AttendanceUserFaces.AddAsync(faceEntity);
            if (nextIndex == 1) { dbUser.PhotoFileName = savedFileName; dbUser.PhotoLength = photoBytes.Length; }
            dbUser.EnrollmentStatus = "FaceEnrolled"; dbUser.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            try { if (!string.IsNullOrEmpty(savedFileName)) System.IO.File.Delete(Path.Combine("wwwroot", "enrolled", savedFileName)); } catch { }
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to persist face record.", Error = ex.Message });
        }

        try { _live.TrainRecognizer(); } catch { }
        try { var live = _services.GetService(typeof(LiveFaceService)) as LiveFaceService; live?.TrainRecognizer(); } catch { }

        return Ok(new { Success = true, Message = "Face created.", FaceIndex = nextIndex, PhotoFileName = savedFileName });
    }

    [HttpPut("{userId}/faces/{faceIndex}")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = "Admin,Security")]
    public async Task<IActionResult> ReplaceUserFace([FromRoute] string userId, [FromRoute] int faceIndex, [FromForm] IFormFile? photo)
    {
        if (User.Identity != null && User.IsInRole("User"))
        {
            if (!await IsOwnerAsync(userId)) return Forbid();
        }
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest(new { Success = false, Message = "userId is required." });
        if (faceIndex != 1 && faceIndex != 2) return BadRequest(new { Success = false, Message = "faceIndex must be 1 or 2." });
        if (photo == null || photo.Length == 0) return BadRequest(new { Success = false, Message = "photo is required." });

        var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(u => u.UserId == userId);
        if (dbUser == null) return NotFound(new { Success = false, Message = "User not found." });

        var existingFace = await _db.AttendanceUserFaces.FirstOrDefaultAsync(f => f.AttendanceUserId == dbUser.Id && f.FaceIndex == faceIndex && f.IsActive);
        if (existingFace == null) return NotFound(new { Success = false, Message = "Face slot not found." });

        byte[] photoBytes; await using (var ms = new MemoryStream()) { await photo.CopyToAsync(ms); photoBytes = ms.ToArray(); }

        bool initialized = _sdk.Initialize(); if (!initialized) return Ok(new { Success = false, Message = "Dahua SDK initialization failed." });

        try { var cu = _sdk.CreateAccessControlUser(userId, dbUser.UserName ?? userId); if (!cu.Success && cu.FailCode != 52) return Ok(new { Success = false, Message = "Device user creation failed.", CreateUserResult = cu }); } catch (Exception ex) { return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Error ensuring device user exists.", Error = ex.Message }); }

        // Note: No safe device-side delete wrapper assumed. We will attempt enroll and then update DB. Device may contain duplicate templates until delete is implemented.
        FaceEnrollResult enrollResult = _sdk.EnrollFace(userId, photoBytes);
        if (!enrollResult.Success) return Ok(new { Success = false, Message = "Device enrollment failed.", FaceResult = enrollResult });

        string savedFileName = null;
        try
        {
            var enrolledDir = Path.Combine("wwwroot", "enrolled"); Directory.CreateDirectory(enrolledDir);
            var safeName = MakeSafeName(dbUser.UserName ?? dbUser.UserId);
            var fileName = $"{userId}_{safeName}_f{faceIndex}.jpg";
            var filePath = Path.Combine(enrolledDir, fileName);
            await System.IO.File.WriteAllBytesAsync(filePath, photoBytes);
            savedFileName = Path.GetFileName(filePath);

            existingFace.PhotoFileName = savedFileName;
            existingFace.PhotoLength = photoBytes.Length;
            existingFace.UpdatedAt = DateTime.Now;
            existingFace.IsActive = true;

            if (faceIndex == 1) { dbUser.PhotoFileName = savedFileName; dbUser.PhotoLength = photoBytes.Length; }
            dbUser.UpdatedAt = DateTime.Now;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            try { if (!string.IsNullOrEmpty(savedFileName)) System.IO.File.Delete(Path.Combine("wwwroot", "enrolled", savedFileName)); } catch { }
            return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to persist replacement face.", Error = ex.Message });
        }

        try { _live.TrainRecognizer(); } catch { }
        try { var liveSvc = _services.GetService(typeof(LiveFaceService)) as LiveFaceService; liveSvc?.TrainRecognizer(); } catch { }
        return Ok(new { Success = true, Message = "Face replaced.", FaceIndex = faceIndex, PhotoFileName = savedFileName });
    }

    [HttpDelete("{userId}/faces/{faceIndex}")]
    [Authorize(Roles = "Admin,Security")]
    public async Task<IActionResult> DeleteUserFace([FromRoute] string userId, [FromRoute] int faceIndex)
    {
        if (User.Identity != null && User.IsInRole("User"))
        {
            if (!await IsOwnerAsync(userId)) return Forbid();
        }
        if (string.IsNullOrWhiteSpace(userId)) return BadRequest(new { Success = false, Message = "userId is required." });
        if (faceIndex != 1 && faceIndex != 2) return BadRequest(new { Success = false, Message = "faceIndex must be 1 or 2." });

        var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(u => u.UserId == userId);
        if (dbUser == null) return NotFound(new { Success = false, Message = "User not found." });

        var face = await _db.AttendanceUserFaces.FirstOrDefaultAsync(f => f.AttendanceUserId == dbUser.Id && f.FaceIndex == faceIndex && f.IsActive);
        if (face == null) return NotFound(new { Success = false, Message = "Face not found." });

        // Device deletion not implemented in SDK wrapper; perform soft-delete and update legacy fields accordingly.
        face.IsActive = false;
        face.UpdatedAt = DateTime.Now;

        // If deleting Face 1 and Face 2 exists, promote Face 2 to legacy fields
        if (faceIndex == 1)
        {
            var other = await _db.AttendanceUserFaces.FirstOrDefaultAsync(f => f.AttendanceUserId == dbUser.Id && f.FaceIndex == 2 && f.IsActive);
            if (other != null && !string.IsNullOrEmpty(other.PhotoFileName))
            {
                dbUser.PhotoFileName = other.PhotoFileName;
                dbUser.PhotoLength = other.PhotoLength;
            }
            else
            {
                dbUser.PhotoFileName = null;
                dbUser.PhotoLength = 0;
            }
        }

        if (faceIndex == 2 && dbUser.PhotoFileName == face.PhotoFileName)
        {
            // If somehow legacy pointed to face 2, clear legacy
            dbUser.PhotoFileName = null;
            dbUser.PhotoLength = 0;
        }

        await _db.SaveChangesAsync();
        try { _live.TrainRecognizer(); } catch { }
        try { var live = _services.GetService(typeof(LiveFaceService)) as LiveFaceService; live?.TrainRecognizer(); } catch { }

        return Ok(new { Success = true, Message = "Face deleted (soft)" });
    }

    // Helper: choose lowest available face index (1 or 2). Returns -1 if full.
    private async Task<int> GetLowestAvailableFaceIndexAsync(int attendanceUserId)
    {
        var used = await _db.AttendanceUserFaces
            .Where(f => f.AttendanceUserId == attendanceUserId && f.IsActive)
            .Select(f => f.FaceIndex)
            .ToListAsync();

        if (!used.Contains(1)) return 1;
        if (!used.Contains(2)) return 2;
        return -1;
    }

    private static string MakeSafeName(string input)
    {
        var safe = string.Join("_", (input ?? "").Split(Path.GetInvalidFileNameChars()));
        // no-op placeholder to preserve formatting after prior edits
        return string.IsNullOrWhiteSpace(safe) ? "photo" : safe;
    }

    [HttpGet("init")]
    public IActionResult Init()
    {
        bool result = _sdk.Initialize();

        return Ok(new
        {
            Success = result,
            Message = result
                ? "SDK initialized successfully."
                : "SDK initialization failed."
        });
    }

    [HttpGet("login")]
    public IActionResult Login(
        [FromQuery] string ip,
        [FromQuery] int port = 37777,
        [FromQuery] string username = "admin",
        [FromQuery] string password = "admin123",
        [FromQuery] int channel = 0)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "Device IP is required."
            });
        }

        bool init = _sdk.Initialize();
        if (!init)
        {
            return Ok(new
            {
                Success = false,
                Message = "SDK Initialization Failed"
            });
        }

        bool login = _sdk.Login(ip, port, username, password, channel);

        return Ok(new
        {
            Success = login,
            Message = login
                ? "Device login successful."
                : "Device login failed.",
            IP = ip,
            Port = port
        });
    }

    [HttpGet("users")]
    public IActionResult GetUsers(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 10)
    {
        AttendanceUsersResult result = _sdk.FindAttendanceUsers(offset, count);
        return Ok(result);
    }

    [HttpGet("access-users")]
    public IActionResult GetAccessUsers(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 10)
    {
        AttendanceUsersResult result = _sdk.FindAccessControlUsers(offset, count);
        return Ok(result);
    }

    [HttpGet("attendance-logs")]
    public async Task<IActionResult> GetAttendanceLogs([FromQuery] int take = 100)
    {
        try
        {
            int limit = Math.Max(1, take);

            var logs = await _db.AttendanceLogs
                .OrderByDescending(x => x.RecordedAt)
                .Take(limit)
                .ToListAsync();

            return Ok(new
            {
                Success = true,
                TotalLogs = logs.Count,
                Logs = logs
            });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Could not retrieve attendance logs.",
                    Error = ex.Message
                });
        }
    }

    [HttpGet("sync-users")]
    [Authorize(Roles = "Admin,HR")]
    public async Task<IActionResult> SyncUsers(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        try
        {
            AttendanceUsersResult dahuaResult = _sdk.FindAccessControlUsers(offset, count);

            if (!dahuaResult.Success)
            {
                return Ok(new
                {
                    Success = false,
                    Message = "Could not retrieve users from Dahua device.",
                    DahuaMessage = dahuaResult.Message,
                    Inserted = 0,
                    Updated = 0,
                    TotalFromDevice = 0
                });
            }

            if (dahuaResult.Users == null || dahuaResult.Users.Count == 0)
            {
                return Ok(new
                {
                    Success = true,
                    Message = "No users found on Dahua device.",
                    Inserted = 0,
                    Updated = 0,
                    TotalFromDevice = 0
                });
            }

            int inserted = 0;
            int updated = 0;

            foreach (AttendanceUser user in dahuaResult.Users)
            {
                if (string.IsNullOrWhiteSpace(user.UserId))
                {
                    continue;
                }

                string userId = user.UserId.Trim();
                AttendanceUserEntity? existingUser = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userId);

                if (existingUser != null)
                {
                    existingUser.UserName = user.UserName;
                    existingUser.CardNo = user.CardNo;
                    existingUser.Password = user.Password;
                    existingUser.ClassNumber = user.ClassNumber;
                    existingUser.PhoneNumber = user.PhoneNumber;
                    existingUser.PhotoLength = user.PhotoLength;
                    existingUser.UpdatedAt = DateTime.Now;
                    updated++;
                }
                else
                {
                    var newUser = new AttendanceUserEntity
                    {
                        UserId = userId,
                        UserName = user.UserName,
                        CardNo = user.CardNo,
                        Password = user.Password,
                        ClassNumber = user.ClassNumber,
                        PhoneNumber = user.PhoneNumber,
                        PhotoLength = user.PhotoLength,
                        EnrollmentStatus = null,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = null
                    };

                    await _db.AttendanceUsers.AddAsync(newUser);
                    inserted++;
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                Success = true,
                Message = "Users synced successfully.",
                Inserted = inserted,
                Updated = updated,
                TotalFromDevice = dahuaResult.Users.Count
            });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Could not sync users.",
                    Error = ex.Message
                });
        }
    }

    [HttpGet("database-users")]
    public async Task<IActionResult> GetDatabaseUsers()
    {
        try
        {
            List<AttendanceUserEntity> users = await _db.AttendanceUsers.OrderBy(x => x.Id).ToListAsync();

            return Ok(new
            {
                Success = true,
                Message = "Database users retrieved successfully.",
                TotalUsers = users.Count,
                Users = users
            });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Could not retrieve users from database.",
                    Error = ex.Message
                });
        }
    }

    [HttpPost("face-collection/enable")]
    [Authorize(Roles = "Admin,Security")]
    public async Task<IActionResult> EnableFaceCollectionMode(
        [FromQuery] string userId,
        [FromQuery] bool enable = true)
    {
        string userIdTrimmed = (userId ?? "").Trim();
        _logger?.LogInformation("[EnableFaceCollectionMode] START userId={userId} enable={enable} at {time}", userIdTrimmed, enable, DateTime.UtcNow);
        Console.WriteLine($"[EnableFaceCollectionMode] START userId={userIdTrimmed} enable={enable} at {DateTime.UtcNow:o}");
        if (string.IsNullOrWhiteSpace(userIdTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "userId is required."
            });
        }

        try
        {
            _logger?.LogInformation("[EnableFaceCollectionMode] Looking up user {userId} in database at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] Looking up user {userIdTrimmed} in database at {DateTime.UtcNow:o}");
            var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userIdTrimmed);
            _logger?.LogInformation("[EnableFaceCollectionMode] After DB lookup user found={found} at {time}", dbUser != null, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] After DB lookup user found={(dbUser!=null)} at {DateTime.UtcNow:o}");
            if (dbUser == null)
            {
                _logger?.LogInformation("[EnableFaceCollectionMode] User not found {userId} returning 404 at {time}", userIdTrimmed, DateTime.UtcNow);
                return NotFound(new
                {
                    Success = false,
                    Message = "User not found in database.",
                    UserId = userIdTrimmed
                });
            }

            dbUser.EnrollmentStatus = "PendingFaceEnrollment";
            dbUser.UpdatedAt = DateTime.Now;
            _logger?.LogInformation("[EnableFaceCollectionMode] Setting EnrollmentStatus=PendingFaceEnrollment for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] Setting EnrollmentStatus=PendingFaceEnrollment for {userIdTrimmed} at {DateTime.UtcNow:o}");
            await _db.SaveChangesAsync();
            _logger?.LogInformation("[EnableFaceCollectionMode] After SaveChanges (Pending) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] After SaveChanges (Pending) for {userIdTrimmed} at {DateTime.UtcNow:o}");

            _logger?.LogInformation("[EnableFaceCollectionMode] Before checking SDK.IsLoggedIn for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] Before checking SDK.IsLoggedIn for {userIdTrimmed} at {DateTime.UtcNow:o}");
            bool isLoggedIn = _sdk.IsLoggedIn;
            _logger?.LogInformation("[EnableFaceCollectionMode] After checking SDK.IsLoggedIn={isLoggedIn} for {userId} at {time}", isLoggedIn, userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] After checking SDK.IsLoggedIn={isLoggedIn} for {userIdTrimmed} at {DateTime.UtcNow:o}");

            if (!isLoggedIn)
            {
                _logger?.LogWarning("[EnableFaceCollectionMode] SDK not logged in for {userId} - marking failed and returning 503 at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnableFaceCollectionMode] SDK not logged in for {userIdTrimmed} - marking failed and returning 503 at {DateTime.UtcNow:o}");
                try
                {
                    dbUser.EnrollmentStatus = "FaceEnrollmentFailed";
                    dbUser.UpdatedAt = DateTime.Now;
                    _logger?.LogInformation("[EnableFaceCollectionMode] Setting EnrollmentStatus=FaceEnrollmentFailed for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                    Console.WriteLine($"[EnableFaceCollectionMode] Setting EnrollmentStatus=FaceEnrollmentFailed for {userIdTrimmed} at {DateTime.UtcNow:o}");
                    await _db.SaveChangesAsync();
                    _logger?.LogInformation("[EnableFaceCollectionMode] After SaveChanges (Failed-login) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                    Console.WriteLine($"[EnableFaceCollectionMode] After SaveChanges (Failed-login) for {userIdTrimmed} at {DateTime.UtcNow:o}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[EnableFaceCollectionMode] Error saving failed status for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                }

                string msg = $"Face collection failed for user {userIdTrimmed}. Device is not logged in to Dahua SDK.";
                _logger?.LogInformation("[EnableFaceCollectionMode] Returning 503 for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnableFaceCollectionMode] Returning 503 for {userIdTrimmed} at {DateTime.UtcNow:o}");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Success = false,
                    Message = msg,
                    UserId = userIdTrimmed,
                    EnrollmentStatus = dbUser.EnrollmentStatus
                });
            }

            _logger?.LogInformation("[EnableFaceCollectionMode] Before calling CaptureAccessPersonFaceCollection for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] Before calling CaptureAccessPersonFaceCollection for {userIdTrimmed} at {DateTime.UtcNow:o}");
            DahuaSdkService.CaptureAccessResult captureResult;
            try
            {
                captureResult = _sdk.CaptureAccessPersonFaceCollection(userIdTrimmed, enable);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[EnableFaceCollectionMode] Exception thrown by CaptureAccessPersonFaceCollection for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnableFaceCollectionMode] Exception thrown by CaptureAccessPersonFaceCollection for {userIdTrimmed} at {DateTime.UtcNow:o}: {ex.Message}");
                try
                {
                    dbUser.EnrollmentStatus = "FaceEnrollmentFailed";
                    dbUser.UpdatedAt = DateTime.Now;
                    await _db.SaveChangesAsync();
                }
                catch (Exception saveEx)
                {
                    _logger?.LogError(saveEx, "[EnableFaceCollectionMode] Error saving failed status after SDK exception for {userId}", userIdTrimmed);
                }

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Success = false,
                    Message = "Face collection failed due to SDK exception.",
                    Error = ex.Message,
                    UserId = userIdTrimmed
                });
            }

            _logger?.LogInformation("[EnableFaceCollectionMode] After CaptureAccessPersonFaceCollection for {userId} result.Success={success} sdkError={sdkError} at {time}", captureResult.Success, captureResult.SdkError, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] After CaptureAccessPersonFaceCollection for {userIdTrimmed} result.Success={captureResult.Success} sdkError={captureResult.SdkError} at {DateTime.UtcNow:o}");

            if (!captureResult.Success)
            {
                _logger?.LogWarning("[EnableFaceCollectionMode] Capture returned failure for {userId} sdkError={sdkError} - marking failed at {time}", userIdTrimmed, captureResult.SdkError, DateTime.UtcNow);
                Console.WriteLine($"[EnableFaceCollectionMode] Capture returned failure for {userIdTrimmed} sdkError={captureResult.SdkError} - marking failed at {DateTime.UtcNow:o}");
                try
                {
                    dbUser.EnrollmentStatus = "FaceEnrollmentFailed";
                    dbUser.UpdatedAt = DateTime.Now;
                    _logger?.LogInformation("[EnableFaceCollectionMode] Setting EnrollmentStatus=FaceEnrollmentFailed for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                    Console.WriteLine($"[EnableFaceCollectionMode] Setting EnrollmentStatus=FaceEnrollmentFailed for {userIdTrimmed} at {DateTime.UtcNow:o}");
                    await _db.SaveChangesAsync();
                    _logger?.LogInformation("[EnableFaceCollectionMode] After SaveChanges (Failed-sdk) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                    Console.WriteLine($"[EnableFaceCollectionMode] After SaveChanges (Failed-sdk) for {userIdTrimmed} at {DateTime.UtcNow:o}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[EnableFaceCollectionMode] Error saving FaceEnrollmentFailed for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                }

                string msg = $"Face collection failed for user {userIdTrimmed}. Dahua SDK error: {captureResult.SdkError} (0x{captureResult.SdkError:X8}).";
                _logger?.LogInformation("[EnableFaceCollectionMode] Returning 502 for {userId} with sdkError={sdkError} at {time}", userIdTrimmed, captureResult.SdkError, DateTime.UtcNow);
                Console.WriteLine($"[EnableFaceCollectionMode] Returning 502 for {userIdTrimmed} with sdkError={captureResult.SdkError} at {DateTime.UtcNow:o}");

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    Success = false,
                    Message = msg,
                    SdkError = captureResult.SdkError,
                    SdkErrorHex = $"0x{captureResult.SdkError:X8}",
                    SdkErrorDescription = _sdk.GetSdkErrorDescription(captureResult.SdkError),
                    UserId = userIdTrimmed,
                    EnrollmentStatus = dbUser.EnrollmentStatus
                });
            }

            _logger?.LogInformation("[EnableFaceCollectionMode] Capture succeeded for {userId} - returning 200 at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnableFaceCollectionMode] Capture succeeded for {userIdTrimmed} - returning 200 at {DateTime.UtcNow:o}");

            return Ok(new
            {
                Success = true,
                Enabled = true,
                UserId = userIdTrimmed,
                EnrollmentStatus = dbUser.EnrollmentStatus,
                Message = "Dahua device face collection mode enabled. Use the device terminal to select the user and scan the face."
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[EnableFaceCollectionMode] Unexpected exception for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Failed to enable face collection mode.",
                    Error = ex.Message
                });
        }
    }

    [HttpGet("user-status")]
    public async Task<IActionResult> GetUserStatus([FromQuery] string userId)
    {
        string userIdTrimmed = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userIdTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "userId is required."
            });
        }

        try
        {
            var user = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userIdTrimmed);
            if (user == null)
            {
                return NotFound(new
                {
                    Success = false,
                    Message = "User not found in database.",
                    UserId = userIdTrimmed
                });
            }

            return Ok(new
            {
                Success = true,
                UserId = user.UserId,
                UserName = user.UserName,
                EnrollmentStatus = user.EnrollmentStatus,
                UpdatedAt = user.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Could not retrieve user status.",
                    Error = ex.Message
                });
        }
    }

    [HttpPost("register")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Register(
        [FromForm] string? userID,
        [FromForm] string? name,
        [FromForm] IFormFile? photo,
        // Optional new registration fields
        [FromForm] int? department,
        [FromForm] string? scheduleMode,
        [FromForm] string? validFrom,
        [FromForm] string? validTo,
        [FromForm] string? permission,
        [FromForm] string? userType,
        [FromForm] string? timesUsed,
        [FromForm] int? period,
        [FromForm] int? holidayPlan,
        // Optional application account
        [FromForm] string? appUserName,
        [FromForm] string? appPassword,
        [FromForm] string? appRole)
    {
        string userIdTrimmed = (userID ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userIdTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "userID is required."
            });
        }

        string nameTrimmed = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nameTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "name is required."
            });
        }

        const long maxFileSize = 15 * 1024 * 1024;
        if (photo != null && photo.Length > maxFileSize)
        {
            return BadRequest(new
            {
                Success = false,
                Message = "Photo is too large. Maximum allowed size is 15 MB."
            });
        }

        try
        {
            var existing = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userIdTrimmed);
            if (existing != null)
            {
                // User already exists: return explicit conflict and do not call device APIs or modify DB
                return Conflict(new
                {
                    success = false,
                    message = "User already registered",
                    userId = userIdTrimmed
                });
            }

            // If a photo is supplied, perform a server-side duplicate-face check before creating any DB row
            byte[]? photoBytes = null;
            if (photo != null && photo.Length > 0)
            {
                try
                {
                    await using var ms = new MemoryStream();
                    await photo.CopyToAsync(ms);
                    photoBytes = ms.ToArray();

                    // TryDetectExistingFace will return true if the supplied photo matches an existing enrolled user
                    // FIX: previous version had a malformed / duplicated merge of two implementations
                    // (an `if (_live != null)` with no braces followed by a stray declaration, plus a
                    // duplicate `_services.GetService(...)` resolution). That structural break is what
                    // caused the C# parser to desync and cascade hundreds of errors through the rest of
                    // the file. Restored to a single, correct duplicate-face check using the injected _live field.
                    if (_live != null && _live.TryDetectExistingFace(photoBytes, null, out var matchedUserId, out var confidence))
                    {
                        return Conflict(new
                        {
                            success = false,
                            message = "Face already belongs to another registered user",
                            existingUserId = matchedUserId
                        });
                    }

                    // Fallback: if server recognizer didn't match, attempt exact-file byte comparison
                    // This catches the case where the same JPEG file is being reused but LBPH didn't match.
                    try
                    {
                        var enrolledDir = Path.Combine("wwwroot", "enrolled");
                        if (Directory.Exists(enrolledDir))
                        {
                            foreach (var f in Directory.GetFiles(enrolledDir, "*.jpg"))
                            {
                                try
                                {
                                    var existingBytes = await System.IO.File.ReadAllBytesAsync(f);
                                    if (existingBytes.Length == photoBytes.Length)
                                    {
                                        bool same = true;
                                        for (int i = 0; i < existingBytes.Length; i++)
                                        {
                                            if (existingBytes[i] != photoBytes[i])
                                            {
                                                same = false;
                                                break;
                                            }
                                        }

                                        if (same)
                                        {
                                            var baseName = Path.GetFileNameWithoutExtension(f);
                                            var parts = baseName.Split('_');
                                            var existingId = parts.Length > 0 ? parts[0] : baseName;
                                            return Conflict(new
                                            {
                                                success = false,
                                                message = "Face already belongs to another registered user",
                                                existingUserId = existingId
                                            });
                                        }
                                    }
                                }
                                catch { /* ignore file read errors */ }
                            }
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    // If face-check fails unexpectedly, log and continue with registration as a fallback
                    Console.WriteLine($"[Register] Warning: server-side duplicate-face check failed: {ex.Message}");
                }
            }
            else
            {
                // No uploaded photo: attempt to wait briefly for a fresh Dahua device recognition event
                try
                {
                    // Only attempt if SDK is available and likely logged-in
                    if (_sdk != null && _sdk.IsLoggedIn)
                    {
                        var startTime = DateTime.UtcNow;
                        var tcs = new System.Threading.Tasks.TaskCompletionSource<Services.DahuaSdkService.DeviceRecognitionEvent?>(
                            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

                        void Handler(Services.DahuaSdkService.DeviceRecognitionEvent ev)
                        {
                            try
                            {
                                if (ev == null) return;
                                // Only accept events that occur after registration started
                                if (ev.Timestamp >= startTime)
                                {
                                    tcs.TrySetResult(ev);
                                }
                            }
                            catch { }
                        }

                        _sdk.OnDeviceRecognition += Handler;
                        try
                        {
                            var waitMs = 5000; // configurable small timeout (5s)
                            var task = await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(waitMs));
                            if (task == tcs.Task)
                            {
                                var ev = await tcs.Task;
                                if (ev != null && !string.IsNullOrWhiteSpace(ev.UserId))
                                {
                                    // If the device recognized a different existing user, reject
                                    if (!string.Equals(ev.UserId, userIdTrimmed, StringComparison.OrdinalIgnoreCase))
                                    {
                                        // Check DB for that existing user
                                        var existingByFace = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == ev.UserId);
                                        if (existingByFace != null)
                                        {
                                            return Conflict(new
                                            {
                                                success = false,
                                                message = "Face already belongs to another registered user",
                                                existingUserId = ev.UserId
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _sdk.OnDeviceRecognition -= Handler;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Register] Warning: device-based duplicate-face check failed: {ex.Message}");
                }
            }

            // Validate optional new fields
            if (department.HasValue)
            {
                if (department < 1 || department > 20)
                    return BadRequest(new { Success = false, Message = "department must be between 1 and 20." });
            }

            if (!string.IsNullOrWhiteSpace(scheduleMode))
            {
                var sm = scheduleMode.Trim();
                if (!string.Equals(sm, "Department", StringComparison.OrdinalIgnoreCase) && !string.Equals(sm, "Personal", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { Success = false, Message = "scheduleMode must be 'Department' or 'Personal'." });
            }

            DateTime? parsedValidFrom = null;
            DateTime? parsedValidTo = null;
            if (!string.IsNullOrWhiteSpace(validFrom))
            {
                if (!DateTime.TryParse(validFrom, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var vf))
                    return BadRequest(new { Success = false, Message = "validFrom must be a valid ISO datetime." });
                parsedValidFrom = vf.ToUniversalTime();
            }
            if (!string.IsNullOrWhiteSpace(validTo))
            {
                if (!DateTime.TryParse(validTo, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var vt))
                    return BadRequest(new { Success = false, Message = "validTo must be a valid ISO datetime." });
                parsedValidTo = vt.ToUniversalTime();
            }
            if (parsedValidFrom.HasValue && parsedValidTo.HasValue && parsedValidFrom.Value > parsedValidTo.Value)
            {
                return BadRequest(new { Success = false, Message = "validFrom must be less than or equal to validTo." });
            }

            if (!string.IsNullOrWhiteSpace(permission))
            {
                var p = permission.Trim();
                if (!string.Equals(p, "User", StringComparison.OrdinalIgnoreCase) && !string.Equals(p, "Admin", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { Success = false, Message = "permission must be 'User' or 'Admin'." });
            }

            if (!string.IsNullOrWhiteSpace(userType))
            {
                var allowedTypes = new[] { "General User", "VIP User", "Guest User", "Patrol User", "Blocklist User", "Other User", "Custom User 1", "Custom User 2" };
                if (!allowedTypes.Contains(userType.Trim(), StringComparer.OrdinalIgnoreCase))
                    return BadRequest(new { Success = false, Message = "userType is invalid." });
            }

            if (period.HasValue)
            {
                if (period < 0 || period > 255)
                    return BadRequest(new { Success = false, Message = "period must be between 0 and 255." });
            }

            if (holidayPlan.HasValue)
            {
                if (holidayPlan < 0 || holidayPlan > 255)
                    return BadRequest(new { Success = false, Message = "holidayPlan must be between 0 and 255." });
            }

            // New user path
            string initialStatus = (photo == null || photo.Length == 0)
                ? "PendingFaceEnrollment"
                : "EnrollmentInProgress";

            var newUser = new AttendanceUserEntity
            {
                UserId = userIdTrimmed,
                UserName = nameTrimmed,
                PhotoLength = (int)(photo?.Length ?? 0),
                EnrollmentStatus = initialStatus,
                CreatedAt = DateTime.Now,
                UpdatedAt = null
            };

            await _db.AttendanceUsers.AddAsync(newUser);
            await _db.SaveChangesAsync();

            // If application credentials provided, create ApplicationUser and link
            if (!string.IsNullOrWhiteSpace(appUserName) && !string.IsNullOrWhiteSpace(appPassword))
            {
                var appUserTrim = appUserName.Trim();
                // username uniqueness
                var existingApp = await _db.ApplicationUsers.FirstOrDefaultAsync(a => a.UserName == appUserTrim);
                if (existingApp != null)
                {
                    return Conflict(new { Success = false, Message = "Application userName already taken." });
                }

                // Role handling: default to User unless caller is Admin and requested Admin
                var assignRole = string.IsNullOrWhiteSpace(appRole) ? "User" : appRole.Trim();
                if (string.Equals(assignRole, "Admin", StringComparison.OrdinalIgnoreCase))
                {
                    if (User?.Identity == null || !User.IsInRole("Admin"))
                    {
                        return Forbid();
                    }
                }

                var appEntity = new ApplicationUserEntity
                {
                    UserName = appUserTrim,
                    Role = assignRole,
                    AttendanceUserId = newUser.Id,
                    CreatedAt = DateTime.UtcNow
                };
                var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<ApplicationUserEntity>();
                appEntity.PasswordHash = hasher.HashPassword(appEntity, appPassword);
                await _db.ApplicationUsers.AddAsync(appEntity);
                await _db.SaveChangesAsync();
            }

            bool hasPhoto = photo is { Length: > 0 };

            bool initialized = _sdk.Initialize();
            if (!initialized)
            {
                await SetEnrollmentStatus(userIdTrimmed, "DeviceUserCreationFailed");
                return Ok(new
                {
                    Success = false,
                    Message = "Dahua SDK initialization failed. User saved with DeviceUserCreationFailed status.",
                    UserId = userIdTrimmed
                });
            }

            // Build device options for SDK call. Only safely map fields where SDK representation is clear.
            var opts = new DahuaAttendanceAPI.Services.DahuaSdkService.CreateAccessControlUserOptions
            {
                Department = department,
                Period = period,
                HolidayPlan = holidayPlan,
                ValidFrom = parsedValidFrom,
                ValidTo = parsedValidTo,
                ScheduleMode = scheduleMode,
                Permission = permission,
                UserType = userType,
                TimesUsed = timesUsed
            };

            CreateUserResult createUserResult = _sdk.CreateAccessControlUser(userIdTrimmed, nameTrimmed, opts);
            if (!createUserResult.Success && createUserResult.FailCode != 52)
            {
                await SetEnrollmentStatus(userIdTrimmed, "DeviceUserCreationFailed");
                return Ok(new
                {
                    Success = false,
                    Message = "User saved but device user creation failed.",
                    CreateUserResult = createUserResult,
                    UserId = userIdTrimmed
                });
            }

            if (!hasPhoto)
            {
                await SetEnrollmentStatus(userIdTrimmed, "PendingFaceEnrollment");
                return Ok(new
                {
                    Success = true,
                    Message = "User registered and device user created. Face enrollment is pending. Use POST /api/Attendance/enroll-face with a JPEG photo or use the device collection workflow.",
                    UserId = userIdTrimmed,
                    Name = nameTrimmed,
                    EnrollmentStatus = "PendingFaceEnrollment",
                    DeviceUserCreated = true
                });
            }

            if (photoBytes == null)
            {
                await using (var stream = new MemoryStream())
                {
                    await photo!.CopyToAsync(stream);
                    photoBytes = stream.ToArray();
                }
            }

            FaceEnrollResult faceResult = _sdk.EnrollFace(userIdTrimmed, photoBytes);
            var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userIdTrimmed);

            if (faceResult.Success)
            {
                string? savedFileName = null;
                int assignedFaceIndex = -1;

                try
                {
                    if (dbUser == null)
                    {
                        // Defensive: should not happen since we just created the user
                        return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "User not found after creation." });
                    }

                    // Determine next available face slot
                    assignedFaceIndex = await GetLowestAvailableFaceIndexAsync(dbUser.Id);
                    if (assignedFaceIndex == -1)
                    {
                        return Conflict(new { Success = false, Message = "User already has maximum allowed faces (2). Use update/replace to change a slot or delete one first." });
                    }

                    var enrolledDir = Path.Combine("wwwroot", "enrolled");
                    Directory.CreateDirectory(enrolledDir);
                    var safeName = MakeSafeName(nameTrimmed);
                    var fileName = $"{userIdTrimmed}_{safeName}_f{assignedFaceIndex}.jpg";
                    var filePath = Path.Combine(enrolledDir, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, photoBytes);
                    savedFileName = Path.GetFileName(filePath);

                    // Insert AttendanceUserFace record
                    var faceEntity = new AttendanceUserFaceEntity
                    {
                        AttendanceUserId = dbUser.Id,
                        UserId = dbUser.UserId,
                        FaceIndex = assignedFaceIndex,
                        PhotoFileName = savedFileName,
                        PhotoLength = photoBytes.Length,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = null,
                        IsActive = true
                    };

                    await _db.AttendanceUserFaces.AddAsync(faceEntity);

                    // Legacy compatibility: update AttendanceUsers.PhotoFileName only for FaceIndex == 1
                    if (assignedFaceIndex == 1)
                    {
                        dbUser.PhotoFileName = savedFileName;
                        dbUser.PhotoLength = photoBytes.Length;
                    }

                    dbUser.EnrollmentStatus = "FaceEnrolled";
                    dbUser.UpdatedAt = DateTime.Now;

                    await _db.SaveChangesAsync();
                }
                catch (Exception saveEx)
                {
                    // If file was written but DB failed, attempt to remove file and report error
                    try { if (savedFileName != null) System.IO.File.Delete(Path.Combine("wwwroot", "enrolled", savedFileName)); } catch { }
                    Console.WriteLine($"[Register] Error saving face record for {userIdTrimmed}: {saveEx.Message}");
                    return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to persist face record.", Error = saveEx.Message });
                }

                try
                {
                    _live.TrainRecognizer();
                    var liveSvc = _services.GetService(typeof(LiveFaceService)) as LiveFaceService;
                    liveSvc?.TrainRecognizer();
                }
                catch (Exception trainEx)
                {
                    Console.WriteLine($"[Register] Warning: LBPH retrain failed: {trainEx.Message}");
                }

                return Ok(new
                {
                    Success = true,
                    Message = "User registered and face enrolled successfully.",
                    UserId = userIdTrimmed,
                    Name = nameTrimmed,
                    EnrollmentStatus = "FaceEnrolled",
                    PhotoFileName = savedFileName,
                    FaceIndex = assignedFaceIndex,
                    CreateUserResult = createUserResult,
                    FaceResult = faceResult
                });
            }

            if (faceResult.FailCode == 24)
            {
                // Device indicates the photo already exists. Reconcile DB: if
                // the device face belongs to this user but the AttendanceUserFaces
                // record is missing, create it (face record) using the correct
                // AttendanceUserId FK. Do NOT invent a photo file name if none
                // is available from the server-side JPEG.
                if (dbUser != null)
                {
                    // Mark enrollment status optimistically
                    dbUser.EnrollmentStatus = "FaceEnrolled";
                    dbUser.UpdatedAt = DateTime.Now;

                    // If there's no face record for this user, create a reconciled row
                    var existingFace1 = await _db.AttendanceUserFaces.FirstOrDefaultAsync(f => f.AttendanceUserId == dbUser.Id && f.FaceIndex == 1 && f.IsActive);
                    if (existingFace1 == null)
                    {
                        var reconciled = new AttendanceUserFaceEntity
                        {
                            AttendanceUserId = dbUser.Id,
                            UserId = dbUser.UserId,
                            FaceIndex = 1,
                            PhotoFileName = dbUser.PhotoFileName, // may be null
                            PhotoLength = dbUser.PhotoLength,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = null,
                            IsActive = true
                        };

                        await _db.AttendanceUserFaces.AddAsync(reconciled);
                    }

                    await _db.SaveChangesAsync();
                }

                return Ok(new
                {
                    Success = false,
                    AlreadyEnrolled = true,
                    Message = "The supplied face photo is already enrolled on the Dahua device (fail code 24: Photo already exists). Database reconciled if a record was missing.",
                    UserId = userIdTrimmed,
                    EnrollmentStatus = "FaceEnrolled",
                    FailCode = faceResult.FailCode,
                    SdkError = faceResult.SdkError
                });
            }

            if (faceResult.FailCode == 28)
            {
                if (dbUser != null)
                {
                    dbUser.EnrollmentStatus = "DuplicateFace";
                    dbUser.UpdatedAt = DateTime.Now;
                    await _db.SaveChangesAsync();
                }

                return Ok(new
                {
                    Success = false,
                    DuplicateFaceDetected = true,
                    Message = "The supplied face is already enrolled on this device. The Dahua SDK confirms the face feature exists (fail code 28) but does not identify which user owns it.",
                    UserId = userIdTrimmed,
                    EnrollmentStatus = "DuplicateFace",
                    FailCode = faceResult.FailCode,
                    SdkError = faceResult.SdkError
                });
            }

            if (dbUser != null)
            {
                dbUser.EnrollmentStatus = "FaceEnrollmentFailed";
                dbUser.UpdatedAt = DateTime.Now;
                await _db.SaveChangesAsync();
            }

            return Ok(new
            {
                Success = false,
                Message = "User saved and device user created, but face enrollment failed.",
                UserId = userIdTrimmed,
                EnrollmentStatus = "FaceEnrollmentFailed",
                FaceResult = faceResult
            });
        }
        catch (Exception ex)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "User registration failed.",
                    Error = ex.Message
                });
        }
    }

    [HttpPost("enroll-face")]
    [Consumes("multipart/form-data")]
    [Authorize(Roles = "Admin,Security")]
    public async Task<IActionResult> EnrollFace(
        [FromForm] string? userID,
        [FromForm] IFormFile? photo)
    {
        string userIdTrimmed = (userID ?? "").Trim();
        _logger?.LogInformation("[EnrollFace] START userId={userId} at {time}", userIdTrimmed, DateTime.UtcNow);
        Console.WriteLine($"[EnrollFace] START userId={userIdTrimmed} at {DateTime.UtcNow:o}");
        if (string.IsNullOrWhiteSpace(userIdTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "userID is required."
            });
        }

        if (photo == null || photo.Length == 0)
        {
            return BadRequest(new
            {
                Success = false,
                Message = "photo is required."
            });
        }

        try
        {
            _logger?.LogInformation("[EnrollFace] Before DB lookup userId={userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] Before DB lookup userId={userIdTrimmed} at {DateTime.UtcNow:o}");
            var dbUser = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userIdTrimmed);
            _logger?.LogInformation("[EnrollFace] After DB lookup found={found} at {time}", dbUser != null, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] After DB lookup found={(dbUser!=null)} at {DateTime.UtcNow:o}");
            if (dbUser == null)
            {
                return NotFound(new
                {
                    Success = false,
                    Message = "User not found in database."
                });
            }

            // NOTE: Do not short-circuit here. Allow the enrollment flow to attempt
            // device-side creation/enrollment even if the DB marks the user as
            // FaceEnrolled. The device may be missing a corresponding access user
            // record and attempting enrollment will surface the precise SDK result.
            // Previously this returned 409 and prevented any SDK diagnostic being
            // emitted.

            byte[] photoBytes;
            await using (var stream = new MemoryStream())
            {
                _logger?.LogInformation("[EnrollFace] Before photo.CopyToAsync for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] Before photo.CopyToAsync for {userIdTrimmed} at {DateTime.UtcNow:o}");
                await photo.CopyToAsync(stream);
                _logger?.LogInformation("[EnrollFace] After photo.CopyToAsync for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] After photo.CopyToAsync for {userIdTrimmed} at {DateTime.UtcNow:o}");
                photoBytes = stream.ToArray();
            }

            _logger?.LogInformation("[EnrollFace] Before SDK/login operation for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] Before SDK/login operation for {userIdTrimmed} at {DateTime.UtcNow:o}");
            bool initialized = _sdk.Initialize();
            _logger?.LogInformation("[EnrollFace] After SDK/login operation initialized={inited} for {userId} at {time}", initialized, userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] After SDK/login operation initialized={initialized} for {userIdTrimmed} at {DateTime.UtcNow:o}");
            if (!initialized)
            {
                _logger?.LogInformation("[EnrollFace] RETURN 400 Dahua SDK initialization failed for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] RETURN 400 Dahua SDK initialization failed for {userIdTrimmed} at {DateTime.UtcNow:o}");
                return Ok(new
                {
                    Success = false,
                    Message = "Dahua SDK initialization failed."
                });
            }

            _logger?.LogInformation("[EnrollFace] Before face enrollment/capture operation for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] Before face enrollment/capture operation for {userIdTrimmed} at {DateTime.UtcNow:o}");

            // Diagnostic: print key values before attempting device user creation / enrollment.
            try
            {
                Console.WriteLine("[Dahua ENROLL TEST] UserId={0}", userIdTrimmed);
                Console.WriteLine("[Dahua ENROLL TEST] photoBytes.Length={0}", photoBytes.Length);
                Console.WriteLine("[Dahua ENROLL TEST] IsLoggedIn={0}", _sdk.IsLoggedIn);
                try
                {
                    // NET_ACCESS_FACE_INFO size (managed)
                    int faceStructSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(DahuaAttendanceAPI.Services.NET_ACCESS_FACE_INFO));
                    Console.WriteLine("[Dahua ENROLL TEST] NET_ACCESS_FACE_INFO size={0}", faceStructSize);
                }
                catch (Exception) { }

                // Show the exact 32-byte user id buffer that will be sent (ASCII, NUL-padded)
                try
                {
                    byte[] uidBytes = System.Text.Encoding.ASCII.GetBytes(userIdTrimmed ?? string.Empty);
                    byte[] uidFixed = new byte[32];
                    Array.Clear(uidFixed, 0, uidFixed.Length);
                    Array.Copy(uidBytes, uidFixed, Math.Min(uidBytes.Length, uidFixed.Length - 1));
                    Console.WriteLine("[Dahua ENROLL TEST] szUserID (ASCII, padded)='{0}'", System.Text.Encoding.ASCII.GetString(uidFixed).TrimEnd('\0'));
                    Console.WriteLine("[Dahua ENROLL TEST] szUserID (hex)={0}", BitConverter.ToString(uidFixed));
                }
                catch (Exception) { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EnrollFace DIAG] Pre-enroll diagnostics failed: {ex.Message}");
            }

            // Ensure the user exists on the Dahua device before attempting face enrollment.
            try
            {
                CreateUserResult createUserResult = _sdk.CreateAccessControlUser(userIdTrimmed, dbUser.UserName ?? userIdTrimmed);
                // FailCode 52 indicates the user already exists on the device; treat that as non-fatal for our purposes.
                if (!createUserResult.Success && createUserResult.FailCode != 52)
                {
                    _logger?.LogWarning("[EnrollFace] Device user creation failed for {userId} failCode={fail} sdkError={err}", userIdTrimmed, createUserResult.FailCode, createUserResult.SdkError);
                    Console.WriteLine($"[EnrollFace] Device user creation failed for {userIdTrimmed} failCode={createUserResult.FailCode} sdkError={createUserResult.SdkError} at {DateTime.UtcNow:o}");

                    return Ok(new
                    {
                        Success = false,
                        Message = "Device user creation failed. Face enrollment aborted.",
                        CreateUserResult = createUserResult,
                        UserId = userIdTrimmed
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[EnrollFace] Exception while ensuring device user exists for {userId}", userIdTrimmed);
                Console.WriteLine($"[EnrollFace] Exception while ensuring device user exists for {userIdTrimmed}: {ex.Message}");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Error while ensuring device user exists.", Error = ex.Message });
            }

            // Diagnostic: call the SDK and capture runtime diagnostics immediately before/after.
            Console.WriteLine("[Dahua ENROLL TEST] Calling EnrollFace SDK now...");
            FaceEnrollResult result = _sdk.EnrollFace(userIdTrimmed, photoBytes);
            Console.WriteLine("[Dahua ENROLL TEST] SDK returned: Success={0} FailCode={1} SdkError={2}", result.Success, result.FailCode, result.SdkError);
            _logger?.LogInformation("[EnrollFace] After face enrollment/capture operation for {userId} success={success} failCode={fail} sdkError={err} at {time}", userIdTrimmed, result.Success, result.FailCode, result.SdkError, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] After face enrollment/capture operation for {userIdTrimmed} success={result.Success} failCode={result.FailCode} sdkError={result.SdkError} at {DateTime.UtcNow:o}");

            Console.WriteLine(
                $"[EnrollFace DIAG] success={result.Success} " +
                $"failCode={result.FailCode} " +
                $"sdkError={result.SdkError} " +
                $"message='{result.Message}'");

            if (result.Success)
            {
                // Persist face as AttendanceUserFace
                int assignedFaceIndex = -1;
                string? savedFileName = null;

                try
                {
                    assignedFaceIndex = await GetLowestAvailableFaceIndexAsync(dbUser.Id);
                    if (assignedFaceIndex == -1)
                    {
                        return Conflict(new { Success = false, Message = "User already has maximum allowed faces (2). Use update/replace to change a slot or delete one first." });
                    }

                    var enrolledDir = Path.Combine("wwwroot", "enrolled");
                    Directory.CreateDirectory(enrolledDir);
                    var safeName = MakeSafeName(dbUser.UserName ?? dbUser.UserId);
                    var fileName = $"{userIdTrimmed}_{safeName}_f{assignedFaceIndex}.jpg";
                    var filePath = Path.Combine(enrolledDir, fileName);

                    await System.IO.File.WriteAllBytesAsync(filePath, photoBytes);
                    savedFileName = Path.GetFileName(filePath);

                    var faceEntity = new AttendanceUserFaceEntity
                    {
                        AttendanceUserId = dbUser.Id,
                        UserId = dbUser.UserId,
                        FaceIndex = assignedFaceIndex,
                        PhotoFileName = savedFileName,
                        PhotoLength = photoBytes.Length,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = null,
                        IsActive = true
                    };

                    await _db.AttendanceUserFaces.AddAsync(faceEntity);

                    if (assignedFaceIndex == 1)
                    {
                        dbUser.PhotoFileName = savedFileName;
                        dbUser.PhotoLength = photoBytes.Length;
                    }

                    dbUser.EnrollmentStatus = "FaceEnrolled";
                    dbUser.UpdatedAt = DateTime.Now;

                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    try { if (savedFileName != null) System.IO.File.Delete(Path.Combine("wwwroot", "enrolled", savedFileName)); } catch { }
                    Console.WriteLine($"[EnrollFace] Failed to persist face record: {ex.Message}");
                    return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to persist face record.", Error = ex.Message });
                }

                try
                {
                    _live.TrainRecognizer();
                    var liveSvc = _services.GetService(typeof(LiveFaceService)) as LiveFaceService;
                    liveSvc?.TrainRecognizer();
                }
                catch (Exception trainEx)
                {
                    Console.WriteLine($"[EnrollFace] Warning: LBPH retrain failed: {trainEx.Message}");
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Face enrolled successfully.",
                    UserId = userIdTrimmed,
                    EnrollmentStatus = "FaceEnrolled",
                    FaceResult = result,
                    FaceIndex = assignedFaceIndex,
                    PhotoFileName = savedFileName
                });
            }

            if (result.FailCode == 24)
            {
                // Device reports the photo already exists. Reconcile DB by
                // creating the missing AttendanceUserFaces row using the exact
                // uploaded JPEG bytes provided in this request. Do not attempt
                // to re-enroll or delete the device-side face template.
                dbUser.EnrollmentStatus = "FaceEnrolled";
                dbUser.UpdatedAt = DateTime.Now;
                _logger?.LogInformation("[EnrollFace] Before SaveChanges (AlreadyEnrolled) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] Before SaveChanges (AlreadyEnrolled) for {userIdTrimmed} at {DateTime.UtcNow:o}");

                var existingFace = await _db.AttendanceUserFaces.FirstOrDefaultAsync(f => f.AttendanceUserId == dbUser.Id && f.FaceIndex == 1 && f.IsActive);
                string? savedFileName = null;

                if (existingFace == null)
                {
                    if (photoBytes == null || photoBytes.Length == 0)
                    {
                        // Defensive: we expected photoBytes to be present for this flow.
                        return StatusCode(StatusCodes.Status400BadRequest, new { Success = false, Message = "No photo bytes available to reconcile existing device face." });
                    }

                    try
                    {
                        var enrolledDir = Path.Combine("wwwroot", "enrolled");
                        Directory.CreateDirectory(enrolledDir);
                        var safeName = MakeSafeName(dbUser.UserName ?? dbUser.UserId);
                        var fileName = $"{userIdTrimmed}_{safeName}_f1.jpg";
                        var filePath = Path.Combine(enrolledDir, fileName);
                        await System.IO.File.WriteAllBytesAsync(filePath, photoBytes);
                        savedFileName = Path.GetFileName(filePath);

                        var faceEntity = new AttendanceUserFaceEntity
                        {
                            AttendanceUserId = dbUser.Id,
                            UserId = dbUser.UserId,
                            FaceIndex = 1,
                            PhotoFileName = savedFileName,
                            PhotoLength = photoBytes.Length,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = null,
                            IsActive = true
                        };

                        await _db.AttendanceUserFaces.AddAsync(faceEntity);

                        // Legacy compatibility: update AttendanceUsers.PhotoFileName
                        dbUser.PhotoFileName = savedFileName;
                        dbUser.PhotoLength = photoBytes.Length;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[EnrollFace] Failed to persist reconciled face photo: {ex.Message}");
                        return StatusCode(StatusCodes.Status500InternalServerError, new { Success = false, Message = "Failed to persist reconciled face photo.", Error = ex.Message });
                    }
                }

                await _db.SaveChangesAsync();
                _logger?.LogInformation("[EnrollFace] After SaveChanges (AlreadyEnrolled) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] After SaveChanges (AlreadyEnrolled) for {userIdTrimmed} at {DateTime.UtcNow:o}");

                return Ok(new
                {
                    Success = false,
                    AlreadyEnrolled = true,
                    Message = "The supplied face photo is already enrolled on the Dahua device (fail code 24: Photo already exists). Database reconciled if a record was missing.",
                    UserId = userIdTrimmed,
                    EnrollmentStatus = "FaceEnrolled",
                    FailCode = result.FailCode,
                    SdkError = result.SdkError,
                    FaceIndex = 1,
                    PhotoFileName = savedFileName
                });
            }

            if (result.FailCode == 28)
            {
                dbUser.EnrollmentStatus = "DuplicateFace";
                dbUser.UpdatedAt = DateTime.Now;
                _logger?.LogInformation("[EnrollFace] Before SaveChanges (DuplicateFace) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] Before SaveChanges (DuplicateFace) for {userIdTrimmed} at {DateTime.UtcNow:o}");
                await _db.SaveChangesAsync();
                _logger?.LogInformation("[EnrollFace] After SaveChanges (DuplicateFace) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
                Console.WriteLine($"[EnrollFace] After SaveChanges (DuplicateFace) for {userIdTrimmed} at {DateTime.UtcNow:o}");

                return Ok(new
                {
                    Success = false,
                    DuplicateFaceDetected = true,
                    Message = "The supplied face is already enrolled on this device. The Dahua SDK confirms the face feature exists (fail code 28) but does not identify which user owns it.",
                    UserId = userIdTrimmed,
                    EnrollmentStatus = "DuplicateFace",
                    FailCode = result.FailCode,
                    SdkError = result.SdkError
                });
            }

            dbUser.EnrollmentStatus = "FaceEnrollmentFailed";
            dbUser.UpdatedAt = DateTime.Now;
            _logger?.LogInformation("[EnrollFace] Before SaveChanges (FaceEnrollmentFailed) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] Before SaveChanges (FaceEnrollmentFailed) for {userIdTrimmed} at {DateTime.UtcNow:o}");
            await _db.SaveChangesAsync();
            _logger?.LogInformation("[EnrollFace] After SaveChanges (FaceEnrollmentFailed) for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] After SaveChanges (FaceEnrollmentFailed) for {userIdTrimmed} at {DateTime.UtcNow:o}");

            return Ok(new
            {
                Success = false,
                Message = "Face enrollment failed on the Dahua device.",
                UserId = userIdTrimmed,
                EnrollmentStatus = "FaceEnrollmentFailed",
                FaceResult = result
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[EnrollFace] EXCEPTION for {userId} at {time}", userIdTrimmed, DateTime.UtcNow);
            Console.WriteLine($"[EnrollFace] EXCEPTION for {userIdTrimmed} at {DateTime.UtcNow:o}: {ex.Message}");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new
                {
                    Success = false,
                    Message = "Face enrollment failed.",
                    Error = ex.Message
                });
        }
    }

    [HttpPost("create-user")]
    public IActionResult CreateUser(
        [FromQuery] string userId,
        [FromQuery] string? name = null)
    {
        string userIdTrimmed = (userId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userIdTrimmed))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "userId is required."
            });
        }

        CreateUserResult result = _sdk.CreateAccessControlUser(userIdTrimmed, name?.Trim() ?? "");
        return Ok(result);
    }

    [HttpGet("logout")]
    public IActionResult Logout()
    {
        bool result = _sdk.Logout();

        return Ok(new
        {
            Success = result,
            Message = result
                ? "Device logout successful."
                : "Device logout failed."
        });
    }

    private async Task SetEnrollmentStatus(string userId, string status)
    {
        try
        {
            var u = await _db.AttendanceUsers.FirstOrDefaultAsync(x => x.UserId == userId);
            if (u != null)
            {
                u.EnrollmentStatus = status;
                u.UpdatedAt = DateTime.Now;
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SetEnrollmentStatus] Failed for {userId}: {ex.Message}");
        }
    }
}