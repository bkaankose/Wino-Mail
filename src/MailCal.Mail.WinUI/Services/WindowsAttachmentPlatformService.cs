using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Attachments;
using MailCal.Platform.Windows;
using MailCal.Services;

namespace MailCal.Mail.WinUI.Services;

/// <summary>Keeps native policy prompts and their live window owner in the Windows head.</summary>
public sealed class WindowsAttachmentPlatformService(
    NativeAppService nativeAppService,
    IWindowsAttachmentPolicyService policy,
    IMailCalLogger logger) : IAttachmentPlatformService
{
    public async Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await nativeAppService.ExecuteOnPlatformThreadAsync(() =>
            {
                var owner = nativeAppService.GetCoreWindowHwnd?.Invoke() ?? IntPtr.Zero;
                if (owner == IntPtr.Zero || !IsWindow(owner))
                    return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Unavailable, localPath));

                policy.Execute(localPath, owner);
                return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath));
            }, cancellationToken);
        }
        catch (WindowsAttachmentPolicyException ex) when (!ex.IsCancellation && !ex.IsPolicyBlock)
        {
            // Attachment Services rejected the call itself (E_INVALIDARG for some paths, unexpected HRESULTs on
            // some systems) without a policy verdict. The saved file already carries its zone mark, so the shell
            // launcher still applies the usual security prompts.
            Log.Warning("Attachment Services could not open the file (HRESULT 0x{HResult:X8}); using the default launcher.", ex.HResultCode);
            return await LaunchWithoutAttachmentServicesAsync(localPath, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            return MapFailure(localPath, ex);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    public async Task<AttachmentFileOperationResult> ApplySavePolicyAsync(string localPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await nativeAppService.ExecuteOnPlatformThreadAsync(() =>
            {
                policy.ApplySavePolicy(localPath);
                return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath));
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return MapFailure(localPath, ex);
        }
    }

    private async Task<AttachmentFileOperationResult> LaunchWithoutAttachmentServicesAsync(
        string path, WindowsAttachmentPolicyException policyException, CancellationToken cancellationToken)
    {
        IExternalLauncher launcher = nativeAppService;
        var launch = await launcher.LaunchFileAsync(path, cancellationToken);

        switch (launch.Status)
        {
            case PlatformOperationStatus.Succeeded:
                return new(AttachmentFileOperationStatus.Succeeded, path);
            case PlatformOperationStatus.Cancelled:
                return new(AttachmentFileOperationStatus.Cancelled, path);
            case PlatformOperationStatus.Unavailable:
                return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: launch.ErrorMessage ?? policyException.Message);
            default:
                // Both paths failed; report the Attachment Services result once unless it is a known HRESULT.
                if (!policyException.IsExpectedFailure)
                    logger.CaptureException(policyException, "AttachmentFilePolicy");

                return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: launch.ErrorMessage ?? policyException.Message);
        }
    }

    private AttachmentFileOperationResult MapFailure(string path, Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return new(AttachmentFileOperationStatus.Cancelled, path);
            case WindowsAttachmentPolicyException ex:
                // Policy blocks, cancellations and E_INVALIDARG are expected outcomes, not defects.
                if (!ex.IsExpectedFailure)
                    logger.CaptureException(ex, "AttachmentFilePolicy");
                else
                    Log.Warning("Attachment policy operation returned HRESULT 0x{HResult:X8}.", ex.HResultCode);

                return new(ex.IsCancellation ? AttachmentFileOperationStatus.Cancelled : AttachmentFileOperationStatus.PolicyBlocked,
                    path, ErrorMessage: ex.Message);
            case PlatformNotSupportedException ex:
                return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: ex.Message);
            default:
                logger.CaptureException(exception, "AttachmentFilePlatformOperation");
                return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: exception.Message);
        }
    }
}
