using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure.Updates;

/// <summary>
/// The user side of the update check: the automatic prompt, Check for Updates in the menu and in Settings, the
/// download and the hand-off to the desktop's package installer (App Center on Ubuntu, Software on Fedora), which asks
/// for the password and upgrades in place. The app neither quits nor runs a package tool itself; the new version starts
/// with the next launch. When the download or the installer fails, the package address opens in the browser. An Arch
/// copy is only told about the update: Update opens the download page, because no desktop installs Arch packages.
/// </summary>
internal sealed class LinuxUpdatePresenter(LinuxUpdateService updates, GtkDialogService dialogs, IExternalLauncher launcher) : IApplicationLifecycleHook
{
    private bool _busy;

    public Task ServicesReadyAsync()
    {
        updates.PromptRequested += OnPromptRequested;
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        updates.PromptRequested -= OnPromptRequested;
        return Task.CompletedTask;
    }

    private void OnPromptRequested(LinuxUpdateInfo update) => _ = OfferAsync(update);

    /// <summary>A manual check always answers: the offer, "up to date" or the failure message.</summary>
    public async Task CheckNowAsync()
    {
        if (!updates.IsAvailable || _busy) return;
        var result = await updates.CheckAsync(manual: true, CancellationToken.None);
        switch (result)
        {
            case { Status: LinuxUpdateCheckStatus.Available, Update: { } update }:
                await OfferAsync(update);
                break;
            case { Status: LinuxUpdateCheckStatus.UpToDate }:
                await dialogs.ShowUpdateUpToDateAsync(updates.CurrentVersion);
                break;
            case { Status: LinuxUpdateCheckStatus.Failed }:
                ShowFailure();
                break;
        }
    }

    private async Task OfferAsync(LinuxUpdateInfo update)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var response = await dialogs.ShowUpdateAvailableAsync(update);
            if (response == UpdateAvailableDialog.LearnMore && update.NotesUrl is { } notes) await launcher.LaunchUriAsync(notes);
            else if (response == UpdateAvailableDialog.Update) await InstallAsync(update);
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Updates: the update dialog failed.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallAsync(LinuxUpdateInfo update)
    {
        if (!update.Kind.DownloadsUpdate)
        {
            var page = update.DownloadPageUrl ?? update.PackageUrl;
            Serilog.Log.Information("Updates: {Format} copies update through the package manager; opening {Page}.", update.Kind.FeedKey, page);
            await launcher.LaunchUriAsync(page);
            return;
        }
        string? path;
        try
        {
            path = await dialogs.ShowUpdateDownloadAsync(update, (progress, cancellation) => updates.DownloadAsync(update, progress, cancellation));
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning("Updates: the download of {Package} failed: {Reason}", update.PackageUrl, exception.Message);
            await FallBackToBrowserAsync(update);
            return;
        }
        if (path is null)
        {
            Serilog.Log.Information("Updates: the download was cancelled.");
            return;
        }
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINO_DEBUG_UPDATE_FEED") is { Length: > 0 })
        {
            Serilog.Log.Information("Updates: the installer would open {Path} (Debug feed override, not launched).", path);
            return;
        }
#endif
        var launched = await launcher.LaunchFileAsync(path);
        Serilog.Log.Information("Updates: opening {Path} in the package installer: {Status}.", path, launched.Status);
        if (launched.Status != PlatformOperationStatus.Succeeded) await FallBackToBrowserAsync(update);
    }

    private async Task FallBackToBrowserAsync(LinuxUpdateInfo update)
    {
        ShowFailure();
        await launcher.LaunchUriAsync(update.PackageUrl);
    }

    private void ShowFailure()
        => dialogs.InfoBarMessage(Translator.GeneralTitle_Error, Translator.Notifications_AppUpdateFailedMessage, InfoBarMessageType.Error);
}
