#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Attachments;

namespace MailCal.Core.Domain.Interfaces;

public interface IAttachmentFileService
{
    ValueTask<ContentTypeDetectionResult> InspectAsync(
        AttachmentFileSource source,
        CancellationToken cancellationToken = default);

    Task<AttachmentFileOperationResult> OpenAsync(
        AttachmentFileSource source,
        string workingFolderPath,
        ContentTypeDetectionResult? knownDetection,
        bool mismatchApproved,
        CancellationToken cancellationToken = default);

    Task<AttachmentFileOperationResult> SaveAsync(
        AttachmentFileSource source,
        string destinationFolderPath,
        string? destinationFileName = null,
        CancellationToken cancellationToken = default);
}
