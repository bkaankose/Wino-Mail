#nullable enable
namespace MailCal.Core.Domain.Enums;

public enum ContentTypeDetectionStatus
{
    NotInspected,
    Detected,
    LowConfidence,
    Unavailable,
    Failed
}
