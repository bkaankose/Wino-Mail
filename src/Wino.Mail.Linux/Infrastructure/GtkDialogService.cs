using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Dialogs of the Linux head. This file holds the shared presentation helpers, info bars (toasts) and the certificate
/// and open-link questions; <c>GtkDialogService.Basic.cs</c> the alerts and the text input, <c>GtkDialogService.Pickers.cs</c>
/// the file pickers, and <c>GtkDialogService.Dismissed.cs</c> every dialog whose package has not landed. A package moves
/// its own members out of <c>Dismissed.cs</c> into its own <c>GtkDialogService.&lt;Group&gt;.cs</c> and builds them on
/// <see cref="AlertAsync"/>, <see cref="PresentAsync{T}"/>, <see cref="FormDialog"/> and <see cref="FilePickers"/>.
/// </summary>
internal sealed partial class GtkDialogService(IDispatcher dispatcher, Func<Views.MainWindow?> window, IConfigurationService configuration) : IMailDialogService
{
    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType)
        => _ = dispatcher.ExecuteOnUIThread(() => window()?.ShowToast(title, message, null, null));

    public void InfoBarMessage(string title, string message, InfoBarMessageType messageType, string actionButtonText, Action action)
        => _ = dispatcher.ExecuteOnUIThread(() => window()?.ShowToast(title, message, actionButtonText, action));

    public Task<bool> ShowServerCertificateTrustDialogAsync(ServerCertificateTrustPrompt prompt)
    {
        var body = new List<string> { prompt.Intro };
        if (prompt.Problems.Length > 0)
            body.Add(prompt.ProblemsHeader + "\n" + string.Join("\n", prompt.Problems.Select(problem => "• " + problem)));
        if (prompt.Facts.Length > 0)
            body.Add(string.Join("\n", prompt.Facts.Select(fact => $"{fact.Label}: {fact.Value}")));
        body.Add(prompt.Closing);
        if (!string.IsNullOrEmpty(prompt.AcknowledgementText)) body.Add(prompt.AcknowledgementText);
        return AskAsync(prompt.Title, string.Join("\n\n", body.Where(text => !string.IsNullOrWhiteSpace(text))), prompt.AcceptButtonText, destructive: prompt.IsDangerous);
    }

    public Task<bool> ShowOpenLinkConfirmationAsync(string? linkText, Uri originalUri, Uri cleanedUri, int removedParameterCount, string? browserName)
        => AskAsync(Translator.OpenLinkDialog_Title, OpenLinkConfirmationText.GetMessage(linkText, cleanedUri, removedParameterCount, browserName),
            Translator.OpenLinkDialog_OpenLink, destructive: false);

    private async Task<bool> AskAsync(string title, string body, string acceptText, bool destructive)
        => await AlertAsync(new AlertRequest(title, body,
            [
                new AlertResponse(Cancel, Translator.Buttons_Cancel),
                new AlertResponse(Primary, acceptText, destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = Cancel,
        }) == Primary;

    /// <summary>
    /// The window a dialog hangs from: the application's active window while it is visible (a dialog raised from a
    /// second window stays on it), else the main window. Main thread only.
    /// </summary>
    private Gtk.Window? ResolveOwner()
    {
        if ((Gio.Application.GetDefault() as Gtk.Application)?.GetActiveWindow() is { } active && active.GetVisible()) return active;
        return window()?.Window is { } main && main.GetVisible() ? main : null;
    }

    private Task<string> AlertAsync(AlertRequest request)
        => PresentAsync(owner => AlertPresenter.PresentAsync(owner, request), request.CloseResponse);

    /// <summary>
    /// Runs <paramref name="present"/> on the main thread with the owner window; <paramref name="dismissed"/> when there
    /// is none. <see cref="Adw.Dialog.Present"/> stacks over a dialog that is already open, so unlike macOS sheets no
    /// presentation gate is needed.
    /// </summary>
    private Task<T> PresentAsync<T>(Func<Gtk.Window, Task<T>> present, T dismissed)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.ExecuteOnUIThread(() => _ = CompleteAsync(completion, present, dismissed));
        return completion.Task;
    }

    private async Task CompleteAsync<T>(TaskCompletionSource<T> completion, Func<Gtk.Window, Task<T>> present, T dismissed)
    {
        try
        {
            if (ResolveOwner() is not { } owner)
            {
                Serilog.Log.Warning("A dialog was dismissed because no window can show it.");
                completion.TrySetResult(dismissed);
                return;
            }
            completion.TrySetResult(await present(owner));
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
