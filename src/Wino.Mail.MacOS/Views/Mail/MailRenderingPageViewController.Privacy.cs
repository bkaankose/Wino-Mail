using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The reader's privacy surface (Windows MailRenderingPage privacy button and InfoBars): the shield button next to the
/// date opens a menu with what was loaded or blocked and the actions from <c>PrivacyMenuItems</c>; under the header,
/// one of three bars explains withheld content (Block all), Junk or a Block rule, each with Load once.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private NSButton _privacyButton = null!;
    private WinoInfoBar _withheldBar = null!;
    private WinoInfoBar _junkBar = null!;
    private WinoInfoBar _ruleBar = null!;
    private NSStackView _privacyBars = null!;

    private NSButton BuildPrivacyButton()
    {
        _privacyButton = new NSButton
        {
            Bordered = false,
            Title = string.Empty,
            ImagePosition = NSCellImagePosition.ImageLeading,
            ImageHugsTitle = true,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _privacyButton.SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        _privacyButton.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _privacyButton.Activated += (_, _) => ShowPrivacyMenu();
        return _privacyButton;
    }

    private NSStackView BuildPrivacyBars()
    {
        // Not closable, as on Windows: a closed bar must not stay closed for the next message.
        _withheldBar = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.Reader_Privacy_WithheldTitle, null)
        {
            ActionTitle = Translator.Reader_Privacy_LoadOnce,
            IsClosable = false,
            Hidden = true
        };
        _junkBar = new WinoInfoBar(WinoInfoBarSeverity.Warning, Translator.Reader_Privacy_JunkBarTitle, Translator.Reader_Privacy_JunkBarMessage)
        {
            ActionTitle = Translator.Reader_Privacy_LoadOnce,
            IsClosable = false,
            Hidden = true
        };
        _ruleBar = new WinoInfoBar(WinoInfoBarSeverity.Error, null, null)
        {
            ActionTitle = Translator.Reader_Privacy_LoadOnce,
            IsClosable = false,
            Hidden = true
        };
        foreach (var bar in new[] { _withheldBar, _junkBar, _ruleBar })
            bar.ActionInvoked += (_, _) => Observe(ViewModel.LoadOnceCommand.ExecuteAsync(null));

        _privacyBars = WinoLayout.VStack(8, _withheldBar, _junkBar, _ruleBar);
        _privacyBars.Alignment = NSLayoutAttribute.Leading;
        _privacyBars.DetachesHiddenViews = true;
        _privacyBars.Hidden = true;
        foreach (var bar in new[] { _withheldBar, _junkBar, _ruleBar })
            bar.WidthAnchor.ConstraintEqualTo(_privacyBars.WidthAnchor).Active = true;
        return _privacyBars;
    }

    private void BindPrivacy()
    {
        foreach (var property in new[]
        {
            nameof(ViewModel.CurrentDecision), nameof(ViewModel.PrivacyState), nameof(ViewModel.BlockedTrackerCount),
            nameof(ViewModel.PrivacyButtonAutomationName), nameof(ViewModel.IsRemoteContentWithheld), nameof(ViewModel.IsJunkBlocked),
            nameof(ViewModel.IsSenderBlockedByRule), nameof(ViewModel.WithheldBarMessage), nameof(ViewModel.RuleBarTitle),
            nameof(ViewModel.RuleBarMessage)
        })
        {
            Bind(property, _ => 0, _ => UpdatePrivacy());
        }
    }

    private void UpdatePrivacy()
    {
        var vm = ViewModel;
        _privacyButton.Hidden = !vm.HasPrivacyDecision;
        var tint = vm.PrivacyState switch
        {
            PrivacyButtonState.Blocked => WinoStyle.Critical,
            PrivacyButtonState.Rule => WinoStyle.Success,
            _ => WinoStyle.SecondaryText
        };
        _privacyButton.Image = WinoIcons.Image(vm.PrivacyButtonIcon, 14, tint);
        _privacyButton.ContentTintColor = tint;
        _privacyButton.AttributedTitle = new NSAttributedString(
            vm.IsPrivacyCountVisible ? vm.BlockedTrackerCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty,
            new NSStringAttributes { Font = NSFont.SystemFontOfSize(12), ForegroundColor = WinoStyle.SecondaryText });
        _privacyButton.ToolTip = vm.PrivacyButtonAutomationName;
        WinoAccessibility.Label(_privacyButton, vm.PrivacyButtonAutomationName);

        _withheldBar.Message = vm.WithheldBarMessage;
        _withheldBar.Hidden = !vm.IsRemoteContentWithheld;
        _junkBar.Hidden = !vm.IsJunkBlocked;
        _ruleBar.Title = vm.RuleBarTitle;
        _ruleBar.Message = vm.RuleBarMessage;
        _ruleBar.Hidden = !vm.IsSenderBlockedByRule;
        _privacyBars.Hidden = !vm.IsRemoteContentBarVisible;
    }

    /// <summary>
    /// The privacy menu, built when it opens: the header lines and blocked hosts as disabled items, then the actions
    /// with their hints as a second line. A separator starts each new group.
    /// </summary>
    private void ShowPrivacyMenu()
    {
        var vm = ViewModel;
        if (!vm.HasPrivacyDecision) return;
        var menu = new NSMenu { AutoEnablesItems = false };

        var header = Mutable(vm.PrivacyPolicyLine,
            new NSStringAttributes { Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold), ForegroundColor = WinoStyle.PrimaryText });
        if (!string.IsNullOrWhiteSpace(vm.PrivacyReasonLine))
            header.Append(new NSAttributedString("\n" + vm.PrivacyReasonLine, Secondary(11)));
        menu.AddItem(new NSMenuItem(string.Empty) { AttributedTitle = header, Image = WinoIcons.Image(vm.PrivacyButtonIcon, 16), Enabled = false });

        if (vm.HasPrivacyDetailLine)
            menu.AddItem(new NSMenuItem(string.Empty) { AttributedTitle = new NSAttributedString(vm.PrivacyDetailLine, Secondary(12)), Enabled = false });

        foreach (var host in vm.PrivacyBlockedHosts)
        {
            var line = Mutable(host.Host,
                new NSStringAttributes { Font = NSFont.MonospacedSystemFont(11, NSFontWeight.Regular), ForegroundColor = WinoStyle.PrimaryText });
            line.Append(new NSAttributedString("   " + host.Description, Secondary(11)));
            menu.AddItem(new NSMenuItem(string.Empty) { AttributedTitle = line, Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 11, WinoStyle.Critical), Enabled = false });
        }

        menu.AddItem(NSMenuItem.SeparatorItem);
        foreach (var item in vm.PrivacyMenuItems.ToList())
        {
            if (item.StartsGroup) menu.AddItem(NSMenuItem.SeparatorItem);
            var captured = item;
            var title = Mutable(item.Text, new NSStringAttributes
            {
                Font = item.IsRecommended ? NSFont.MenuFontOfSize(0).WithSemibold() : NSFont.MenuFontOfSize(0),
                ForegroundColor = WinoStyle.PrimaryText
            });
            if (item.HasHint) title.Append(new NSAttributedString("\n" + item.Hint, Secondary(11)));
            menu.AddItem(new NSMenuItem(string.Empty, (_, _) => Observe(ViewModel.InvokePrivacyMenuItemCommand.ExecuteAsync(captured)))
            {
                AttributedTitle = title,
                Image = WinoIcons.Image(item.Icon, 16)
            });
        }

        menu.PopUpMenu(null, new CGPoint(0, _privacyButton.Bounds.Height + 4), _privacyButton);
    }

    private static NSMutableAttributedString Mutable(string text, NSStringAttributes attributes)
        => new(new NSAttributedString(text, attributes));

    private static NSStringAttributes Secondary(double size)
        => new() { Font = NSFont.SystemFontOfSize((nfloat)size), ForegroundColor = WinoStyle.SecondaryText };
}

internal static class PrivacyMenuFontExtensions
{
    public static NSFont WithSemibold(this NSFont font) => NSFont.SystemFontOfSize(font.PointSize, NSFontWeight.Semibold);
}
