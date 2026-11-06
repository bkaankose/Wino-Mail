using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// Content of the Wino Account popover (Windows ShellWindow WinoAccountFlyout, 320–360 wide):
/// a gradient hero, then either the two benefit cards with Sign in / Register, or the signed-in
/// profile with Manage and Sign out. Actions are raised as events; the presenter runs them.
/// </summary>
public sealed class WinoAccountPopoverViewController : NSViewController
{
    private const double Width = 340;

    private NSStackView _signedOut = null!;
    private NSStackView _signedIn = null!;
    private WinoContactPicture _picture = null!;
    private NSTextField _name = null!;
    private NSTextField _email = null!;

    public event EventHandler? SignInRequested;
    public event EventHandler? RegisterRequested;
    public event EventHandler? ManageRequested;
    public event EventHandler? SignOutRequested;

    public override void LoadView()
    {
        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        root.WidthAnchor.ConstraintEqualTo((nfloat)Width).Active = true;

        // Signed out: hero text, benefit cards, actions.
        var outTitle = WinoStyle.Label(Translator.WinoAccount_Titlebar_SignedOutTitle, NSFont.SystemFontOfSize(20, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        var outDescription = WinoStyle.Label(Translator.WinoAccount_Titlebar_SignedOutDescription, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var outHero = Hero(WinoLayout.VStack(4, outTitle, outDescription));
        var benefits = Stretch(WinoLayout.VStack(8,
            Benefit(WinoIconGlyph.Key, Translator.WinoAccount_Titlebar_SyncBenefitTitle, Translator.WinoAccount_Titlebar_SyncBenefitDescription),
            Benefit(WinoIconGlyph.Info, Translator.WinoAccount_Titlebar_AddonsBenefitTitle, Translator.WinoAccount_Titlebar_AddonsBenefitDescription)));
        var signIn = Button(Translator.WinoAccount_LoginButton_Title, () => SignInRequested?.Invoke(this, EventArgs.Empty));
        signIn.KeyEquivalent = "\r";
        var register = Button(Translator.WinoAccount_RegisterButton_Title, () => RegisterRequested?.Invoke(this, EventArgs.Empty));
        var outActions = WinoLayout.HStack(8, signIn, register);
        outActions.Distribution = NSStackViewDistribution.FillEqually;
        _signedOut = Section(outHero, benefits, outActions);

        // Signed in: profile hero and account actions.
        _picture = new WinoContactPicture(48);
        _name = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        _email = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var profile = WinoLayout.HStack(14, _picture, WinoLayout.VStack(2, _name, _email));
        var inHero = Hero(profile);
        var manage = Button(Translator.WinoAccount_Titlebar_ManageAccount, () => ManageRequested?.Invoke(this, EventArgs.Empty));
        var signOut = Button(Translator.WinoAccount_SignOutButton_Action, () => SignOutRequested?.Invoke(this, EventArgs.Empty));
        var inActions = Stretch(WinoLayout.VStack(8, manage, signOut));
        _signedIn = Section(inHero, inActions);
        _signedIn.Hidden = true;

        foreach (var section in new[] { _signedOut, _signedIn })
        {
            root.AddSubview(section);
            NSLayoutConstraint.ActivateConstraints(
            [
                section.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
                section.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
                section.TopAnchor.ConstraintEqualTo(root.TopAnchor),
                section.BottomAnchor.ConstraintLessThanOrEqualTo(root.BottomAnchor)
            ]);
        }
        View = root;
        WinoAccessibility.Label(root, Translator.WinoAccount_Titlebar_SignedOutTitle);
    }

    /// <summary>Switches between the signed-out and signed-in layouts.</summary>
    public void Update(WinoAccount? account, NSImage? avatar)
    {
        var signedIn = account is not null;
        _signedOut.Hidden = signedIn;
        _signedIn.Hidden = !signedIn;
        if (account is null) return;
        var displayName = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName;
        _name.StringValue = displayName;
        _email.StringValue = account.Email;
        _picture.SetIdentity(displayName, account.Email);
        _picture.Image = avatar;
        if (avatar is null) _picture.Image = null;
    }

    /// <summary>The Windows hero: a soft mint-sky-indigo gradient behind 20pt padding.</summary>
    private static NSView Hero(NSView content)
    {
        var hero = new GradientView();
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(content, hero, 20, 20, 20, 20);
        return hero;
    }

    private static WinoSurfaceView Benefit(WinoIconGlyph glyph, string title, string description)
    {
        var card = new WinoSurfaceView { CornerRadius = 8, Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xF6F6F6, 0.5), WinoStyle.Hex(0xFFFFFF, 0.03)) };
        var tile = new WinoSurfaceView { CornerRadius = 8, Fill = WinoStyle.Accent.ColorWithAlphaComponent(0.15f) };
        WinoLayout.Size(tile, 32, 32);
        var icon = new WinoIconView(glyph, 14, WinoStyle.Accent);
        tile.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints([icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor), icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor)]);
        var titleLabel = WinoStyle.Label(title, WinoStyle.CaptionStrong, WinoStyle.PrimaryText, 0);
        var descriptionLabel = WinoStyle.Label(description, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(2, titleLabel, descriptionLabel);
        var row = WinoLayout.HStack(12, tile, text);
        row.Alignment = NSLayoutAttribute.Top;
        row.EdgeInsets = new NSEdgeInsets(12, 12, 12, 12);
        WinoLayout.Fill(row, card);
        text.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -12).Active = true;
        titleLabel.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor).Active = true;
        descriptionLabel.TrailingAnchor.ConstraintEqualTo(text.TrailingAnchor).Active = true;
        return card;
    }

    /// <summary>Pins every arranged view to the stack's width (Windows HorizontalAlignment=Stretch).</summary>
    private static NSStackView Stretch(NSStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            view.LeadingAnchor.ConstraintEqualTo(stack.LeadingAnchor).Active = true;
            view.TrailingAnchor.ConstraintEqualTo(stack.TrailingAnchor).Active = true;
        }
        return stack;
    }

    private static NSButton Button(string title, Action action)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => action();
        return button;
    }

    /// <summary>A hero that bleeds to the popover edges followed by 16pt-padded content, 16pt apart.</summary>
    private static NSStackView Section(NSView hero, params NSView[] content)
    {
        var stack = WinoLayout.VStack(16, hero);
        foreach (var view in content) stack.AddArrangedSubview(view);
        stack.EdgeInsets = new NSEdgeInsets(0, 0, 16, 0);
        hero.LeadingAnchor.ConstraintEqualTo(stack.LeadingAnchor).Active = true;
        hero.TrailingAnchor.ConstraintEqualTo(stack.TrailingAnchor).Active = true;
        foreach (var view in content)
        {
            view.LeadingAnchor.ConstraintEqualTo(stack.LeadingAnchor, 16).Active = true;
            view.TrailingAnchor.ConstraintEqualTo(stack.TrailingAnchor, -16).Active = true;
        }
        return stack;
    }

    private sealed class GradientView : NSView
    {
        public GradientView() => TranslatesAutoresizingMaskIntoConstraints = false;

        public override void DrawRect(CGRect dirtyRect)
        {
            using var gradient = new NSGradient(
                [WinoStyle.Hex(0x6EE7B7, 0.10), WinoStyle.Hex(0x38BDF8, 0.125), WinoStyle.Hex(0x818CF8, 0.10)],
                [0, 0.5f, 1]);
            gradient.DrawInRect(Bounds, -45);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SignInRequested = null;
            RegisterRequested = null;
            ManageRequested = null;
            SignOutRequested = null;
        }
        base.Dispose(disposing);
    }
}
