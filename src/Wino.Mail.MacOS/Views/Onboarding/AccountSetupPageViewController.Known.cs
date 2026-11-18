using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// The account so far (Windows KnownRow): one pill with the provider mark on the account colour, the address and what
/// is known under it, the discovery spinner, and Change beside it. Change opens a menu; "Change provider" opens the
/// provider picker, which the failure and not-found bars open at their own buttons too.
/// </summary>
public sealed partial class AccountSetupPageViewController
{
    private NSButton _changeButton = null!;
    private NSMenu? _changeMenu;
    private NSMenuItem? _changeProviderItem;
    private NSPopover? _providerPicker;
    private string? _knownKey;
    private NSStackView? _knownText;
    private NSTextField? _knownChips;

    private NSView BuildKnownRow()
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
        identity.LineBreakMode = NSLineBreakMode.TruncatingTail;
        identity.WidthAnchor.ConstraintLessThanOrEqualTo(260).Active = true;
        identity.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var chips = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        chips.PreferredMaxLayoutWidth = 260;
        var text = WinoLayout.VStack(1, identity, chips);
        // A stack's own vertical hugging is weak, so the row could stretch it to the circle's height with the text at the top.
        text.SetHuggingPriority(1000, NSLayoutConstraintOrientation.Vertical);
        _knownText = text;
        _knownChips = chips;
        var spinner = AccountSetupUi.Spinner();
        var row = WinoLayout.HStack(10, circle, text, spinner);

        var pill = new WinoSurfaceView { CornerRadius = 22, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke };
        WinoLayout.Fill(row, pill, 6, 6, 6, 16);
        pill.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        pill.AccessibilityElement = true;
        pill.AccessibilityRole = NSAccessibilityRoles.GroupRole;

        _changeButton = AccountSetupUi.Link(Translator.AccountSetup_Change);
        _changeButton.Activated += (_, _) => ShowChangeMenu();
        When(_changeButton, s => s.IsChangeAddressVisible);

