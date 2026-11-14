using Android.Content;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.CheckBox;
using Wino.Core.Domain;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// The Windows ConsentPolicyDialog as a Material bottom sheet (iOS IntelligenceConsentPolicySheet): title, notice, the
/// policy page, an acknowledgement check that enables Accept, and an inline error when acceptance fails.
/// <c>accept</c> returns whether consent was granted; the sheet stays open on failure. Cancel, Back or a swipe down
/// close it without consent.
/// </summary>
internal static class IntelligenceConsentPolicySheet
{
    public static Task<bool> PresentAsync(Context context, Uri? policy, Func<Task<bool>> accept)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new BottomSheetDialog(context);
        int padding = WinoTokens.Px(context, Resource.Dimension.wino_space_xl);

        var title = IntelligenceViews.Label(context, Translator.WinoAccount_IntelligenceConsentPolicyTitle, WinoTextStyle.Title);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var notice = IntelligenceViews.Label(context, Translator.WinoAccount_IntelligenceConsentPolicyNotice, WinoTextStyle.BodySecondary);

        var web = new WebView(context);
        web.SetWebViewClient(new WebViewClient());
        web.NestedScrollingEnabled = true;
        if (policy is not null) web.LoadUrl(policy.AbsoluteUri);
        var webFrame = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, WinoColors.CardFill(context), WinoColors.Divider(context), Resource.Dimension.wino_radius_medium), ClipToOutline = true };
        int inset = Math.Max(1, WinoTokens.Px(context, Resource.Dimension.wino_space_hairline));
        webFrame.SetPadding(inset, inset, inset, inset);
        webFrame.AddView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_policy_height)));

        var check = new MaterialCheckBox(context) { Text = Translator.Intelligence_PrivacyPolicyAcknowledgement };
        check.SetTextColor(WinoColors.PrimaryText(context));
        WinoMaterialAccent.Tint(check);

        var error = IntelligenceViews.Label(context, Translator.Intelligence_ConsentAcceptanceFailed, WinoTextStyle.Description);
        error.SetTextColor(WinoColors.Critical(context));
        error.Visibility = ViewStates.Gone;
        error.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;

        var cancel = SettingsBinder.CreateButton(context, Translator.Buttons_Cancel);
        var confirm = SettingsBinder.CreateButton(context, Translator.Intelligence_AcceptPrivacyPolicyButton, outlined: false);
        confirm.Enabled = false;
        var gap = IntelligenceViews.Spacer(context);
        var buttons = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_s, gap, gap, cancel, confirm);

        var column = IntelligenceViews.VStack(context, Resource.Dimension.wino_space_m, title, notice, webFrame, check, error, buttons);
        column.SetPadding(padding, padding, padding, padding);
        var scroll = new global::AndroidX.Core.Widget.NestedScrollView(context);
        scroll.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        bool busy = false;
        check.CheckedChange += (_, _) => confirm.Enabled = check.Checked && !busy;
        cancel.Click += (_, _) => sheet.Dismiss();
        confirm.Click += async (_, _) =>
        {
            if (busy || !check.Checked) return;
            busy = true;
            confirm.Enabled = false;
            cancel.Enabled = false;
            error.Visibility = ViewStates.Gone;
            bool ok;
            try { ok = await accept(); }
            catch { ok = false; }
            busy = false;
            cancel.Enabled = true;
            if (ok)
            {
                result.TrySetResult(true);
                sheet.Dismiss();
                return;
            }
            confirm.Enabled = check.Checked;
            error.Visibility = ViewStates.Visible;
        };
        sheet.DismissEvent += (_, _) =>
        {
            web.StopLoading();
            web.Destroy();
            result.TrySetResult(false);
        };

        sheet.SetContentView(scroll);
        sheet.Behavior.State = BottomSheetBehavior.StateExpanded;
        sheet.Behavior.SkipCollapsed = true;
        sheet.Show();
        return result.Task;
    }
}
