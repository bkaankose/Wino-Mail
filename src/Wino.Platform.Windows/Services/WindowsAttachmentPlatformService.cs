using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
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
    IPreferencesService preferencesService,
    IWinoLogger logger) : IAttachmentPlatformService
{
    public async Task<AttachmentFileOperationResult> OpenReceivedAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!preferencesService.CheckAttachmentsWithPlatformSecurity)
            return await LaunchWithoutAttachmentServicesAsync(localPath, null, cancellationToken);

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
        if (!preferencesService.CheckAttachmentsWithPlatformSecurity)
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath);

        try
        {
            return await nativeAppService.ExecuteOnPlatformThreadAsync(() =>
            {
                policy.ApplySavePolicy(localPath);
                return Task.FromResult(new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath));
            }, cancellationToken);
        }
        catch (WindowsAttachmentPolicyException ex) when (ex.IsSecurityZoneFailure)
        {
            // The file is already written and keeps the zone mark it was given; only the policy check could not
            // run, so the save is not reported as blocked.
            Log.Warning("Attachment Services could not check the saved file (HRESULT 0x{HResult:X8}); keeping the file.", ex.HResultCode);
            return new AttachmentFileOperationResult(AttachmentFileOperationStatus.Succeeded, localPath);
        }
        catch (Exception ex)
        {
            return MapFailure(localPath, ex);
        }
    }

    private async Task<AttachmentFileOperationResult> LaunchWithoutAttachmentServicesAsync(
        string path, WindowsAttachmentPolicyException? policyException, CancellationToken cancellationToken)
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
                return new(AttachmentFileOperationStatus.Unavailable, path, ErrorMessage: launch.ErrorMessage ?? policyException?.Message);
            default:
                if (policyException is null)
                {
                    Log.Warning("The default launcher could not open the attachment: {LaunchStatus} {LaunchError}", launch.Status, launch.ErrorMessage);
                    return new(AttachmentFileOperationStatus.Failed, path, ErrorMessage: launch.ErrorMessage);
                }

                Log.Warning("The default launcher could not open the attachment after HRESULT 0x{HResult:X8}: {LaunchStatus} {LaunchError}",
                    policyException.HResultCode, launch.Status, launch.ErrorMessage);

                // A security zone failure is expected, so only the launcher's own failure is worth a report.
                if (!policyException.IsExpectedFailure || policyException.IsSecurityZoneFailure)
                {
                    logger.CaptureException(new InvalidOperationException(
                        $"The default launcher failed ({launch.Status}: {launch.ErrorMessage}) after Attachment Services returned HRESULT 0x{policyException.HResultCode:X8}.",
                        policyException), "AttachmentFilePolicy");
                }

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
                // Policy blocks, cancellations, security zone failures and E_INVALIDARG are expected outcomes, not defects.
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
