using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// The account so far (Windows KnownRow): one pill with the provider mark on the account colour, the address and what
/// is known under it, the discovery spinner, and Change beside it. Change opens a menu with Change address and the
/// providers to choose from (the Windows provider flyout); the failure and not-found bars open the same providers.
/// </summary>
public sealed partial class AccountSetupPageViewController
{
    private NSLayoutConstraint _knownCenter = null!;
    private NSLayoutConstraint _knownLeading = null!;
    private UIButton _changeButton = null!;
    private string? _knownKey;

    private UIView BuildKnownRow()
    {
        var circle = new WinoSurfaceView { CornerRadius = 16, Fill = WinoStyle.SubtleFill };
        WinoLayout.Size(circle, 32, 32);
        var icon = new WinoIconView(WinoIconGlyph.Mail, 16);
        circle.AddSubview(icon);
        WinoLayout.Size(icon, 16, 16);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(circle.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(circle.CenterYAnchor)
        ]);

        var identity = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        identity.LineBreakMode = UILineBreakMode.TailTruncation;
        identity.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var chips = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        chips.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        var text = WinoLayout.VStack(1, identity, chips);
        text.Alignment = UIStackViewAlignment.Leading;
        var spinner = AccountSetupUi.Spinner();
        var row = WinoLayout.HStack(10, circle, text, spinner);
        row.Alignment = UIStackViewAlignment.Center;

        var pill = new WinoSurfaceView { CornerRadius = 24, Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1 };
        pill.TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(row, pill, 6, 6, 6, 16);
        pill.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        pill.IsAccessibilityElement = true;
        pill.AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;