        var line = WinoLayout.HStack(4, pill, _changeButton);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(line);
        NSLayoutConstraint.ActivateConstraints(
        [
            line.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            line.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            line.LeadingAnchor.ConstraintGreaterThanOrEqualTo(host.LeadingAnchor),
            line.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor),
            line.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor)
        ]);
        When(host, s => s.IsKnownCardVisible);

        _appliers.Add(() =>
        {
            var vm = ViewModel;
            AccountSetupUi.SetSpinning(spinner, vm.IsResolvingVisible);
            WinoAccessibility.Label(spinner, vm.DiscoveryText);
            WinoAccessibility.Help(spinner, vm.DiscoveryHelpText);
            pill.AccessibilityLabel = vm.KnownText;
            pill.AccessibilityHelp = vm.RouteEvidenceText;
            WinoAccessibility.Help(_changeButton, vm.ChangeAddressAutomationName);
            RenderKnown(identity, chips, pill, circle, icon);
        });
        return host;
    }

    /// <summary>
    /// The address (or a local account's name) is the first line; the provider and capabilities follow under it, and why
    /// Wino chose the provider is the pill's tooltip. The circle shows the generic mail glyph until discovery names the
    /// provider, and a local account's laptop.
    /// </summary>
    private void RenderKnown(NSTextField identity, NSTextField chips, NSView pill, WinoSurfaceView circle, WinoIconView icon)
    {
        var vm = ViewModel;
        var identityText = string.Empty;
        var reason = string.Empty;
        var hasProvider = false;
        var items = new List<AccountSetupKnownItem>();
        foreach (var item in vm.KnownItems)
        {
            switch (item.Kind)
            {
                case AccountSetupKnownItemKind.Identity: identityText = item.Text; break;
                case AccountSetupKnownItemKind.Reason: reason = item.Text; break;
                default:
                    hasProvider |= item.Kind == AccountSetupKnownItemKind.Provider;
                    if (!string.IsNullOrWhiteSpace(item.Text)) items.Add(item);
                    break;
            }
        }

        if (identity.StringValue != identityText) identity.StringValue = identityText;
        pill.ToolTip = reason.Length > 0 ? reason : null;

        var key = string.Join('\u001f', items.Select(item => $"{item.Kind}:{item.Text}"));
        if (key != _knownKey)
        {
            _knownKey = key;
            chips.AttributedStringValue = BuildChips(items);
            chips.ToolTip = string.Join('\n', items.Select(item => item.HelpText).Where(help => !string.IsNullOrEmpty(help)));
        }
        chips.Hidden = items.Count == 0;

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
        var plain = new NSStringAttributes { Font = WinoStyle.Caption, ForegroundColor = WinoStyle.SecondaryText };
        var separator = new NSStringAttributes { Font = WinoStyle.Caption, ForegroundColor = WinoStyle.TertiaryText };
        foreach (var item in items)
        {
            if (text.Length > 0) text.Append(new NSAttributedString("  ·  ", separator));
            if (item.Kind == AccountSetupKnownItemKind.SignIn)
            {
                var attachment = new NSTextAttachment
                {
                    Image = WinoIcons.Image(WinoIconGlyph.Checkmark, 12, WinoStyle.Success),
                    Bounds = new CGRect(0, -2, 12, 12)
                };
                text.Append(NSAttributedString.FromAttachment(attachment));
                text.Append(new NSAttributedString(" ", plain));
            }
            text.Append(new NSAttributedString(item.Text, plain));
        }
        return text;
    }

    private void ShowChangeMenu()
    {
        var vm = ViewModel;
        if (_changeMenu is null)
        {
            _changeMenu = new NSMenu { AutoEnablesItems = false };
            _changeMenu.AddItem(new NSMenuItem(Translator.AccountSetup_ChangeAddress, (_, _) => Run(ViewModel.ChangeAddressCommand)));
            _changeProviderItem = new NSMenuItem(Translator.AccountSetup_ChangeProvider,
                (_, _) => NSApplication.SharedApplication.BeginInvokeOnMainThread(() => ShowProviderPicker(_changeButton)));
            _changeMenu.AddItem(_changeProviderItem);
        }
        _changeProviderItem!.Hidden = !vm.IsChangeProviderVisible;
        _changeProviderItem.AccessibilityLabel = vm.ChangeProviderAutomationName;
        var below = _changeButton.IsFlipped ? _changeButton.Bounds.Height + 4 : -4;
        _changeMenu.PopUpMenu(null, new CGPoint(0, below), _changeButton);
    }

    /// <summary>The providers Wino can set up, with the current one highlighted; choosing one runs discovery for it.</summary>
    private void ShowProviderPicker(NSView anchor)
    {
        if (Bindings.IsDisposed) return;
        var vm = ViewModel;
        _providerPicker ??= new NSPopover { Behavior = NSPopoverBehavior.Transient, Animates = true };
        if (_providerPicker.Shown) _providerPicker.Close();

        var title = WinoStyle.Label(Translator.AccountSetup_ProviderPicker_Title, WinoStyle.BodyStrong);
        var caption = AccountSetupUi.Caption(vm.ProviderPickerCaption);
        caption.PreferredMaxLayoutWidth = 320;
        caption.Hidden = string.IsNullOrEmpty(vm.ProviderPickerCaption);
        var rows = AccountSetupUi.Column(0);
        WinoAccessibility.Label(rows, Translator.AccountSetup_ProviderPicker_Title);
        var current = vm.CurrentProviderChoice;
        foreach (var choice in vm.ProviderChoices)
        {
            var row = new AccountSetupProviderChoiceView(choice, ReferenceEquals(choice, current));
            row.Pressed += (_, _) =>
            {
                _providerPicker?.Close();
                Run(ViewModel.ChangeProviderCommand, choice);
            };
            rows.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(rows.WidthAnchor).Active = true;
        }

        var stack = AccountSetupUi.Column(8, title, caption, rows);
        var content = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, content, 12);
        content.WidthAnchor.ConstraintEqualTo(344).Active = true;
        content.LayoutSubtreeIfNeeded();

        var previous = _providerPicker.ContentViewController;
        _providerPicker.ContentViewController = new NSViewController { View = content, PreferredContentSize = content.FittingSize };
        if (previous is not null)
        {
            ViewTreeDisposal.DisposeController(previous);
            previous.Dispose();
        }
        _providerPicker.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }
}
