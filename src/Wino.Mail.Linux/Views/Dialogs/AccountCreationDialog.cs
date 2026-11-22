using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The waiting dialog of <see cref="IAccountCreationDialog"/> (macOS AccountProgress): a spinner, the text and Cancel.
/// <see cref="ShowDialogAsync"/> returns once it is shown; <see cref="Complete"/> closes it. Nothing touches GTK before
/// <see cref="ShowDialogAsync"/>, because the service creates it on any thread.
/// </summary>
internal sealed class AccountCreationDialog(IDispatcher dispatcher, Func<Gtk.Window?> owner) : IAccountCreationDialog
{
    private Adw.Dialog? _dialog;
    private CancellationTokenSource? _cancellation;
    private IDisposable? _cancelClicked;
    private IDisposable? _closeAttempt;

    public AccountCreationDialogState State { get; set; }

    public Task ShowDialogAsync(CancellationTokenSource cancellationTokenSource) => dispatcher.ExecuteOnUIThread(() =>
    {
        _cancellation = cancellationTokenSource;
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(24, 24);
        var title = Gtk.Label.New(Translator.MacOS_AccountCreation_Title);
        title.AddCssClass("title-4");
        var body = Gtk.Label.New(Translator.MacOS_AccountCreation_BrowserMessage);
        body.SetWrap(true);
        body.SetJustify(Gtk.Justification.Center);
        body.AddCssClass("dim-label");
        var cancel = Gtk.Button.NewWithLabel(Translator.Buttons_Cancel);
        cancel.SetHalign(Gtk.Align.Center);
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.SetMarginTop(24);
        box.SetMarginBottom(20);
        box.SetMarginStart(24);
        box.SetMarginEnd(24);
        box.Append(spinner);
        box.Append(title);
        box.Append(body);
        box.Append(cancel);

        var dialog = _dialog = Adw.Dialog.New();
        dialog.SetTitle(Translator.MacOS_AccountCreation_Title);
        dialog.SetContentWidth(400);
        dialog.SetChild(box);
        dialog.SetCanClose(false);
        _cancelClicked = GtkSignals.OnClicked(cancel, () => Complete(true));
        // Escape asks to close a dialog that cannot close: only Cancel ends it.
        _closeAttempt = GtkSignals.Connect<Adw.Dialog>(h => dialog.OnCloseAttempt += h, h => dialog.OnCloseAttempt -= h, (_, _) => { });
        if (owner() is { } window) dialog.Present(window);
        else Serilog.Log.Warning("The account creation dialog has no window to show in.");
    });

    public void Complete(bool cancel)
    {
        if (cancel)
        {
            try { _cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _ = dispatcher.ExecuteOnUIThread(() =>
        {
            _cancelClicked?.Dispose();
            _closeAttempt?.Dispose();
            _cancelClicked = _closeAttempt = null;
            var dialog = _dialog;
            _dialog = null;
            dialog?.ForceClose();
        });
    }
}
