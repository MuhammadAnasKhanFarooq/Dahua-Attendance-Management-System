using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DahuaAttendanceAPI.Models;

[Table("AttendanceUserFaces")]
public class AttendanceUserFaceEntity
{
    [Key]
    public int Id { get; set; }

    // FK to AttendanceUsers.Id
    public int AttendanceUserId { get; set; }

    [MaxLength(100)]
    public string? UserId { get; set; }

    // Face slot index (1 or 2)
    public int FaceIndex { get; set; }

    [MaxLength(260)]
    public string? PhotoFileName { get; set; }

    public int PhotoLength { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Whether this face record is active
    public bool IsActive { get; set; }

    // Navigation property
    [ForeignKey("AttendanceUserId")]
    public AttendanceUserEntity? AttendanceUser { get; set; }
}
