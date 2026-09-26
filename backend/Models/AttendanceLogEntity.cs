using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DahuaAttendanceAPI.Models;

/// <summary>
/// Persisted record of every successful face recognition event received
/// from the Dahua device via EVENT_IVS_FACERECOGNITION (lCommand = 0x117).
///
/// One row is written per recognition event where a non-empty UserId is
/// returned by the device.  The row is written by AttendanceLoggingService,
/// which subscribes directly to DahuaSdkService.OnDeviceRecognition.
/// </summary>
[Table("AttendanceLogs")]
public class AttendanceLogEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The access-control user ID reported by the Dahua device.
    /// Maps to AttendanceUserEntity.UserId (e.g. "1005").
    /// Populated from Dhx_FACERECOGNITION_PERSON_INFO.szID via stuCandidates[0].
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string UserId { get; set; } = "";

    /// <summary>
    /// Display name from the device event (Dhx_DEV_EVENT_FACERECOGNITION_INFO.szName).
    /// May be empty if the device does not return a name.
    /// </summary>
    [MaxLength(200)]
    public string? UserName { get; set; }

    /// <summary>
    /// UTC timestamp at which the NativeMessCallback received and parsed the event.
    /// </summary>
    public DateTime EventTime { get; set; }

    /// <summary>
    /// Similarity score reported by the device (stuCandidates[0].bySimilarity).
    /// Range 0–100.  Higher = more confident match.
    /// </summary>
    public int Confidence { get; set; }

    /// <summary>
    /// Device channel ID (nChannelID from the event struct).
    /// </summary>
    public int ChannelId { get; set; }

    /// <summary>
    /// Device event sequence ID (nEventID from the event struct).
    /// </summary>
    public int EventId { get; set; }

    /// <summary>
    /// Event source — always "DAHUA" for device-reported recognition events.
    /// </summary>
    [MaxLength(20)]
    public string Source { get; set; } = "DAHUA";

    /// <summary>
    /// UTC timestamp at which this row was inserted into the database.
    /// </summary>
    public DateTime RecordedAt { get; set; }
}
