using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The waiting dialog of a sign-in that runs in the browser (Windows ExternalBrowserAuthenticationDialog): a spinner,
/// the provider message, a warning when the browser did not open, Copy link and Cancel. Closing it any other way than
/// <see cref="Close"/> cancels the sign-in. The authorization address is shown and logged nowhere; Copy is its only way out.
/// </summary>
internal sealed class ExternalBrowserAuthDialog
{
    private readonly Adw.Dialog _dialog = Adw.Dialog.New();
    private readonly WinoInfoBar _launchFailed = new(WinoInfoBarSeverity.Warning, null, Translator.ExternalBrowserAuthenticationDialog_BrowserNotOpened);
    private readonly WinoInfoBar _copied = new(WinoInfoBarSeverity.Success, null, Translator.ExternalBrowserAuthenticationDialog_LinkCopied);
    private readonly Gtk.Button _copy = Gtk.Button.NewWithLabel(Translator.ExternalBrowserAuthenticationDialog_CopyLink);
    private readonly List<IDisposable> _owned = [];
    private Action? _cancelRequested;
    private bool _closed;

    public ExternalBrowserAuthDialog(ExternalBrowserAuthenticationRequest request, Action cancelRequested, Func<string, Task> copy)
    {
        _cancelRequested = cancelRequested;
        _dialog.SetTitle(Translator.ExternalBrowserAuthenticationDialog_Title);
        _dialog.SetContentWidth(440);

        var cancel = WinoA11y.Rename(Gtk.Button.NewWithLabel(Translator.Buttons_Cancel), Translator.Buttons_Cancel);
        var header = Adw.HeaderBar.New();
        header.SetShowStartTitleButtons(false);
        header.SetShowEndTitleButtons(false);
        header.PackStart(cancel);

        var body = Gtk.Box.New(Gtk.Orientation.Vertical, 16);
        body.SetMarginTop(12);
        body.SetMarginStart(18);
        body.SetMarginEnd(18);
        body.SetMarginBottom(18);

        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(32, 32);
        spinner.SetHalign(Gtk.Align.Center);
        WinoA11y.Label(spinner, Translator.ExternalBrowserAuthenticationDialog_Title);
        body.Append(spinner);

        var message = Gtk.Label.New(string.Format(Translator.ExternalBrowserAuthenticationDialog_Message, request.ProviderDisplayName));
        message.SetWrap(true);
        message.SetXalign(0);
        body.Append(message);

        _launchFailed.IsClosable = false;
        _launchFailed.IsOpen = false;
        body.Append(_launchFailed.Root);

        _copy.AddCssClass("flat");
        _copy.SetHalign(Gtk.Align.Start);
        WinoA11y.Rename(_copy, Translator.ExternalBrowserAuthenticationDialog_CopyLink);
        body.Append(_copy);

        _copied.IsClosable = false;
        _copied.IsOpen = false;
        body.Append(_copied.Root);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(body);
        _dialog.SetChild(view);

        var link = request.AuthorizationUri.AbsoluteUri;
        _owned.Add(GtkSignals.OnClicked(cancel, () => _dialog.Close()));
        _owned.Add(GtkSignals.OnClicked(_copy, () => _ = CopyAsync(copy, link)));
        _owned.Add(GtkSignals.Connect<Adw.Dialog>(h => _dialog.OnClosed += h, h => _dialog.OnClosed -= h, (_, _) => OnClosed()));
    }

    public bool IsBrowserLaunchFailed => _launchFailed.IsOpen;

    public bool IsLinkCopied => _copied.IsOpen;

    public void Present(Gtk.Widget owner) => _dialog.Present(owner);

    public void ShowBrowserLaunchFailed()
    {
        if (_closed) return;
        _launchFailed.IsOpen = true;
        _copy.GrabFocus();
    }

    /// <summary>Closes the dialog because the sign-in finished on its own; it does not cancel. Idempotent.</summary>
    public void Close()
    {
        if (_closed) return;
        _cancelRequested = null;
        _dialog.ForceClose();
    }

    private async Task CopyAsync(Func<string, Task> copy, string link)
    {
        try
        {
            await copy(link);
            if (!_closed) _copied.IsOpen = true;
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "The sign-in link could not be copied."); }
    }

    private void OnClosed()
    {
        if (_closed) return;
        _closed = true;
        var cancel = _cancelRequested;
        _cancelRequested = null;
        foreach (var owned in _owned) owned.Dispose();
        _owned.Clear();
        _launchFailed.Dispose();
        _copied.Dispose();
        cancel?.Invoke();
    }
}
