using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Where to buy Unlimited Accounts (port of UnlimitedAccountsPurchaseChannelDialog). App Store builds
/// list the Apple App Store first; they show this sheet only in the United States storefront, where
/// Apple allows other channels. Wino Account (Stripe) needs a signed-in Wino Account; the Microsoft
/// Store appears when the platform sells through it. Continue returns the selected channel, Cancel null.
/// </summary>
internal sealed class PurchaseChannelSheet : WinoAccountSheet<UnlimitedAccountsPurchaseChannel?>
{
    private readonly List<(UnlimitedAccountsPurchaseChannel Channel, NSButton Radio)> _options = [];

    public PurchaseChannelSheet(bool isWinoAccountAvailable, bool microsoftStore, bool appleAppStore) : base(480)
    {
        Sheet.Title = Translator.UnlimitedAccountsPurchaseDialog_Title;
        var heading = WinoStyle.Label(Translator.UnlimitedAccountsPurchaseDialog_Title, NSFont.SystemFontOfSize(18, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        var description = WinoStyle.Label(Translator.UnlimitedAccountsPurchaseDialog_Description, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        description.PreferredMaxLayoutWidth = 432;
        var header = WinoLayout.VStack(8, heading, description);
        header.Alignment = NSLayoutAttribute.Leading;
        description.WidthAnchor.ConstraintEqualTo(header.WidthAnchor).Active = true;
        AddRow(header);

        var options = WinoLayout.VStack(10);
        options.Alignment = NSLayoutAttribute.Leading;
        void AddOption(NSView option)
        {
            options.AddArrangedSubview(option);
            option.WidthAnchor.ConstraintEqualTo(options.WidthAnchor).Active = true;
        }

        if (appleAppStore)
            AddOption(Option(UnlimitedAccountsPurchaseChannel.AppleAppStore, WinoIconGlyph.Apple,
                Translator.UnlimitedAccountsPurchaseDialog_AppleAppStoreTitle, Translator.UnlimitedAccountsPurchaseDialog_AppleAppStoreDescription, true));
        if (microsoftStore)
            AddOption(Option(UnlimitedAccountsPurchaseChannel.MicrosoftStore, WinoIconGlyph.MicrosoftStore,
                Translator.UnlimitedAccountsPurchaseDialog_MicrosoftStoreTitle, Translator.UnlimitedAccountsPurchaseDialog_MicrosoftStoreDescription, true));
        AddOption(Option(UnlimitedAccountsPurchaseChannel.WinoAccount, WinoIconGlyph.Payment,
            Translator.UnlimitedAccountsPurchaseDialog_WinoAccountTitle,
            isWinoAccountAvailable ? Translator.UnlimitedAccountsPurchaseDialog_WinoAccountDescription : Translator.UnlimitedAccountsPurchaseDialog_WinoAccountSignInRequired,
            isWinoAccountAvailable));
        AddRow(options);

        Secondary.Title = Translator.Buttons_Cancel;
        Primary.Title = Translator.Buttons_Continue;

        // Windows checks the first channel; here the first one that can be chosen.
        var first = _options.FirstOrDefault(option => option.Radio.Enabled);
        if (first.Radio is not null) Select(first.Radio);
        Primary.Enabled = CanSubmit;
    }

    protected override NSView? InitialResponder => _options.FirstOrDefault(option => option.Radio.Enabled).Radio;
    protected override bool CanSubmit => _options.Any(option => option.Radio.Enabled && option.Radio.State == NSCellStateValue.On);

    protected override Task PrimaryAsync()
    {
        var selected = _options.FirstOrDefault(option => option.Radio.Enabled && option.Radio.State == NSCellStateValue.On);
        if (selected.Radio is not null) Finish(selected.Channel);
        return Task.CompletedTask;
    }

    private void Select(NSButton radio)
    {
        foreach (var option in _options) option.Radio.State = ReferenceEquals(option.Radio, radio) ? NSCellStateValue.On : NSCellStateValue.Off;
        Primary.Enabled = !IsBusy && CanSubmit;
    }

    /// <summary>A radio button with the channel mark, a bold title and a wrapping description; the text selects too.</summary>
    private NSView Option(UnlimitedAccountsPurchaseChannel channel, WinoIconGlyph glyph, string title, string description, bool enabled)
    {
        var radio = new NSButton { Title = string.Empty, TranslatesAutoresizingMaskIntoConstraints = false, Enabled = enabled };
        radio.SetButtonType(NSButtonType.Radio);
        WinoAccessibility.Label(radio, title);
        WinoAccessibility.Help(radio, description);
        radio.Activated += (_, _) => Select(radio);
        _options.Add((channel, radio));

        var mark = new WinoIconView(glyph, 24, enabled ? null : WinoStyle.TertiaryText);
        WinoLayout.Size(mark, 28, 28);
        mark.AccessibilityElement = false;
        var name = WinoStyle.Label(title, WinoStyle.BodyStrong, enabled ? WinoStyle.PrimaryText : WinoStyle.TertiaryText, 0);
        var detail = WinoStyle.Label(description, NSFont.SystemFontOfSize(12), enabled ? WinoStyle.SecondaryText : WinoStyle.TertiaryText, 0);
        detail.PreferredMaxLayoutWidth = 340;
        var text = WinoLayout.VStack(2, name, detail);
        text.Alignment = NSLayoutAttribute.Leading;
        detail.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(12, radio, mark, text);
        row.Alignment = NSLayoutAttribute.Top;
        row.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        var surface = new WinoSurfaceView
        {
            Fill = Wino.Mail.Controls.AppKit.Settings.WinoSettingsStyle.CardFill,
            Stroke = Wino.Mail.Controls.AppKit.Settings.WinoSettingsStyle.CardStroke,
            CornerRadius = WinoStyle.ControlRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(row, surface);
        if (enabled)
            surface.AddGestureRecognizer(new NSClickGestureRecognizer(() => { Select(radio); Sheet.MakeFirstResponder(radio); }));
        return surface;
    }
}
