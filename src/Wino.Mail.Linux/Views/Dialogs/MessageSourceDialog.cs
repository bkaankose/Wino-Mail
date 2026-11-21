using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The MIME source of a message in a read-only monospace view with Copy (Windows <c>MessageSourceDialog</c>, macOS
/// message source sheet).
/// </summary>
internal static class MessageSourceDialog
{
    /// <summary>Main thread. Completes when the dialog closed.</summary>
    public static Task PresentAsync(Gtk.Widget owner, string source)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source ??= string.Empty;

        var view = Gtk.TextView.New();
        view.SetEditable(false);
        view.SetCursorVisible(false);
        view.SetMonospace(true);
        view.SetWrapMode(Gtk.WrapMode.None);
        view.SetTopMargin(6);
        view.SetBottomMargin(6);
        view.SetLeftMargin(6);
        view.SetRightMargin(6);
        WinoA11y.Label(view, Translator.MessageSourceDialog_Title);
        // A NUL would end the text at the native boundary.
        view.GetBuffer().SetText(source.Replace('\0', '␀'), -1);

        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(view);
        var toasts = Adw.ToastOverlay.New();
        toasts.SetChild(scroller);

        var copy = Gtk.Button.New();
        copy.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Copy, Translator.Buttons_Copy));
        WinoA11y.Label(copy, Translator.Buttons_Copy);
        var header = Adw.HeaderBar.New();
        header.PackStart(copy);
        var toolbar = Adw.ToolbarView.New();
        toolbar.AddTopBar(header);
        toolbar.SetContent(toasts);

        var dialog = Adw.Dialog.New();
        dialog.SetTitle(Translator.MessageSourceDialog_Title);
        dialog.SetContentWidth((int)Math.Clamp(owner.GetWidth() * 0.7, 480, 760));
        dialog.SetContentHeight((int)Math.Min(560, Math.Max(260, owner.GetHeight() * 0.8)));
        dialog.SetFollowsContentSize(false);
        dialog.SetChild(toolbar);

        var subscriptions = new List<IDisposable>
        {
            GtkSignals.OnClicked(copy, () =>
            {
                Gdk.Display.GetDefault()?.GetClipboard().SetText(source);
                toasts.AddToast(Adw.Toast.New(Translator.MessageSourceDialog_Copied));
            }),
        };
        subscriptions.Add(GtkSignals.Connect<Adw.Dialog>(h => dialog.OnClosed += h, h => dialog.OnClosed -= h, (_, _) =>
        {
            foreach (var subscription in subscriptions) subscription.Dispose();
            subscriptions.Clear();
            completion.TrySetResult();
        }));
        dialog.Present(owner);
        dialog.SetFocus(view);
        return completion.Task;
    }
}
