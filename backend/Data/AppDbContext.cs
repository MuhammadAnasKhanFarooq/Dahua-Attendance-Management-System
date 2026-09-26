using DahuaAttendanceAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DahuaAttendanceAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AttendanceUserEntity> AttendanceUsers => Set<AttendanceUserEntity>();

    public DbSet<AttendanceLogEntity> AttendanceLogs => Set<AttendanceLogEntity>();

    // New faces table
    public DbSet<DahuaAttendanceAPI.Models.AttendanceUserFaceEntity> AttendanceUserFaces => Set<DahuaAttendanceAPI.Models.AttendanceUserFaceEntity>();

    // Application users for authentication/authorization (RBAC)
    public DbSet<DahuaAttendanceAPI.Models.ApplicationUserEntity> ApplicationUsers => Set<DahuaAttendanceAPI.Models.ApplicationUserEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DahuaAttendanceAPI.Models.AttendanceUserFaceEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PhotoFileName).HasMaxLength(260);
            entity.Property(e => e.UserId).HasMaxLength(100);
            entity.HasIndex(e => e.AttendanceUserId).HasDatabaseName("IX_AttendanceUserFaces_AttendanceUserId");
            entity.HasIndex(e => new { e.AttendanceUserId, e.FaceIndex }).IsUnique().HasDatabaseName("UX_AttendanceUserFaces_User_FaceIndex");
            // FaceIndex constrained to 1..2 for phase-1
            entity.HasCheckConstraint("CK_AttendanceUserFaces_FaceIndex", "[FaceIndex] IN (1,2)");
            entity.HasOne(e => e.AttendanceUser)
                  .WithMany(u => u.Faces)
                  .HasForeignKey(e => e.AttendanceUserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Application user mapping
        modelBuilder.Entity<DahuaAttendanceAPI.Models.ApplicationUserEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.UserName).IsUnique().HasDatabaseName("UX_ApplicationUsers_UserName");
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(50).IsRequired();
            entity.Property(e => e.AttendanceUserId).IsRequired(false);
            entity.HasOne(e => e.AttendanceUser)
                  .WithMany()
                  .HasForeignKey(e => e.AttendanceUserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}