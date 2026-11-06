using Wino.Mail.UIKit.Views.Settings;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Server certificate trust (Windows ServerCertificateTrustDialog, the Mac sheet): the shared prompt's intro, its
/// problems, the certificate facts as a labelled list, a "Certificate details" disclosure with the full certificate
/// dump in a monospaced view, Cancel and accept. It fails closed: Cancel, swiping the sheet away or anything but accept
/// refuses, and Return never accepts (the sheet has no text field to submit from). A dangerous prompt shows its
/// warning in red and keeps accept disabled until the acknowledgement switch is on.
/// </summary>
internal sealed class ServerCertificateTrustSheet : WinoAccountSheet<bool>
{
    private readonly UISwitch? _acknowledgement;

    public ServerCertificateTrustSheet(ServerCertificateTrustPrompt prompt) : base(prompt.Title)
    {
        var caution = new UIImageView(WinoIcons.Image(WinoIconGlyph.Warning, 40, prompt.IsDangerous ? WinoStyle.Critical : WinoStyle.Caution)) { ContentMode = UIViewContentMode.ScaleAspectFit, IsAccessibilityElement = false };
        WinoLayout.Size(caution, 40, 40);
        AddRow(WinoLayout.HStack(0, caution, WinoLayout.Spacer()));

        AddRow(prompt.IsDangerous
            ? WinoStyle.Label(prompt.Intro, WinoStyle.Body, WinoStyle.Critical, 0)
            : Paragraph(prompt.Intro));

        if (prompt.Problems.Length > 0)
        {
            var problems = WinoLayout.VStack(4, WinoStyle.Label(prompt.ProblemsHeader, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0));
            problems.Alignment = UIStackViewAlignment.Fill;
            foreach (var problem in prompt.Problems) problems.AddArrangedSubview(Paragraph("• " + problem));
            AddRow(problems);
        }

        if (prompt.Facts.Length > 0) AddRow(Facts(prompt.Facts));
        AddRow(Paragraph(prompt.Closing));

        using var certificate = TryLoad(prompt.CertificateRawData);
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

        if (prompt.IsDangerous)
        {
            _acknowledgement = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
            _acknowledgement.ValueChanged += (_, _) => Refresh();
            var label = WinoStyle.Label(prompt.AcknowledgementText, WinoStyle.Body, WinoStyle.PrimaryText, 0);
            label.IsAccessibilityElement = false;
            _acknowledgement.AccessibilityLabel = prompt.AcknowledgementText;
            var row = WinoLayout.HStack(12, label, _acknowledgement);
            row.Alignment = UIStackViewAlignment.Center;
            AddRow(row);
        }

        Primary.Title = prompt.AcceptButtonText;
        Primary.Style = UIBarButtonItemStyle.Plain;
        Primary.TintColor = WinoStyle.Critical;
        Refresh();
    }

    // Nothing gets focus: the risky choice always takes a deliberate tap.
    protected override UIView? InitialResponder => null;

    protected override bool CanSubmit => _acknowledgement is null || _acknowledgement.On;

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

    private static UILabel Paragraph(string text) => WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 0);

    /// <summary>Each fact as a secondary caption above its value (fingerprints wrap by character), in a grouped card.</summary>
    private static UIView Facts(ServerCertificateFact[] facts)
    {
        var list = WinoLayout.Insets(WinoLayout.VStack(10), 12, 14, 12, 14);
        list.Alignment = UIStackViewAlignment.Fill;
        foreach (var fact in facts)
        {
            var name = WinoStyle.Label(fact.Label, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
            var text = WinoStyle.Label(fact.Value, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);
            text.LineBreakMode = UILineBreakMode.CharacterWrap;
            var pair = WinoLayout.VStack(2, name, text);
            pair.Alignment = UIStackViewAlignment.Fill;
            pair.IsAccessibilityElement = true;
            pair.AccessibilityLabel = fact.Label;
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
