using Wino.Core.Domain;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// Windows ImapValidationFailedDialog as an alert: the error, the protocol log in a scrolling monospace view, Close and
/// Copy diagnostics (macOS AppKitDialogService does the same).
/// </summary>
internal static class ImapValidationFailedDialog
{
    public const string Copy = "copy";

    // A protocol log can run to megabytes; the text view keeps only the end, where the failure is.
    private const int MaximumLogLength = 2 * 1024 * 1024;

    public static AlertRequest Create(string errorMessage, string protocolLog)
    {
        return new AlertRequest(Translator.ImapValidationFailedDialog_Title, errorMessage,
            [
                new AlertResponse(Copy, Translator.ImapValidationFailedDialog_Copy),
                new AlertResponse(AlertPresenter.Closed, Translator.Buttons_Close, Adw.ResponseAppearance.Suggested),
            ])
        {
            ExtraChild = string.IsNullOrWhiteSpace(protocolLog) ? null : LogView(protocolLog),
            DefaultResponse = AlertPresenter.Closed,
            PreferWideLayout = true,
        };
    }

    private static Gtk.Widget LogView(string protocolLog)
    {
        var heading = Gtk.Label.New(Translator.ImapValidationFailedDialog_ProtocolLog);
        heading.AddCssClass("heading");
        heading.SetXalign(0);
        var text = protocolLog.Length > MaximumLogLength ? protocolLog[^MaximumLogLength..] : protocolLog;
        var view = Gtk.TextView.New();
        view.SetMonospace(true);
        view.SetEditable(false);
        view.SetCursorVisible(false);
        view.SetWrapMode(Gtk.WrapMode.WordChar);
        view.SetTopMargin(6);
        view.SetBottomMargin(6);
        view.SetLeftMargin(6);
        view.SetRightMargin(6);
        view.GetBuffer().SetText(text, -1);
        Presentation.Linux.WinoA11y.Label(view, Translator.ImapValidationFailedDialog_ProtocolLog);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetMinContentHeight(240);
        scroller.SetMaxContentHeight(240);
        scroller.SetChild(view);
        scroller.AddCssClass("card");
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        box.Append(heading);
        box.Append(scroller);
        return box;
    }
}
