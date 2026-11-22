using Wino.Core.Domain;
using Wino.Mail.Linux.Infrastructure.Updates;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// "Update available" (macOS: Sparkle's update window): Update, Learn more (when the feed names a page) and Close, then
/// a second alert that shows the download's progress and can cancel it. An alert closes on every response, so the
/// progress cannot live in the first one.
/// </summary>
internal static class UpdateAvailableDialog
{
    public const string Update = "update";
    public const string LearnMore = "learn-more";
    public const string Close = "close";
    private const string CancelDownload = "cancel";

    public static AlertRequest Offer(LinuxUpdateInfo update)
    {
        var responses = new List<AlertResponse> { new(Close, Translator.Buttons_Close) };
        if (update.NotesUrl is not null) responses.Add(new AlertResponse(LearnMore, Translator.Buttons_LearnMore));
        responses.Add(new AlertResponse(Update, Translator.Buttons_Update, Adw.ResponseAppearance.Suggested));
        return new AlertRequest(Translator.Notifications_StoreUpdateAvailableTitle, Body(update), [.. responses])
        {
            CloseResponse = Close,
            DefaultResponse = Update,
        };
    }

    public static string Body(LinuxUpdateInfo update)
        => Translator.Notifications_AppUpdateAvailableMessage + "\n" + Translator.SettingsAboutVersion + update.DisplayVersion;

    /// <summary>Main thread only. Completes with the downloaded file, or null when the user cancelled.</summary>
    public static async Task<string?> DownloadAsync(Gtk.Widget owner, LinuxUpdateInfo update,
        Func<IProgress<double>, CancellationToken, Task<string>> download)
    {
        var progressBar = Gtk.ProgressBar.New();
        progressBar.SetShowText(true);
        WinoA11y.Label(progressBar, Translator.Notifications_StoreUpdateAvailableTitle);
        var dialog = Adw.AlertDialog.New(Translator.Notifications_StoreUpdateAvailableTitle, Body(update));
        dialog.SetHeadingUseMarkup(false);
        dialog.SetBodyUseMarkup(false);
        dialog.AddResponse(CancelDownload, Translator.Buttons_Cancel);
        dialog.SetCloseResponse(CancelDownload);
        dialog.SetExtraChild(progressBar);

        using var cancellation = new CancellationTokenSource();
        var finished = false;
        using var subscription = GtkSignals.Connect<Adw.AlertDialog, Adw.AlertDialog.ResponseSignalArgs>(
            h => dialog.OnResponse += h, h => dialog.OnResponse -= h, (_, _) =>
            {
                if (!finished) cancellation.Cancel();
            });
        dialog.Present(owner);
        var progress = new Progress<double>(fraction => progressBar.SetFraction(Math.Clamp(fraction, 0, 1)));
        try
        {
            return await download(progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            finished = true;
            dialog.ForceClose();
        }
    }
}
