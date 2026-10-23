using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Attachments;

namespace MailCal.Core.Domain.Interfaces;

public interface IAttachmentPlatformService
{
    Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default);
    Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default);
}
