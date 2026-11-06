using System.Globalization;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using AndroidX.Core.View;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Editor;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The reader's privacy surface (Windows MailRenderingPage): the shield button beside the date with its popup menu, and
/// the withheld, Junk and blocked-by-rule bars over the body. The menu is the ViewModel's <c>PrivacyMenuItems</c> under
/// disabled header lines; every entry runs <c>InvokePrivacyMenuItemCommand</c>. Links from the body go through
/// <see cref="MailRenderingPageViewModel.OpenLinkAsync"/> so tracking parameters are removed and the confirmation shows.
/// </summary>
public sealed partial class MailReaderFragment
{
    private const int PrivacyHeaderGroup = 0;

    private MaterialButton? _privacyButton;
    private View? _privacyBars;
    private WinoInfoBar? _withheldBar;
    private WinoInfoBar? _junkBar;
    private WinoInfoBar? _ruleBar;
    private MaterialButton? _editRule;

    private void FindPrivacyViews(View view)
    {
        _privacyBars = view.FindViewById(Resource.Id.reader_privacy_bars);
        _withheldBar = view.FindViewById<WinoInfoBar>(Resource.Id.reader_withheld_bar);
        _junkBar = view.FindViewById<WinoInfoBar>(Resource.Id.reader_junk_bar);
        _ruleBar = view.FindViewById<WinoInfoBar>(Resource.Id.reader_rule_bar);
    }

