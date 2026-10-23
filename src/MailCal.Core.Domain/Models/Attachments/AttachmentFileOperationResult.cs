#nullable enable
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Attachments;

public sealed record AttachmentFileOperationResult(
    AttachmentFileOperationStatus Status,
    string? FilePath = null,
    ContentTypeDetectionResult? Detection = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == AttachmentFileOperationStatus.Succeeded;
}
