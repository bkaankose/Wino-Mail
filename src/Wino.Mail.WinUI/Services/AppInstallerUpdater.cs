using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Updates the stable sideload package through the App Installer feed it was installed from,
/// so the feed's own update settings keep working.
/// </summary>
internal static class AppInstallerUpdater
{
    public static async Task<bool> IsUpdateAvailableAsync()
    {
        var package = Package.Current;

        // Installed from a bundle or a development layout rather than the feed: nothing to check against.
        if (package.GetAppInstallerInfo()?.Uri == null)
            return false;

        var result = await package.CheckUpdateAvailabilityAsync();

        if (result.Availability == PackageUpdateAvailability.Error)
            Serilog.Log.Warning("App Installer update check failed (HRESULT 0x{HResult:X8}).", result.ExtendedError?.HResult ?? 0);

        return result.Availability is PackageUpdateAvailability.Available or PackageUpdateAvailability.Required;
    }

    public static async Task<bool> InstallAsync(IProgress<double>? progress, IWinoLogger logger)
    {
        var feedUri = Package.Current.GetAppInstallerInfo()?.Uri;

        if (feedUri == null)
            return false;

        var packageManager = new PackageManager();
        var operation = packageManager.AddPackageByAppInstallerFileAsync(
            feedUri,
            AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
            packageManager.GetDefaultPackageVolume());

        var result = progress == null
            ? await operation
            : await operation.AsTask(new Progress<DeploymentProgress>(value => progress.Report(value.percentage)));

        if (result.ExtendedErrorCode == null)
            return true;

        logger.TrackEvent("App Installer update installation did not complete", new Dictionary<string, string>
        {
            { "HResult", $"0x{result.ExtendedErrorCode.HResult:X8}" },
            { nameof(result.ErrorText), result.ErrorText ?? string.Empty }
        });

        return false;
    }
}
