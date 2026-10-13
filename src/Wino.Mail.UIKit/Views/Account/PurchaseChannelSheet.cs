using Wino.Mail.UIKit.Views.Settings;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Where to buy Unlimited Accounts (port of UnlimitedAccountsPurchaseChannelDialog, the Mac sheet). The Apple App
/// Store is listed first when the platform sells through it; Wino Account (Stripe) needs a signed-in Wino Account; the
/// Microsoft Store appears when the platform sells through it. Each channel is a selectable card; Continue returns the
/// selected channel, Cancel null.
/// </summary>
internal sealed class PurchaseChannelSheet : WinoAccountSheet<UnlimitedAccountsPurchaseChannel?>
{
    private readonly List<ChannelOption> _options = [];
    private ChannelOption? _selected;

    public PurchaseChannelSheet(bool isWinoAccountAvailable, bool microsoftStore, bool appleAppStore) : base(Translator.UnlimitedAccountsPurchaseDialog_Title)
    {
        AddRow(WinoStyle.Label(Translator.UnlimitedAccountsPurchaseDialog_Description, WinoStyle.Body, WinoStyle.SecondaryText, 0));

        var options = WinoLayout.VStack(10);
        options.Alignment = UIStackViewAlignment.Fill;
        if (appleAppStore)
            options.AddArrangedSubview(Option(UnlimitedAccountsPurchaseChannel.AppleAppStore, WinoIconGlyph.Apple,
                Translator.UnlimitedAccountsPurchaseDialog_AppleAppStoreTitle, Translator.IOS_UnlimitedAccountsPurchaseDialog_AppleAppStoreDescription, true));
        if (microsoftStore)
            options.AddArrangedSubview(Option(UnlimitedAccountsPurchaseChannel.MicrosoftStore, WinoIconGlyph.MicrosoftStore,
                Translator.UnlimitedAccountsPurchaseDialog_MicrosoftStoreTitle, Translator.UnlimitedAccountsPurchaseDialog_MicrosoftStoreDescription, true));
        options.AddArrangedSubview(Option(UnlimitedAccountsPurchaseChannel.WinoAccount, WinoIconGlyph.Payment,
            Translator.UnlimitedAccountsPurchaseDialog_WinoAccountTitle,
            isWinoAccountAvailable ? Translator.UnlimitedAccountsPurchaseDialog_WinoAccountDescription : Translator.UnlimitedAccountsPurchaseDialog_WinoAccountSignInRequired,
            isWinoAccountAvailable));
        AddRow(options);

        Primary.Title = Translator.Buttons_Continue;
        // Windows checks the first channel; here the first one that can be chosen.
        if (_options.FirstOrDefault(option => option.Enabled) is { } first) Select(first);
    }

    protected override bool CanSubmit => _selected is { Enabled: true };

    protected override Task PrimaryAsync()
    {
        if (_selected is { Enabled: true } selected) Finish(selected.Channel);
        return Task.CompletedTask;
    }

    private void Select(ChannelOption option)
    {
        if (!option.Enabled) return;
        _selected = option;
        foreach (var each in _options) each.IsSelected = ReferenceEquals(each, option);
        Refresh();
    }

    private ChannelOption Option(UnlimitedAccountsPurchaseChannel channel, WinoIconGlyph glyph, string title, string description, bool enabled)
    {
        var option = new ChannelOption(channel, glyph, title, description, enabled);
        option.Tapped += (_, _) => Select(option);
        _options.Add(option);
        return option;
    }

    /// <summary>A card with the channel mark, a bold title, a wrapping description and a selection mark.</summary>
    private sealed class ChannelOption : WinoSurfaceView
    {
        private readonly UIImageView _check;
        private bool _isSelected;

        public ChannelOption(UnlimitedAccountsPurchaseChannel channel, WinoIconGlyph glyph, string title, string description, bool enabled)
        {
            Channel = channel;
            Enabled = enabled;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Fill = WinoSettingsStyle.CardFill;
            Stroke = WinoSettingsStyle.CardStroke;
            StrokeWidth = 1;
            CornerRadius = WinoStyle.ControlRadius;

            var mark = new UIImageView(WinoIcons.Image(glyph, 24, enabled ? null : WinoStyle.TertiaryText)) { ContentMode = UIViewContentMode.Center };
            WinoLayout.Size(mark, 28, 28);
            var name = WinoStyle.Label(title, WinoStyle.BodyStrong, enabled ? WinoStyle.PrimaryText : WinoStyle.TertiaryText, 0);
            var detail = WinoStyle.Label(description, WinoStyle.Subheadline, enabled ? WinoStyle.SecondaryText : WinoStyle.TertiaryText, 0);
            var text = WinoLayout.VStack(2, name, detail);
            text.Alignment = UIStackViewAlignment.Fill;
            WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 750);
            _check = new UIImageView { ContentMode = UIViewContentMode.Center };
            WinoLayout.Size(_check, 24, 24);

            var row = WinoLayout.Insets(WinoLayout.HStack(12, mark, text, _check), 12, 14, 12, 14);
            row.Alignment = UIStackViewAlignment.Center;
            WinoLayout.Fill(row, this);
            HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;

            IsAccessibilityElement = true;
            AccessibilityLabel = title;
            AccessibilityHint = description;
            if (enabled) AddGestureRecognizer(new UITapGestureRecognizer(() => Tapped?.Invoke(this, EventArgs.Empty)));
            Apply();
        }

        public UnlimitedAccountsPurchaseChannel Channel { get; }
        public bool Enabled { get; }
        public event EventHandler? Tapped;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; Apply(); }
        }

        public override bool AccessibilityActivate()
        {
            if (!Enabled) return false;
            Tapped?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private void Apply()
        {
            _check.Image = !Enabled ? null : WinoIcons.Image(_isSelected ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle, 22, _isSelected ? WinoStyle.Accent : WinoStyle.TertiaryText);
            Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            StrokeWidth = _isSelected ? 2 : 1;
            var traits = UIAccessibilityTrait.Button;
            if (_isSelected) traits |= UIAccessibilityTrait.Selected;
            if (!Enabled) traits |= UIAccessibilityTrait.NotEnabled;
            AccessibilityTraits = traits;
        }
    }
}
