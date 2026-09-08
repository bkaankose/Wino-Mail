#nullable enable
namespace Wino.Core.Domain.Enums;

public enum ContentTypeDetectionStatus
{
    NotInspected,
    Detected,
    LowConfidence,
    Unavailable,
    Failed
}