    private void BuildPrivacy(View view, Context context, MailRenderingPageViewModel viewModel, IDispatcher dispatcher)
    {
        ConfigureBar(_withheldBar, WinoInfoBarSeverity.Informational, Translator.Reader_Privacy_WithheldTitle);
        ConfigureBar(_junkBar, WinoInfoBarSeverity.Warning, Translator.Reader_Privacy_JunkBarTitle);
        ConfigureBar(_ruleBar, WinoInfoBarSeverity.Error, null);
        if (_junkBar is not null) _junkBar.Message = Translator.Reader_Privacy_JunkBarMessage;

        // Windows puts an "Edit rule" link in the rule bar; the Android bar has one action, so the link sits under it.
        if (_privacyBars is LinearLayout bars)
        {
            _editRule = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle)
            {
                Text = Translator.Reader_Privacy_EditRule,
                Visibility = ViewStates.Gone
            };
            _editRule.Click += (_, _) => ShowPrivacyMenu();
            bars.AddView(_editRule, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.End });
        }

        if (view.FindViewById<LinearLayout>(Resource.Id.reader_sender_row) is { } senderRow)
        {
            var button = _privacyButton = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle)
            {
                Visibility = ViewStates.Gone,
                IconGravity = MaterialButton.IconGravityTextStart,
                IconPadding = 0
            };
            button.InsetTop = 0;
            button.InsetBottom = 0;
            int touch = WinoTokens.Px(context, Resource.Dimension.wino_size_touch_large);
            button.SetMinWidth(touch);
            button.SetMinimumWidth(touch);
            button.SetMinHeight(touch);
            button.SetMinimumHeight(touch);
            int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_s);
            button.SetPadding(padding, 0, padding, 0);
            button.Click += (_, _) => ShowPrivacyMenu();
            senderRow.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_2xs)
            });
        }

        void Refresh<TValue>(string property, Func<MailRenderingPageViewModel, TValue> read)
            => Bind(viewModel, dispatcher, property, read, _ => UpdatePrivacy());
        Refresh(nameof(MailRenderingPageViewModel.HasPrivacyDecision), vm => vm.HasPrivacyDecision);
        Refresh(nameof(MailRenderingPageViewModel.PrivacyState), vm => vm.PrivacyState);
        Refresh(nameof(MailRenderingPageViewModel.BlockedTrackerCount), vm => vm.BlockedTrackerCount);
        Refresh(nameof(MailRenderingPageViewModel.PrivacyButtonAutomationName), vm => vm.PrivacyButtonAutomationName);
        Refresh(nameof(MailRenderingPageViewModel.IsRemoteContentWithheld), vm => vm.IsRemoteContentWithheld);
        Refresh(nameof(MailRenderingPageViewModel.IsJunkBlocked), vm => vm.IsJunkBlocked);
        Refresh(nameof(MailRenderingPageViewModel.IsSenderBlockedByRule), vm => vm.IsSenderBlockedByRule);
        Refresh(nameof(MailRenderingPageViewModel.WithheldBarMessage), vm => vm.WithheldBarMessage);
        Refresh(nameof(MailRenderingPageViewModel.RuleBarTitle), vm => vm.RuleBarTitle);
        Refresh(nameof(MailRenderingPageViewModel.RuleBarMessage), vm => vm.RuleBarMessage);
    }

    private void ConfigureBar(WinoInfoBar? bar, WinoInfoBarSeverity severity, string? title)
    {
        if (bar is null) return;
        bar.Severity = severity;
        bar.Title = title;
        bar.ActionTitle = Translator.Reader_Privacy_LoadOnce;
        // Not closable: a closed bar would stay closed for the next message.
        bar.IsClosable = false;
        bar.ActionInvoked += (_, _) => Observe(ViewModel?.LoadOnceCommand.ExecuteAsync(null));
    }

    /// <summary>The button's glyph, colour and count, and which bar shows (MailRenderingPage bindings).</summary>
    private void UpdatePrivacy()
    {
        if (ViewModel is not { } viewModel || View?.Context is not { } context) return;

        if (_privacyButton is { } button)
        {
            button.Visibility = viewModel.HasPrivacyDecision ? ViewStates.Visible : ViewStates.Gone;
            var tint = viewModel.PrivacyState switch
            {
                PrivacyButtonState.Blocked => WinoColors.Critical(context),
                PrivacyButtonState.Rule => WinoColors.Success(context),
                _ => WinoColors.SecondaryText(context)
            };
            button.Icon = WinoIcons.Drawable(context, viewModel.PrivacyButtonIcon, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), tint);
            button.IconTint = ColorStateList.ValueOf(tint);
            bool count = viewModel.IsPrivacyCountVisible;
            button.Text = count ? viewModel.BlockedTrackerCount.ToString(CultureInfo.CurrentCulture) : string.Empty;
            button.IconPadding = count ? WinoTokens.Px(context, Resource.Dimension.wino_space_2xs) : 0;
            button.SetTextColor(WinoColors.SecondaryText(context));
            button.ContentDescription = viewModel.PrivacyButtonAutomationName;
            TooltipCompat.SetTooltipText(button, new Java.Lang.String(viewModel.PrivacyButtonAutomationName));
        }

        if (_withheldBar is not null)
        {
            _withheldBar.Message = viewModel.WithheldBarMessage;
            _withheldBar.Visibility = viewModel.IsRemoteContentWithheld ? ViewStates.Visible : ViewStates.Gone;
        }
        if (_junkBar is not null) _junkBar.Visibility = viewModel.IsJunkBlocked ? ViewStates.Visible : ViewStates.Gone;
        if (_ruleBar is not null)
        {
            _ruleBar.Title = viewModel.RuleBarTitle;
            _ruleBar.Message = viewModel.RuleBarMessage;
            _ruleBar.Visibility = viewModel.IsSenderBlockedByRule ? ViewStates.Visible : ViewStates.Gone;
        }
        if (_editRule is not null) _editRule.Visibility = viewModel.IsSenderBlockedByRule ? ViewStates.Visible : ViewStates.Gone;
        if (_privacyBars is not null) _privacyBars.Visibility = viewModel.IsRemoteContentBarVisible ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>
    /// The Windows privacy flyout as a popup menu: the policy, reason and detail lines and the blocked hosts as disabled
    /// header entries, then the ViewModel's actions in their groups (a divider before each group).
    /// </summary>
    private void ShowPrivacyMenu()
    {
        if (ViewModel is not { HasPrivacyDecision: true } viewModel || View?.Context is not { } context) return;
        MaterialButton? anchor = _privacyButton is { Visibility: ViewStates.Visible } ? _privacyButton : _editRule;
        if (anchor is null) return;

        var popup = new PopupMenu(context, anchor);
        var menu = popup.Menu;
        int order = 0;

        void Header(Java.Lang.ICharSequence text)
        {
            var item = menu.Add(PrivacyHeaderGroup, 0, order++, text);
            item?.SetEnabled(false);
        }

        var policy = new SpannableString(viewModel.PrivacyPolicyLine);
        policy.SetSpan(new StyleSpan(TypefaceStyle.Bold), 0, viewModel.PrivacyPolicyLine.Length, SpanTypes.ExclusiveExclusive);
        Header(policy);
        if (!string.IsNullOrEmpty(viewModel.PrivacyReasonLine)) Header(new Java.Lang.String(viewModel.PrivacyReasonLine));
        if (viewModel.HasPrivacyDetailLine) Header(new Java.Lang.String(viewModel.PrivacyDetailLine));
        foreach (var host in viewModel.PrivacyBlockedHosts)
        {
            var line = new SpannableString($"{host.Host} · {host.Description}");
            line.SetSpan(new TypefaceSpan("monospace"), 0, host.Host.Length, SpanTypes.ExclusiveExclusive);
            Header(line);
        }

        var items = viewModel.PrivacyMenuItems.ToList();
        int group = PrivacyHeaderGroup + 1;
        for (int index = 0; index < items.Count; index++)
        {
            var entry = items[index];
            if (entry.StartsGroup && index > 0) group++;
            menu.Add(group, index, order++, MenuTitle(context, entry));
        }
        MenuCompat.SetGroupDividerEnabled(menu, true);

        popup.MenuItemClick += (_, args) =>
        {
            int index = args.Item?.ItemId ?? -1;
            if (args.Item?.GroupId == PrivacyHeaderGroup || index < 0 || index >= items.Count) return;
            Observe(ViewModel?.InvokePrivacyMenuItemCommand.ExecuteAsync(items[index]));
        };
        popup.Show();
    }

    /// <summary>"Text · hint" with the hint in the secondary colour; the recommended action in the accent colour.</summary>
    private static Java.Lang.ICharSequence MenuTitle(Context context, PrivacyMenuItem item)
    {
        string text = item.HasHint ? $"{item.Text} · {item.Hint}" : item.Text;
        var title = new SpannableString(text);
        if (item.IsRecommended)
            title.SetSpan(new ForegroundColorSpan(WinoColors.AccentText(context)), 0, item.Text.Length, SpanTypes.ExclusiveExclusive);
        if (item.HasHint)
            title.SetSpan(new ForegroundColorSpan(WinoColors.SecondaryText(context)), item.Text.Length, text.Length, SpanTypes.ExclusiveExclusive);
        return title;
    }

    /// <summary>Remote content follows the decision the ViewModel rendered with (a one-time Load once included).</summary>
    private static RemoteContentPolicy ResourcePolicy(MailRenderingPageViewModel viewModel)
    {
        var mode = viewModel.CurrentDecision?.EffectiveMode ?? viewModel.CurrentRenderModel?.MailRenderingOptions?.RemoteContent ?? RemoteContentMode.LoadEverything;
        // Block trackers loads images: the trackers were already removed from the HTML.
        return mode == RemoteContentMode.BlockAll ? RemoteContentPolicy.Blocked : RemoteContentPolicy.ImagesAndFontsAllowed;
    }

    /// <summary>A link from the body: cleaned, confirmed when the user asked for it, then opened in the browser.</summary>
    private async Task OpenLinkAsync(RendererNavigationRequestedEventArgs request)
    {
        if (ViewModel is not { } viewModel) return;
        (await viewModel.OpenLinkAsync(request.Uri, request.LinkText)).ThrowIfNotSucceeded();
    }
}
