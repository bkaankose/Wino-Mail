using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The untrusted server certificate question (macOS ServerCertificateTrustSheet) as an alert: intro, problems, facts and
/// closing text, the certificate details behind a disclosure, and for a dangerous certificate an acknowledgement check box
/// that must be ticked before Accept anyway becomes available. Build it on the main thread.
/// </summary>
internal sealed class ServerCertificateTrustDialog : IDisposable
{
    public const string Trust = "trust";
    public const string Cancel = "cancel";

    private readonly Gtk.CheckButton? _acknowledgement;
    private IDisposable? _subscription;

    public ServerCertificateTrustDialog(ServerCertificateTrustPrompt prompt)
    {
        var body = new List<string> { prompt.Intro };
        if (prompt.Problems.Length > 0)
            body.Add(prompt.ProblemsHeader + "\n" + string.Join("\n", prompt.Problems.Select(static problem => "• " + problem)));
        if (prompt.Facts.Length > 0)
            body.Add(string.Join("\n", prompt.Facts.Select(static fact => $"{fact.Label}: {fact.Value}")));
        body.Add(prompt.Closing);

        var extra = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        if (Details(prompt.CertificateRawData) is { } details) extra.Append(details);
        if (prompt.IsDangerous)
        {
            _acknowledgement = Gtk.CheckButton.NewWithLabel(prompt.AcknowledgementText);
            if (_acknowledgement.GetChild() is Gtk.Label label) label.SetWrap(true);
            extra.Append(_acknowledgement);
        }

        Request = new AlertRequest(prompt.Title, string.Join("\n\n", body.Where(static text => !string.IsNullOrWhiteSpace(text))),
            [
                new AlertResponse(Cancel, Translator.Buttons_Cancel),
                new AlertResponse(Trust, prompt.AcceptButtonText,
                    prompt.IsDangerous ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested, Enabled: !prompt.IsDangerous),
            ])
        {
            CloseResponse = Cancel,
            DefaultResponse = Cancel,
            ExtraChild = extra.GetFirstChild() is null ? null : extra,
            PreferWideLayout = true,
            Configured = Configure,
        };
#if DEBUG
        DebugCurrent = this;
#endif
    }

    public AlertRequest Request { get; }

    private void Configure(Adw.AlertDialog dialog)
    {
        if (_acknowledgement is not { } check) return;
        _subscription = GtkSignals.OnToggled(check, () => dialog.SetResponseEnabled(Trust, check.GetActive()));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
    }

    private static Gtk.Widget? Details(byte[] raw)
    {
        if (raw.Length == 0) return null;
        string text;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(raw);
            text = certificate.ToString(verbose: true);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

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
        WinoA11y.Label(view, Translator.IMAPSetupDialog_CertificateDetails);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetMinContentHeight(200);
        scroller.SetMaxContentHeight(200);
        scroller.SetChild(view);
        scroller.AddCssClass("card");
        var expander = Gtk.Expander.New(Translator.IMAPSetupDialog_CertificateDetails);
        expander.SetChild(scroller);
        return expander;
    }

#if DEBUG
    public static ServerCertificateTrustDialog? DebugCurrent { get; private set; }

    public string DebugDescribe()
        => $"dangerous={_acknowledgement is not null} acknowledged={_acknowledgement?.GetActive()} heading='{Request.Heading}'\n{Request.Body}\n";

    public void DebugAcknowledge(bool on) => _acknowledgement?.SetActive(on);
#endif
}
