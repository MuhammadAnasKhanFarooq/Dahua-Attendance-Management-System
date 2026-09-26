using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DahuaAttendanceAPI.Models;

[Table("AttendanceUsers")]
public class AttendanceUserEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string UserId { get; set; } = "";

    [MaxLength(200)]
    public string? UserName { get; set; }

    [MaxLength(100)]
    public string? CardNo { get; set; }

    [MaxLength(200)]
    public string? Password { get; set; }

    [MaxLength(100)]
    public string? ClassNumber { get; set; }

    [MaxLength(50)]
    public string? PhoneNumber { get; set; }

    public int PhotoLength { get; set; }

    // Enrollment status: PendingFaceEnrollment | FaceEnrolled | FaceEnrollmentFailed | DuplicateFace | DeviceUserCreationFailed
    [MaxLength(50)]
    public string? EnrollmentStatus { get; set; }

    [MaxLength(260)]
    public string? PhotoFileName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Navigation: faces for this user (phase-2)
    public ICollection<AttendanceUserFaceEntity>? Faces { get; set; }
}