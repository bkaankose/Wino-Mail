using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Server certificate trust (Windows ServerCertificateTrustDialog): the shared prompt's intro, its problems, the
/// certificate facts as a labelled list, a "Certificate details" disclosure with the full certificate dump in a
/// monospaced view, and Cancel / accept. Cancel is the Escape button and accept has no key equivalent, so the risky
/// choice always takes a deliberate click. A dangerous prompt shows its warning in red and keeps accept disabled
/// until the acknowledgement checkbox is ticked.
/// </summary>
internal sealed class ServerCertificateTrustSheet : WinoAccountSheet<bool>
{
    private const double ContentWidth = 472;
    private readonly NSScrollView? _details;
    private readonly NSButton? _acknowledgement;

    public ServerCertificateTrustSheet(ServerCertificateTrustPrompt prompt) : base(520)
    {
        Sheet.Title = prompt.Title;

        var caution = new NSImageView { Image = NSImage.ImageNamed("NSCaution"), ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false };
        caution.AccessibilityElement = false;
        WinoLayout.Size(caution, 40, 40);
        var heading = WinoStyle.Label(prompt.Title, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        heading.AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        var header = WinoLayout.HStack(12, caution, heading);
        AddRow(header);

        var intro = Paragraph(prompt.Intro);
        if (prompt.IsDangerous)
        {
            intro.TextColor = NSColor.SystemRed;
            intro.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
        }
        AddRow(intro);

        if (prompt.Problems.Length > 0)
        {
            var problemsHeader = WinoStyle.Label(prompt.ProblemsHeader, NSFont.SystemFontOfSize(13, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
            var problems = WinoLayout.VStack(4, problemsHeader);
            problems.Alignment = NSLayoutAttribute.Leading;
            foreach (var problem in prompt.Problems) problems.AddArrangedSubview(Paragraph("• " + problem));
            AddRow(problems);
        }

        if (prompt.Facts.Length > 0) AddRow(FactsGrid(prompt.Facts));
        AddRow(Paragraph(prompt.Closing));

        using var certificate = TryLoad(prompt.CertificateRawData);
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

        if (prompt.IsDangerous)
        {
            _acknowledgement = NSButton.CreateCheckbox(prompt.AcknowledgementText, () => Primary.Enabled = CanSubmit);
            AddRow(_acknowledgement, stretch: false);
        }

        Secondary.Title = Translator.Buttons_Cancel;
        Primary.Title = prompt.AcceptButtonText;
        Primary.KeyEquivalent = string.Empty;
        Primary.Enabled = CanSubmit;
    }

    protected override NSView? InitialResponder => Secondary;

    protected override bool CanSubmit => _acknowledgement is null || _acknowledgement.State == NSCellStateValue.On;

    protected override Task PrimaryAsync()
    {
        if (CanSubmit) Finish(true);
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

    private static NSTextField Paragraph(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        label.PreferredMaxLayoutWidth = (nfloat)ContentWidth;
        label.Selectable = true;
        return label;
    }

    /// <summary>Right-aligned secondary labels and selectable values (fingerprints wrap by character).</summary>
    private static NSGridView FactsGrid(ServerCertificateFact[] facts)
    {
        var grid = new NSGridView { TranslatesAutoresizingMaskIntoConstraints = false, RowSpacing = 6, ColumnSpacing = 10 };
        foreach (var fact in facts)
        {
            var name = WinoStyle.Label(fact.Label, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
            name.Alignment = NSTextAlignment.Right;
            var text = WinoStyle.Label(fact.Value, NSFont.SystemFontOfSize(12), WinoStyle.PrimaryText, 0);
            text.Selectable = true;
            text.LineBreakMode = NSLineBreakMode.CharWrapping;
            text.PreferredMaxLayoutWidth = 300;
            WinoAccessibility.Label(text, fact.Label);
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
