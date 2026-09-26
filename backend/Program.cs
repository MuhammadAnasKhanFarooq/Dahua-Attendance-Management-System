using DahuaAttendanceAPI.Data;
using DahuaAttendanceAPI.Services;
using DahuaAttendanceAPI;
using DahuaAttendanceAPI.Hubs;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// JWT settings
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"] ?? Environment.GetEnvironmentVariable("AUTH_JWT_KEY") ?? "replace_this_in_production";
var jwtIssuer = jwtSection["Issuer"] ?? "DahuaAttendanceAPI";
var jwtAudience = jwtSection["Audience"] ?? "DahuaAttendanceAPIUsers";
var jwtExpiryMinutes = int.TryParse(jwtSection["ExpiryMinutes"], out var m) ? m : 60;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdmin", policy => policy.RequireRole("Admin"));
    options.AddPolicy("RequireHR", policy => policy.RequireRole("Admin", "HR"));
    options.AddPolicy("RequireSecurity", policy => policy.RequireRole("Admin", "Security"));
    options.AddPolicy("RequireUser", policy => policy.RequireRole("Admin", "HR", "Security", "User"));
});

// ============================================================
// SQL SERVER DATABASE
// ============================================================

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString(
            "DefaultConnection"));
});

// ============================================================
// CONTROLLERS
// ============================================================

// Add global resource filter to log MVC resource execution (runs before model binding)
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new DahuaAttendanceAPI.Services.ResourceLoggingFilter());
});

// ============================================================
// DAHUA SDK SERVICE
// ============================================================

builder.Services.AddSingleton<DahuaSdkService>();

// Live face service (requires OpenCvSharp native runtime)
// Register LiveFaceService as scoped because it depends on AppDbContext (scoped).
// Do NOT resolve AppDbContext from the root provider; let DI construct the scoped service.
builder.Services.AddScoped<LiveFaceService>();

// FaceEnrollmentCallbackService listens for DH_ALARM_FACEINFO_COLLECT (0x3240)
// events from the Dahua SDK and updates EnrollmentStatus in the database when
// a device-side face collection completes. Guards prevent false updates.
builder.Services.AddHostedService<FaceEnrollmentCallbackService>();

// AttendanceLoggingService subscribes to DahuaSdkService.OnDeviceRecognition and
// writes successful device recognition events to the AttendanceLogs table.
builder.Services.AddHostedService<AttendanceLoggingService>();

// ============================================================
// OPENAPI
// ============================================================

builder.Services.AddOpenApi();


// SignalR for recognition metadata
builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Apply any pending EF Core migrations. Using Migrate() is preferred here so
    // the manual migration added for AttendanceUserFaces is applied automatically
    // at startup in development/testing. Do not modify the migration itself.
    try
    {
        db.Database.Migrate();
    }

// NOTE: Admin seeding is performed below after migration/table ensures.
    catch (Exception ex)
    {
        // If migrations cannot be applied for some reason, fall back to EnsureCreated
        // to preserve existing runtime behavior while surfacing the migration issue.
        Console.WriteLine($"[Startup] Database Migrate failed: {ex.Message}. Falling back to EnsureCreated().");
        try { db.Database.EnsureCreated(); } catch { }
    }

    // Ensure AttendanceLogs table exists for legacy logging (keeps previous behavior)
    try
    {
        db.Database.ExecuteSqlRaw(@"
            IF OBJECT_ID(N'dbo.AttendanceLogs', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[AttendanceLogs] (
                    [Id] int NOT NULL IDENTITY(1,1),
                    [UserId] nvarchar(100) NOT NULL,
                    [UserName] nvarchar(200) NULL,
                    [EventTime] datetime2 NOT NULL,
                    [Confidence] int NOT NULL,
                    [ChannelId] int NOT NULL,
                    [EventId] int NOT NULL,
                    [Source] nvarchar(20) NOT NULL DEFAULT 'DAHUA',
                    [RecordedAt] datetime2 NOT NULL,
                    CONSTRAINT [PK_AttendanceLogs] PRIMARY KEY ([Id])
                );
            END;
        ");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Ensure AttendanceLogs failed: {ex.Message}");
    }

    // Seed initial admin user if missing (uses environment variables AUTH_ADMIN_USERNAME and AUTH_ADMIN_PASSWORD)
    try
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var adminUser = db.ApplicationUsers.FirstOrDefault(u => u.Role == "Admin");
        if (adminUser == null)
        {
            var adminUserName = config["AUTH_ADMIN_USERNAME"] ?? Environment.GetEnvironmentVariable("AUTH_ADMIN_USERNAME");
            var adminPassword = config["AUTH_ADMIN_PASSWORD"] ?? Environment.GetEnvironmentVariable("AUTH_ADMIN_PASSWORD");
            if (!string.IsNullOrWhiteSpace(adminUserName) && !string.IsNullOrWhiteSpace(adminPassword))
            {
                var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<DahuaAttendanceAPI.Models.ApplicationUserEntity>();
                var newUser = new DahuaAttendanceAPI.Models.ApplicationUserEntity
                {
                    UserName = adminUserName.Trim(),
                    Role = "Admin",
                    CreatedAt = DateTime.UtcNow
                };
                newUser.PasswordHash = hasher.HashPassword(newUser, adminPassword);
                db.ApplicationUsers.Add(newUser);
                db.SaveChanges();
                Console.WriteLine("[Startup] Admin user seeded from AUTH_ADMIN_* environment variables.");
            }
            else
            {
                Console.WriteLine("[Startup] No admin user found and AUTH_ADMIN_* not provided; skipping admin seed.");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] Admin seed failed: {ex.Message}");
    }
}



// Set global instance for legacy resolution (AppServices is defined in AppServices.cs)
AppServices.Instance = app.Services;

// ============================================================
// OPENAPI
// ============================================================


if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Map SignalR hub
app.MapHub<RecognitionHub>(RecognitionHub.HubUrl);

// Authentication & Authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// ============================================================
// HTTPS
// ============================================================

app.UseHttpsRedirection();

// Serve static files from wwwroot (live.html, scripts, assets)
app.UseStaticFiles();

// ============================================================
// CONTROLLERS
// ============================================================

// HTTP request/response tracing middleware (minimal, diagnostic only)
// Logs every incoming request and the response completion with elapsed time.
app.Use(async (context, next) =>
{
    try
    {
        var req = context.Request;
        var ts = DateTime.UtcNow;
        Console.WriteLine($"[HTTP TRACE] METHOD={req.Method} PATH={req.Path} QUERY={req.QueryString} HOST={req.Host} SCHEME={req.Scheme} TIME={ts:o}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await next();
        sw.Stop();

        Console.WriteLine($"[HTTP TRACE] RESPONSE status={context.Response.StatusCode} path={req.Path} elapsedMs={sw.ElapsedMilliseconds}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[HTTP TRACE] Exception in tracing middleware: {ex.Message}");
        throw;
    }
});

app.MapControllers();

// ============================================================
// RUN
// ============================================================

app.Run();