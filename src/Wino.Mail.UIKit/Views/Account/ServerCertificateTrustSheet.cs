using Wino.Mail.UIKit.Views.Settings;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Server certificate trust (Windows DialogService.ShowServerCertificateTrustDialogAsync, the Mac sheet): a warning
/// with the certificate facts as a labelled list, a "Certificate details" disclosure with the full certificate dump in
/// a monospaced view, Cancel and Trust. It fails closed: Cancel, swiping the sheet away or anything but Trust refuses,
/// and Return never trusts (the sheet has no text field to submit from).
/// </summary>
internal sealed class ServerCertificateTrustSheet : WinoAccountSheet<bool>
{
    public ServerCertificateTrustSheet(string summary, byte[]? certificateRawData) : base(Translator.IMAPSetupDialog_CertificateTrustTitle)
    {
        var caution = new UIImageView(WinoIcons.Image(WinoIconGlyph.Warning, 40, WinoStyle.Caution)) { ContentMode = UIViewContentMode.ScaleAspectFit, IsAccessibilityElement = false };
        WinoLayout.Size(caution, 40, 40);
        AddRow(WinoLayout.HStack(0, caution, WinoLayout.Spacer()));

        using var certificate = TryLoad(certificateRawData);
        var (paragraphs, rows, closing) = Parse(summary);
        // A caller that sends only free text still gets the certificate facts from the raw data.
        if (rows.Count == 0 && certificate is not null) rows = FactsFrom(certificate);

        foreach (var paragraph in paragraphs) AddRow(Paragraph(paragraph));
        if (rows.Count > 0) AddRow(Facts(rows));
        foreach (var paragraph in closing) AddRow(Paragraph(paragraph));

        if (certificate is not null)
        {
            var details = DetailsView(certificate.ToString(verbose: true));
            details.Hidden = true;
            var disclosure = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, HorizontalAlignment = UIControlContentHorizontalAlignment.Leading };
            disclosure.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            void ApplyDisclosure()
            {
                var configuration = UIButtonConfiguration.PlainButtonConfiguration;
                configuration.Title = Translator.IMAPSetupDialog_CertificateDetails;
                configuration.Image = WinoIcons.Image(details.Hidden ? WinoIconGlyph.ChevronRight : WinoIconGlyph.ChevronDown, 12, WinoStyle.Accent);
                configuration.ImagePadding = 8;
                configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
                disclosure.Configuration = configuration;
                disclosure.AccessibilityValue = details.Hidden ? null : Translator.IMAPSetupDialog_CertificateDetails;
            }
            disclosure.PrimaryActionTriggered += (_, _) =>
            {
                details.Hidden = !details.Hidden;
                ApplyDisclosure();
            };
            ApplyDisclosure();
            var group = WinoLayout.VStack(4, disclosure, details);
            group.Alignment = UIStackViewAlignment.Fill;
            AddRow(group);
        }

        Primary.Title = Translator.Buttons_Trust;
        Primary.Style = UIBarButtonItemStyle.Plain;
        Primary.TintColor = WinoStyle.Critical;
    }

    // Nothing gets focus: the risky choice always takes a deliberate tap.
    protected override UIView? InitialResponder => null;

    protected override Task PrimaryAsync()
    {
        Finish(true);
        return Task.CompletedTask;
    }

    private static X509Certificate2? TryLoad(byte[]? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        try { return X509CertificateLoader.LoadCertificate(raw); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// The shared ViewModels send "intro, blank line, Label: value lines, blank line, closing". Lines whose label is
    /// one of the certificate labels become rows; other text stays as paragraphs (same parser as the Mac).
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

    private static UILabel Paragraph(string text) => WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 0);

    /// <summary>Each fact as a secondary caption above its value (fingerprints wrap by character), in a grouped card.</summary>
    private static UIView Facts(List<(string Label, string Value)> rows)
    {
        var list = WinoLayout.Insets(WinoLayout.VStack(10), 12, 14, 12, 14);
        list.Alignment = UIStackViewAlignment.Fill;
        foreach (var (label, value) in rows)
        {
            var name = WinoStyle.Label(label, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
            var text = WinoStyle.Label(string.IsNullOrWhiteSpace(value) ? "—" : value, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
            text.LineBreakMode = UILineBreakMode.CharacterWrap;
            var pair = WinoLayout.VStack(2, name, text);
            pair.Alignment = UIStackViewAlignment.Fill;
            pair.IsAccessibilityElement = true;
            pair.AccessibilityLabel = label;
            pair.AccessibilityValue = text.Text;
            list.AddArrangedSubview(pair);
        }
        var card = new WinoSurfaceView { Fill = WinoStyle.GroupFill, CornerRadius = WinoStyle.ControlRadius, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(list, card);
        return card;
    }

    private static UITextView DetailsView(string text)
    {
        var view = new UITextView
        {
            Editable = false,
            Selectable = true,
            Text = text,
            Font = UIFont.GetMonospacedSystemFont(11, UIFontWeight.Regular),
            BackgroundColor = WinoStyle.GroupFill,
            TextContainerInset = new UIEdgeInsets(8, 6, 8, 6),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        view.Layer.CornerRadius = (nfloat)WinoStyle.ControlRadius;
        view.HeightAnchor.ConstraintEqualTo(200).Active = true;
        WinoAccessibility.Label(view, Translator.IMAPSetupDialog_CertificateDetails);
        return view;
    }
}