        _changeButton = AccountSetupUi.Link(Translator.AccountSetup_Change);
        _changeButton.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(ChangeMenuElements()))]);
        _changeButton.ShowsMenuAsPrimaryAction = true;
        WinoLayout.Priorities(_changeButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        When(_changeButton, s => s.IsChangeAddressVisible);

        var line = WinoLayout.HStack(4, pill, _changeButton);
        line.Alignment = UIStackViewAlignment.Center;
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(line);
        _knownCenter = line.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor);
        _knownLeading = line.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor);
        _knownLeading.Active = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            line.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            line.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            line.LeadingAnchor.ConstraintGreaterThanOrEqualTo(host.LeadingAnchor),
            line.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor),
            _knownCenter
        ]);
        When(host, s => s.IsKnownCardVisible);

        _appliers.Add(() =>
        {
            var vm = ViewModel;
            AccountSetupUi.SetSpinning(spinner, vm.IsResolvingVisible);
            spinner.AccessibilityLabel = vm.DiscoveryText;
            spinner.AccessibilityHint = vm.DiscoveryHelpText;
            pill.AccessibilityLabel = vm.KnownText;
            pill.AccessibilityHint = vm.RouteEvidenceText;
            _changeButton.AccessibilityHint = vm.ChangeAddressAutomationName;
            RenderKnown(identity, chips, circle, icon);
        });
        return host;
    }

    /// <summary>
    /// The address (or a local account's name) is the first line; the provider and capabilities follow under it, and why
    /// Wino chose the provider is the pill's VoiceOver hint. The circle shows the generic mail glyph until discovery names
    /// the provider, and a local account's laptop.
    /// </summary>
    private void RenderKnown(UILabel identity, UILabel chips, WinoSurfaceView circle, WinoIconView icon)
    {
        var vm = ViewModel;
        var identityText = string.Empty;
        var hasProvider = false;
        var items = new List<AccountSetupKnownItem>();
        foreach (var item in vm.KnownItems.ToList())
        {
            switch (item.Kind)
            {
                case AccountSetupKnownItemKind.Identity: identityText = item.Text; break;
                case AccountSetupKnownItemKind.Reason: break;
                default:
                    hasProvider |= item.Kind == AccountSetupKnownItemKind.Provider;
                    items.Add(item);
                    break;
            }
        }

        if (identity.Text != identityText) identity.Text = identityText;

        var key = string.Join('\u001f', items.Select(item => $"{item.Kind}:{item.Text}"));
        if (key != _knownKey)
        {
            _knownKey = key;
            chips.AttributedText = BuildChips(items);
            chips.Hidden = items.Count == 0;
        }

        var providerGlyph = hasProvider ? MailProviderGlyphs.GetGlyph(vm.RouteProviderType, vm.RouteSpecialImapProvider) : WinoIconGlyph.Mail;
        icon.Icon = vm.IsLocalOnlyMode ? WinoIconGlyph.Laptop : providerGlyph == WinoIconGlyph.None ? WinoIconGlyph.Mail : providerGlyph;

        var hex = vm.SelectedColor?.Hex;
        var color = WinoStyle.FromHexString(hex);
        circle.Fill = color ?? WinoStyle.SubtleFill;
        // On the account colour the glyph takes black or white, whichever reads; brand colours would clash with it.
        var readable = color is null ? null : ReadableOn(hex);
        icon.Tint = readable;
        icon.Colorful = readable is null ? null : false;
    }

    private static NSAttributedString BuildChips(List<AccountSetupKnownItem> items)
    {
        var text = new NSMutableAttributedString();
        var plain = new UIStringAttributes { Font = WinoStyle.Caption, ForegroundColor = WinoStyle.SecondaryText };
        var separator = new UIStringAttributes { Font = WinoStyle.Caption, ForegroundColor = WinoStyle.TertiaryText };
        foreach (var item in items)
        {
            if (text.Length > 0) text.Append(new NSAttributedString("  ·  ", separator));
            if (item.Kind == AccountSetupKnownItemKind.SignIn)
            {
                var attachment = new NSTextAttachment
                {
                    Image = WinoIcons.MonoImage(WinoIconGlyph.Checkmark, 12, WinoStyle.Success),
                    Bounds = new CGRect(0, -2, 12, 12)
                };
                text.Append(NSAttributedString.FromAttachment(attachment));
                text.Append(new NSAttributedString(" ", plain));
            }
            text.Append(new NSAttributedString(item.Text, plain));
        }
        return text;
    }

    /// <summary>Change address, then the providers to choose from while a route is known.</summary>
    private UIMenuElement[] ChangeMenuElements()
    {
        var vm = ViewModel;
        var address = UIAction.Create(Translator.AccountSetup_ChangeAddress, null, null, _ => Run(ViewModel.ChangeAddressCommand));
        if (!vm.IsChangeProviderVisible) return [address];
        var providers = UIMenu.Create(Translator.AccountSetup_ChangeProvider, null, UIMenuIdentifier.None, (UIMenuOptions)0, ProviderMenuElements());
        return [address, providers];
    }

    /// <summary>The providers Wino can set up under the picker's caption, with the current one checked; choosing one runs discovery for it.</summary>
    private UIMenuElement[] ProviderMenuElements()
    {
        var vm = ViewModel;
        var current = vm.CurrentProviderChoice;
        var actions = new List<UIMenuElement>();
        foreach (var choice in vm.ProviderChoices)
        {
            var picked = choice;
            var action = UIAction.Create(choice.Title, WinoIcons.Image(choice.Glyph, 18), null, _ => Run(ViewModel.ChangeProviderCommand, picked));
            if (choice.HasDescription) action.Subtitle = choice.Description;
            action.State = ReferenceEquals(choice, current) ? UIMenuElementState.On : UIMenuElementState.Off;
            actions.Add(action);
        }
        var caption = vm.ProviderPickerCaption;
        var title = string.IsNullOrWhiteSpace(caption) ? Translator.AccountSetup_ProviderPicker_Title : caption;
        return [UIMenu.Create(title, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, actions.ToArray())];
    }

    /// <summary>A button that opens the providers (Choose provider, Change provider in the failure bars).</summary>
    private UIButton ProviderMenuButton(string title, bool link)
    {
        var button = link ? AccountSetupUi.Link(title) : SettingsBinder.CreateButton(title);
        button.Menu = UIMenu.Create(Translator.AccountSetup_ProviderPicker_Title, null, UIMenuIdentifier.None, (UIMenuOptions)0,
            [UIDeferredMenuElement.CreateUncached(completion => completion(ProviderMenuElements()))]);
        button.ShowsMenuAsPrimaryAction = true;
        button.AccessibilityLabel = title;
        return button;
    }
}
