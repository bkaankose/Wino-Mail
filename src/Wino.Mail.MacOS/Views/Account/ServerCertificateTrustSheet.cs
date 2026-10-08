using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Server certificate trust (Windows DialogService.ShowServerCertificateTrustDialogAsync): a warning
/// sheet with the certificate facts as a labelled list, a "Certificate details" disclosure with the
/// full certificate dump in a monospaced view, and Trust / Cancel. Cancel is the Escape button and
/// Trust has no key equivalent, so the risky choice always takes a deliberate click.
/// </summary>
internal sealed class ServerCertificateTrustSheet : WinoAccountSheet<bool>
{
    private const double ContentWidth = 472;
    private readonly NSScrollView? _details;

    public ServerCertificateTrustSheet(string summary, byte[]? certificateRawData) : base(520)
    {
        Sheet.Title = Translator.IMAPSetupDialog_CertificateTrustTitle;

        var caution = new NSImageView { Image = NSImage.ImageNamed("NSCaution"), ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false };
        caution.AccessibilityElement = false;
        WinoLayout.Size(caution, 40, 40);
        var heading = WinoStyle.Label(Translator.IMAPSetupDialog_CertificateTrustTitle, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        heading.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        var header = WinoLayout.HStack(12, caution, heading);
        AddRow(header);

        using var certificate = TryLoad(certificateRawData);
        var (paragraphs, rows, closing) = Parse(summary);
        // A caller that sends only free text still gets the certificate facts from the raw data.
        if (rows.Count == 0 && certificate is not null) rows = FactsFrom(certificate);

        foreach (var paragraph in paragraphs) AddRow(Paragraph(paragraph));
        if (rows.Count > 0) AddRow(FactsGrid(rows));
        foreach (var paragraph in closing) AddRow(Paragraph(paragraph));

        if (certificate is not null)
        {
            _details = DetailsView(certificate.ToString(verbose: true));
            _details.Hidden = true;
            var disclosure = new NSButton { Title = string.Empty, BezelStyle = NSBezelStyle.Disclosure, TranslatesAutoresizingMaskIntoConstraints = false };
            disclosure.SetButtonType(NSButtonType.PushOnPushOff);
            WinoAccessibility.Label(disclosure, Translator.IMAPSetupDialog_CertificateDetails);
            var label = WinoStyle.Label(Translator.IMAPSetupDialog_CertificateDetails, WinoStyle.Body, WinoStyle.PrimaryText);
            label.AccessibilityElement = false;
            disclosure.Activated += (_, _) => ToggleDetails(disclosure.State == NSCellStateValue.On);
            var toggle = WinoLayout.HStack(6, disclosure, label);
            // The label toggles too (on the label only, so a click on the triangle is not counted twice).
            label.AddGestureRecognizer(new NSClickGestureRecognizer(() =>
            {
                disclosure.State = disclosure.State == NSCellStateValue.On ? NSCellStateValue.Off : NSCellStateValue.On;
                ToggleDetails(disclosure.State == NSCellStateValue.On);
            }));
            AddRow(toggle, stretch: false);
            AddRow(_details);
        }

        Secondary.Title = Translator.Buttons_Cancel;
        Primary.Title = Translator.Buttons_Trust;
        Primary.KeyEquivalent = string.Empty;
    }

    protected override NSView? InitialResponder => Secondary;

    protected override Task PrimaryAsync()
    {
        Finish(true);
        return Task.CompletedTask;
    }

    private void ToggleDetails(bool show)
    {
        if (_details is null) return;
        _details.Hidden = !show;
        Resize();
    }

    private static X509Certificate2? TryLoad(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        try { return X509CertificateLoader.LoadCertificate(raw); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// The shared ViewModels send "intro, blank line, Label: value lines, blank line, closing". Lines
    /// whose label is one of the certificate labels become rows; other text stays as paragraphs.
    /// </summary>
    private static (List<string> Intro, List<(string Label, string Value)> Rows, List<string> Closing) Parse(string? summary)
    {
        var labels = new[]
        {
            Translator.IMAPSetupDialog_CertificateProtocol, Translator.IMAPSetupDialog_CertificateEndpoint,
            Translator.IMAPSetupDialog_CertificateSubject, Translator.IMAPSetupDialog_CertificateSans,
            Translator.IMAPSetupDialog_CertificateIssuer, Translator.IMAPSetupDialog_CertificateValidFrom,
            Translator.IMAPSetupDialog_CertificateValidTo, Translator.IMAPSetupDialog_CertificateFingerprint,
            Translator.IMAPSetupDialog_CertificateFailureReason
        };
        var intro = new List<string>();
        var rows = new List<(string, string)>();
        var closing = new List<string>();
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            (rows.Count == 0 ? intro : closing).Add(string.Join(Environment.NewLine, paragraph));
            paragraph.Clear();
        }

        foreach (var raw in (summary ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            var label = separator > 0 ? line[..separator] : null;
            if (label is not null && labels.Contains(label, StringComparer.Ordinal))
            {
                Flush();
                rows.Add((label, line[(separator + 2)..]));
                continue;
            }
            if (line.Length == 0) { Flush(); continue; }
            paragraph.Add(line);
        }
        Flush();
        return (intro, rows, closing);
    }

    private static List<(string Label, string Value)> FactsFrom(X509Certificate2 certificate) =>
    [
        (Translator.IMAPSetupDialog_CertificateSubject, certificate.Subject),
        (Translator.IMAPSetupDialog_CertificateIssuer, certificate.Issuer),
        (Translator.IMAPSetupDialog_CertificateValidFrom, certificate.NotBefore.ToUniversalTime().ToString("u")),
        (Translator.IMAPSetupDialog_CertificateValidTo, certificate.NotAfter.ToUniversalTime().ToString("u")),
        (Translator.IMAPSetupDialog_CertificateFingerprint, certificate.GetCertHashString(HashAlgorithmName.SHA256))
    ];

    private static NSTextField Paragraph(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        label.PreferredMaxLayoutWidth = (nfloat)ContentWidth;
        label.Selectable = true;
        return label;
    }

    /// <summary>Right-aligned secondary labels and selectable values (fingerprints wrap by character).</summary>
    private static NSGridView FactsGrid(List<(string Label, string Value)> rows)
    {
        var grid = new NSGridView { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 6, ColumnSpacing = 10 };
        foreach (var (label, value) in rows)
        {
            var name = WinoStyle.Label(label, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
            name.Alignment = NSTextAlignment.Right;
            var text = WinoStyle.Label(string.IsNullOrWhiteSpace(value) ? "—" : value, NSFont.SystemFontOfSize(12), WinoStyle.PrimaryText, 0);
            text.Selectable = true;
            text.LineBreakMode = NSLineBreakMode.CharWrapping;
            text.PreferredMaxLayoutWidth = 300;
            WinoAccessibility.Label(text, label);
            grid.AddRow([name, text]);
        }
        grid.GetColumn(0).X = NSGridCellPlacement.Trailing;
        grid.RowAlignment = NSGridRowAlignment.FirstBaseline;
        return grid;
    }

    private static NSScrollView DetailsView(string text)
    {
        var view = new NSTextView(new CGRect(0, 0, ContentWidth, 200))
        {
            Editable = false,
            Selectable = true,
            RichText = false,
            DrawsBackground = true,
            BackgroundColor = NSColor.TextBackground,
            TextColor = NSColor.Label,
            Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular),
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            MaxSize = new CGSize(float.MaxValue, float.MaxValue),
            Value = text
        };
        view.TextContainer!.WidthTracksTextView = true;
        view.TextContainerInset = new CGSize(6, 6);
        WinoAccessibility.Label(view, Translator.IMAPSetupDialog_CertificateDetails);
        var scroll = new NSScrollView
        {
            DocumentView = view,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            BorderType = NSBorderType.BezelBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        scroll.HeightAnchor.ConstraintEqualTo(200).Active = true;
        return scroll;
    }
}
