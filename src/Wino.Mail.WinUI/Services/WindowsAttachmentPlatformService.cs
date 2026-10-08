using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Attachments;
using Wino.Platform.Windows;
using Wino.Services;

namespace Wino.Mail.WinUI.Services;

/// <summary>Keeps native policy prompts and their live window owner in the Windows head.</summary>
public sealed class WindowsAttachmentPlatformService(
    NativeAppService nativeAppService,
    IWindowsAttachmentPolicyService policy,
    IWinoLogger logger) : IAttachmentPlatformService
{
    public Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default)
        => RunAsync(localPath, () =>
        {
            var owner = nativeAppService.GetCoreWindowHwnd?.Invoke() ?? IntPtr.Zero;
            if (owner == IntPtr.Zero || !IsWindow(owner))
                return new(AttachmentFileOperationStatus.Unavailable, localPath);

            policy.Execute(localPath, owner);
            return new(AttachmentFileOperationStatus.Succeeded, localPath);
        }, cancellationToken);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    public Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default)
        => RunAsync(localPath, () =>
        {
            policy.ApplySavePolicy(localPath);
            return new(AttachmentFileOperationStatus.Succeeded, localPath);
        }, cancellationToken);

    private async Task<AttachmentFileOperationResult> RunAsync(
        string path, Func<AttachmentFileOperationResult> action, CancellationToken cancellationToken)
    {
        try
        {
            return await nativeAppService.ExecuteOnPlatformThreadAsync(() => Task.FromResult(action()), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(AttachmentFileOperationStatus.Cancelled, path);
        }
        catch (WindowsAttachmentPolicyException ex)
        {
            if (!ex.IsCancellation)
                logger.CaptureException(ex, "AttachmentFilePolicy");

            return new(ex.IsCancellation ? AttachmentFileOperationStatus.Cancelled : AttachmentFileOperationStatus.PolicyBlocked,
                path, ErrorMessage: ex.Message);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: ex.Message);
        }
        catch (Exception ex)
        {
            logger.CaptureException(ex, "AttachmentFilePlatformOperation");
            return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: ex.Message);
        }
    }
}
