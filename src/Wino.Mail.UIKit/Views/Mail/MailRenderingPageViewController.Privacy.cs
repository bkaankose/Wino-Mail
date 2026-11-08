using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The reader's privacy surface (Windows MailRenderingPage privacy button and InfoBars, Mac .Privacy partial): the
/// shield button next to the date opens a menu with what was loaded or blocked and the actions from
/// <c>PrivacyMenuItems</c>; under the header, one of three bars explains withheld content (Block all), Junk or a Block
/// rule, each with Load once.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private UIButton _privacyButton = null!;
    private WinoInfoBar _withheldBar = null!;
    private WinoInfoBar _junkBar = null!;
    private WinoInfoBar _ruleBar = null!;
    private UIStackView _privacyBars = null!;

    private UIButton BuildPrivacyButton()
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(4, 4, 4, 4);
        configuration.ImagePadding = 3;
        _privacyButton = new UIButton(UIButtonType.System)
        {
            Configuration = configuration,
            ShowsMenuAsPrimaryAction = true,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        // Built when it opens, so the lines and actions always match the message on screen.
        _privacyButton.Menu = UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
            [UIDeferredMenuElement.CreateUncached(completion => completion(PrivacyMenuElements()))]);
        WinoLayout.Priorities(_privacyButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        _privacyButton.PointerInteractionEnabled = true;
        return _privacyButton;
    }

    private UIStackView BuildPrivacyBars()
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
        _privacyBars.Alignment = UIStackViewAlignment.Fill;
        _privacyBars.Hidden = true;
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
        var configuration = _privacyButton.Configuration ?? UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(vm.PrivacyButtonIcon, 15, tint);
        configuration.BaseForegroundColor = WinoStyle.SecondaryText;
        configuration.Title = vm.IsPrivacyCountVisible ? vm.BlockedTrackerCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;
        configuration.TitleTextAttributesTransformer = attributes =>
        {
            attributes[UIStringAttributeKey.Font] = WinoStyle.Caption;
            return attributes;
        };
        _privacyButton.Configuration = configuration;
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
    /// The privacy menu: a titled section with the reason, the detail line and the blocked hosts as disabled rows, then
    /// one inline section per action group, each action with its hint as the subtitle.
    /// </summary>
    private UIMenuElement[] PrivacyMenuElements()
    {
        var vm = ViewModel;
        if (!vm.HasPrivacyDecision) return [];

        var info = new List<UIMenuElement>();
        if (!string.IsNullOrWhiteSpace(vm.PrivacyReasonLine)) info.Add(InfoRow(vm.PrivacyReasonLine, null, null));
        if (vm.HasPrivacyDetailLine) info.Add(InfoRow(vm.PrivacyDetailLine, null, null));
        foreach (var host in vm.PrivacyBlockedHosts)
            info.Add(InfoRow(host.Host, host.Description, WinoIcons.Image(WinoIconGlyph.Dismiss, 14, WinoStyle.Critical)));

        var sections = new List<UIMenuElement>
        {
            UIMenu.Create(vm.PrivacyPolicyLine, WinoIcons.Image(vm.PrivacyButtonIcon, 18), UIMenuIdentifier.None, UIMenuOptions.DisplayInline, info.ToArray())
        };

        var group = new List<UIMenuElement>();
        foreach (var item in vm.PrivacyMenuItems.ToList())
        {
            if (item.StartsGroup && group.Count > 0)
            {
                sections.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, group.ToArray()));
                group = [];
            }
            var captured = item;
            var action = UIAction.Create(item.Text, WinoIcons.Image(item.Icon, 18), null,
                _ => Observe(ViewModel.InvokePrivacyMenuItemCommand.ExecuteAsync(captured)));
            if (item.HasHint) action.Subtitle = item.Hint;
            group.Add(action);
        }
        if (group.Count > 0) sections.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, group.ToArray()));
        return sections.ToArray();
    }

    private static UIAction InfoRow(string title, string? subtitle, UIImage? image)
    {
        var row = UIAction.Create(title, image, null, _ => { });
        if (!string.IsNullOrEmpty(subtitle)) row.Subtitle = subtitle;
        row.Attributes = UIMenuElementAttributes.Disabled;
        return row;
    }
}
