using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Settings root's first row, shaped like the Settings app's Apple Account card but for the mail accounts, which
/// every user has: the first account's picture (two overlapping when there are more), its name, and the account count
/// with the providers. It opens Manage accounts; without accounts it invites to add one.
/// </summary>
public sealed class MailAccountsCardCell : UITableViewCell
{
    private const double PictureSize = 60;
    private const double PairSize = 42;

    private readonly WinoContactPicture _single = new(PictureSize);
    private readonly WinoContactPicture _back = new(PairSize);
    private readonly WinoContactPicture _front = new(PairSize) { Stroke = WinoSettingsStyle.CardFill, StrokeWidth = 2 };
    private readonly UIView _pair = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly WinoSurfaceView _placeholder = new() { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xC7C7CC), WinoStyle.Hex(0x48484A)), CornerRadius = PictureSize / 2 };
    private readonly UILabel _title = WinoStyle.Label(string.Empty, WinoStyle.Scaled(20, UIFontWeight.Medium), WinoStyle.PrimaryText, 2);
    private readonly UILabel _subtitle = WinoStyle.Label(string.Empty, WinoStyle.Scaled(13), WinoStyle.SecondaryText, 2);

    public MailAccountsCardCell() : base(UITableViewCellStyle.Default, (string?)null)
    {
        BackgroundColor = WinoSettingsStyle.CardFill;
        Accessory = UITableViewCellAccessory.DisclosureIndicator;
        AccessibilityIdentifier = "SettingsMailAccountsCard";

        var add = new WinoIconView(WinoIconGlyph.PersonAdd, 30, UIColor.White) { TranslatesAutoresizingMaskIntoConstraints = false };
        _placeholder.AddSubview(add);
        WinoLayout.Size(_placeholder, PictureSize, PictureSize);
        NSLayoutConstraint.ActivateConstraints([add.CenterXAnchor.ConstraintEqualTo(_placeholder.CenterXAnchor), add.CenterYAnchor.ConstraintEqualTo(_placeholder.CenterYAnchor)]);

        // The second account sits behind the first, offset to the top left like a contact group picture.
        _back.TranslatesAutoresizingMaskIntoConstraints = false;
        _front.TranslatesAutoresizingMaskIntoConstraints = false;
        _pair.AddSubview(_back);
        _pair.AddSubview(_front);
        WinoLayout.Size(_pair, PictureSize, PictureSize);
        NSLayoutConstraint.ActivateConstraints(
        [
            _back.TopAnchor.ConstraintEqualTo(_pair.TopAnchor),
            _back.LeadingAnchor.ConstraintEqualTo(_pair.LeadingAnchor),
            _front.BottomAnchor.ConstraintEqualTo(_pair.BottomAnchor),
            _front.TrailingAnchor.ConstraintEqualTo(_pair.TrailingAnchor)
        ]);

        var text = WinoLayout.VStack(2, _title, _subtitle);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var row = WinoLayout.HStack(14, _single, _pair, _placeholder, text);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, ContentView, 10, 14, 10, 8);

        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        Update([]);
    }

    public void Update(IEnumerable<IAccountProviderDetailViewModel> accounts)
    {
        var list = accounts.ToList();
        _placeholder.Hidden = list.Count > 0;
        _single.Hidden = list.Count != 1;
        _pair.Hidden = list.Count < 2;

        if (list.Count == 0)
        {
            _title.Text = Translator.SettingsOptions_AccountsCardEmptyTitle;
            _title.Font = WinoStyle.Scaled(17);
            _title.TextColor = WinoStyle.Accent;
            _subtitle.Text = Translator.SettingsOptions_AccountsCardEmptySubtitle;
        }
        else
        {
            var first = list[0];
            _title.Text = first.StartupEntityTitle;
            _title.Font = WinoStyle.Scaled(20, UIFontWeight.Medium);
            _title.TextColor = WinoStyle.PrimaryText;
            var providers = string.Join(", ", list.Select(account => account.ProviderDetail?.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
            var lead = list.Count == 1 ? first.StartupEntityAddresses : string.Format(Translator.SettingsOptions_AccountsCardCount, list.Count);
            _subtitle.Text = string.Join(" · ", new[] { lead, providers }.Where(part => !string.IsNullOrWhiteSpace(part)));

            if (list.Count == 1) _single.SetIdentity(first.StartupEntityTitle, first.StartupEntityAddresses);
            else
            {
                _front.SetIdentity(first.StartupEntityTitle, first.StartupEntityAddresses);
                _back.SetIdentity(list[1].StartupEntityTitle, list[1].StartupEntityAddresses);
            }
        }

        AccessibilityLabel = $"{SettingsCatalog.Title(WinoPage.ManageAccountsPage)}, {_title.Text}";
        AccessibilityValue = _subtitle.Text;
    }
}

/// <summary>
/// The Wino Account under the mail accounts card, an ordinary row like the Settings app's Family row: signed in, the
/// account picture and name; signed out, the Wino Account title with a quiet Sign in before the chevron.
/// </summary>
public sealed class WinoAccountRowCell : UITableViewCell
{
    private const double GlyphSize = 22;
    private const double PictureSize = 28;

    public WinoAccountRowCell() : base(UITableViewCellStyle.Default, (string?)null)
    {
        BackgroundColor = WinoSettingsStyle.CardFill;
        Accessory = UITableViewCellAccessory.DisclosureIndicator;
        AccessibilityIdentifier = "SettingsWinoAccountRow";
        SeparatorInset = new UIEdgeInsets(0, 14 + 28 + 12, 0, 0);
        Update(false, string.Empty, string.Empty, null);
    }

    public void Update(bool signedIn, string displayName, string email, UIImage? picture)
    {
        var title = SettingsCatalog.Title(WinoPage.WinoAccountManagementPage);
        var content = UIListContentConfiguration.ValueCellConfiguration;
        content.Text = signedIn && !string.IsNullOrWhiteSpace(displayName) ? displayName : title;
        content.TextProperties.Font = WinoStyle.Scaled(16);
        content.TextProperties.Color = WinoStyle.PrimaryText;
        content.SecondaryText = signedIn ? title : Translator.WinoAccount_LoginButton_Title;
        content.SecondaryTextProperties.Font = WinoStyle.Scaled(15);
        content.SecondaryTextProperties.Color = signedIn ? WinoStyle.SecondaryText : WinoStyle.Accent;
        content.PrefersSideBySideTextAndSecondaryText = true;
        var glyph = WinoStyle.ScaledIcon(GlyphSize, TraitCollection);
        var pictureSize = WinoStyle.ScaledIcon(PictureSize, TraitCollection, 1.4);
        content.Image = signedIn ? Circle(picture, displayName, email) : WinoIcons.Image(WinoIconGlyph.Person, glyph, WinoStyle.Accent);
        content.ImageProperties.ReservedLayoutSize = new CGSize(glyph + 6, glyph);
        content.ImageProperties.MaximumSize = signedIn ? new CGSize(pictureSize, pictureSize) : new CGSize(glyph, glyph);
        content.ImageToTextPadding = 12;
        content.DirectionalLayoutMargins = new NSDirectionalEdgeInsets(11, 14, 11, 12);
        ContentConfiguration = content;
        AccessibilityHint = signedIn ? email : null;
    }

    /// <summary>The account picture, or its initials on the palette colour, as a round image for the row's image slot.</summary>
    private static UIImage Circle(UIImage? picture, string displayName, string email)
    {
        var avatar = new WinoContactPicture(PictureSize) { Frame = new CGRect(0, 0, PictureSize, PictureSize) };
        avatar.SetIdentity(string.IsNullOrWhiteSpace(displayName) ? email : displayName, email);
        avatar.Image = picture;
        avatar.LayoutIfNeeded();
        var renderer = new UIGraphicsImageRenderer(new CGSize(PictureSize, PictureSize));
        return renderer.CreateImage(context =>
        {
            UIBezierPath.FromOval(new CGRect(0, 0, PictureSize, PictureSize)).AddClip();
            avatar.Layer.RenderInContext(context.CGContext);
        }).ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }
}
