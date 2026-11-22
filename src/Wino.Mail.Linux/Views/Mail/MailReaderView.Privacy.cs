using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Editor;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The reader's privacy surface (Windows <c>MailRenderingPage</c> privacy button and InfoBars, macOS
/// <c>MailRenderingPageViewController.Privacy</c>): the shield button beside the date with its popover, the three
/// remote-content bars with Load once, and the hovered link's address over the bottom of the body.
/// </summary>
internal sealed partial class MailReaderView
{
    private Gtk.Button? _privacyButton;
    private Gtk.Label? _privacyIcon;
    private Gtk.Label? _privacyCount;
    private WinoInfoBar? _withheldBar;
    private WinoInfoBar? _junkBar;
    private WinoInfoBar? _ruleBar;
    private ReaderLinkTargetBar? _linkTarget;
    private ReaderPrivacyPopover? _privacyPopover;

    partial void BuildPrivacy()
    {
        var icon = _privacyIcon = WinoIcons.Label(WinoIconGlyph.Shield, 14);
        var count = _privacyCount = Gtk.Label.New(string.Empty);
        count.AddCssClass("caption");
        count.AddCssClass("numeric");
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        content.Append(icon);
        content.Append(count);
        var button = _privacyButton = Gtk.Button.New();
        button.SetChild(content);
        button.AddCssClass("flat");
        button.AddCssClass("wino-privacy-button");
        button.SetValign(Gtk.Align.Center);
        button.SetVisible(false);
        _header.DateLine.Append(button);

        // Not closable, as on Windows and macOS: a closed bar must not stay closed for the next message.
        _withheldBar = PrivacyBar(WinoInfoBarSeverity.Informational, Translator.Reader_Privacy_WithheldTitle, null);
        _junkBar = PrivacyBar(WinoInfoBarSeverity.Warning, Translator.Reader_Privacy_JunkBarTitle, Translator.Reader_Privacy_JunkBarMessage);
        _ruleBar = PrivacyBar(WinoInfoBarSeverity.Error, null, null);
        var bars = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        bars.Append(_withheldBar.Root);
        bars.Append(_junkBar.Root);
        bars.Append(_ruleBar.Root);
        _privacySlot.SetChild(bars);

        _linkTarget = new ReaderLinkTargetBar();
        _bodyOverlay.SetOverflow(Gtk.Overflow.Hidden);
        _bodyOverlay.AddOverlay(_linkTarget.Root);
        _privacyPopover = new ReaderPrivacyPopover();
        Bindings.Own(_privacyPopover);
    }

    private WinoInfoBar PrivacyBar(WinoInfoBarSeverity severity, string? title, string? message)
    {
        var bar = new WinoInfoBar(severity, title, message)
        {
            ActionTitle = Translator.Reader_Privacy_LoadOnce,
            IsClosable = false,
            IsOpen = false,
        };
        Bindings.Own(bar);
        return bar;
    }

    partial void InitializePrivacy()
    {
        Bind.BindAny(static vm => (vm.CurrentDecision, vm.PrivacyState, vm.BlockedTrackerCount, vm.PrivacyButtonAutomationName, vm.IsRemoteContentWithheld,
            vm.IsJunkBlocked, vm.IsSenderBlockedByRule, vm.WithheldBarMessage, vm.RuleBarTitle, vm.RuleBarMessage), _ => UpdatePrivacy());
        Bind.Bind(nameof(MailRenderingPageViewModel.CurrentRenderModel), static vm => vm.CurrentRenderModel, _ => UpdatePrivacy());

        foreach (var bar in new[] { _withheldBar, _junkBar, _ruleBar })
            if (bar is not null) bar.ActionInvoked += _ => LoadOnce();
        if (_privacyButton is { } button) Bindings.Own(GtkSignals.OnClicked(button, ShowPrivacyPopover));
        if (_privacyPopover is { } popover)
            popover.ItemInvoked += item => { if (!IsReleased) Observe(ViewModel.InvokePrivacyMenuItemCommand.ExecuteAsync(item)); };

        if (_reader is { } reader)
        {
            var linkTarget = _linkTarget;
            EventHandler<RendererLinkHoverEventArgs> hovered = (_, args) => RunOnUI(() => linkTarget?.Show(args));
            EventHandler<RendererNavigationRequestedEventArgs> navigated = (_, _) => RunOnUI(() => linkTarget?.Clear());
            reader.LinkHovered += hovered;
            reader.NavigationRequested += navigated;
            Bindings.Own(new ActionDisposable(() =>
            {
                reader.LinkHovered -= hovered;
                reader.NavigationRequested -= navigated;
            }));
        }
        WhenRendering(() => _linkTarget?.Clear());
        WhenRenavigating(() => _privacyPopover?.Close());
        UpdatePrivacy();
#if DEBUG
        WhenDescribing(() => $"privacy button={_privacyButton?.GetVisible() == true} state={ViewModel.PrivacyState} count={ViewModel.BlockedTrackerCount} " +
            $"withheld={ViewModel.IsRemoteContentWithheld} junk={ViewModel.IsJunkBlocked} rule={ViewModel.IsSenderBlockedByRule} popover={_privacyPopover?.IsShown == true} " +
            $"link=\"{_linkTarget?.Text}\" contextMenu=\"{_reader?.LastContextMenu}\"");
#endif
    }

    private void LoadOnce()
    {
        if (!IsReleased) Observe(ViewModel.LoadOnceCommand.ExecuteAsync(null));
    }

    private void ShowPrivacyPopover()
    {
        if (IsReleased || _privacyButton is not { } button || _privacyPopover is not { } popover) return;
        if (ViewModel.HasPrivacyDecision) popover.Show(button, ViewModel);
    }

    private void UpdatePrivacy()
    {
        if (IsReleased || _privacyButton is not { } button) return;
        var vm = ViewModel;
        button.SetVisible(vm.HasPrivacyDecision);
        if (_privacyIcon is { } icon) WinoIcons.SetGlyph(icon, WinoIcons.Glyph(vm.PrivacyButtonIcon));
        button.RemoveCssClass("error");
        button.RemoveCssClass("success");
        button.RemoveCssClass("dim-label");
        button.AddCssClass(vm.PrivacyState switch
        {
            PrivacyButtonState.Blocked => "error",
            PrivacyButtonState.Rule => "success",
            _ => "dim-label",
        });
        if (_privacyCount is { } count)
        {
            count.SetText(vm.BlockedTrackerCount.ToString(CultureInfo.CurrentCulture));
            count.SetVisible(vm.IsPrivacyCountVisible);
        }
        if (!string.IsNullOrEmpty(vm.PrivacyButtonAutomationName)) WinoA11y.LabelWithTooltip(button, vm.PrivacyButtonAutomationName);

        if (_withheldBar is { } withheld)
        {
            withheld.Message = vm.WithheldBarMessage;
            withheld.IsOpen = vm.IsRemoteContentWithheld;
        }
        if (_junkBar is { } junk) junk.IsOpen = vm.IsJunkBlocked;
        if (_ruleBar is { } rule)
        {
            rule.Title = vm.RuleBarTitle;
            rule.Message = vm.RuleBarMessage;
            rule.IsOpen = vm.IsSenderBlockedByRule;
        }
        _privacySlot.SetVisible(vm.IsRemoteContentBarVisible);
        if (!vm.HasPrivacyDecision) _privacyPopover?.Close();
    }

#if DEBUG
    internal ReaderLinkTargetBar? DebugLinkTarget => _linkTarget;
    internal void DebugShowPrivacy() => ShowPrivacyPopover();
    internal void DebugLoadOnce() => LoadOnce();
#endif
}
