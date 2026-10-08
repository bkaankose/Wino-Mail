#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Attachments;

public sealed record AttachmentFileOperationResult(
    AttachmentFileOperationStatus Status,
    string? FilePath = null,
    ContentTypeDetectionResult? Detection = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == AttachmentFileOperationStatus.Succeeded;
}
