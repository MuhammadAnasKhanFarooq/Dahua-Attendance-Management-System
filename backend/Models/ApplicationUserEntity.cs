using System;

namespace DahuaAttendanceAPI.Models
{
    public class ApplicationUserEntity
    {
        public int Id { get; set; }

        // Username used to login; unique
        public string UserName { get; set; } = string.Empty;

        // Hashed password (ASP.NET Identity PasswordHasher)
        public string PasswordHash { get; set; } = string.Empty;

        // Role: Admin, HR, Security, User
        public string Role { get; set; } = "User";

        // Optional link to an attendance user (maps to AttendanceUsers.Id)
        public int? AttendanceUserId { get; set; }
        public AttendanceUserEntity? AttendanceUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
