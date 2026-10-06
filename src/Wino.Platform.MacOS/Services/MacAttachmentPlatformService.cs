using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Platform.MacOS.Services;

public sealed class MacAttachmentPlatformService(IExternalLauncher launcher) : IAttachmentPlatformService
{
    public Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Cancelled, localPath));
        return Task.FromResult(MacQuarantinePolicy.Ensure(localPath));
    }

    public async Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var policy = await ApplySavePolicyAsync(localPath, cancellationToken);
        if (!policy.IsSuccess)
            return policy;

        var result = await launcher.LaunchFileAsync(localPath, cancellationToken);
        var status = result.Status switch
        {
            PlatformOperationStatus.Succeeded => AttachmentFileOperationStatus.Succeeded,
            PlatformOperationStatus.Cancelled => AttachmentFileOperationStatus.Cancelled,
            PlatformOperationStatus.Unavailable => AttachmentFileOperationStatus.Unavailable,
            _ => AttachmentFileOperationStatus.Failed
        };
        // The shared materialization owner retains this file for the external reader, including declined opens.
        return new(status, localPath, ErrorMessage: result.ErrorMessage);
    }
}
