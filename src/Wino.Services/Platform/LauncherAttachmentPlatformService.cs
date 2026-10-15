#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;

namespace Wino.Services.Platform;

/// <summary>
/// Attachments for hosts without a save policy (no quarantine or zone identifier: iOS, Android). Saved files stay where
/// the shared file service put them; opening goes through the host's <see cref="IExternalLauncher"/>.
/// </summary>
public sealed class LauncherAttachmentPlatformService(IExternalLauncher launcher) : IAttachmentPlatformService
{
    public Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Cancelled, localPath));
        // No save policy applies; only confirm that there is a file to apply it to.
        return Task.FromResult(File.Exists(localPath)
            ? new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath)
            : new AttachmentFileOperationResult(AttachmentFileOperationStatus.Failed, localPath, ErrorMessage: "The attachment does not exist."));
    }

    public async Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var policy = await ApplySavePolicyAsync(localPath, cancellationToken).ConfigureAwait(false);
        if (!policy.IsSuccess)
            return policy;

        var result = await launcher.LaunchFileAsync(localPath, cancellationToken).ConfigureAwait(false);
        var status = result.Status switch
        {
            PlatformOperationStatus.Succeeded => AttachmentFileOperationStatus.Succeeded,
            PlatformOperationStatus.Cancelled => AttachmentFileOperationStatus.Cancelled,
            PlatformOperationStatus.Unavailable => AttachmentFileOperationStatus.Unavailable,
            _ => AttachmentFileOperationStatus.Failed
        };
        // The shared materialization owner retains this file for the presenter, including declined opens.
        return new(status, localPath, ErrorMessage: result.ErrorMessage);
    }
}
