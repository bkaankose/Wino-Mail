using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Core.Domain.Interfaces;

public interface IAttachmentPlatformService
{
    Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default);
    Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default);
}
