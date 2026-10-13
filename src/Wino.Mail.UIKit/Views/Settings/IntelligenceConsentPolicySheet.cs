using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Views.Dialogs;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Windows ConsentPolicyDialog as a form sheet: title, notice, the policy page, an acknowledgement check that
/// enables Accept, and an inline error when acceptance fails. <paramref name="accept"/> returns whether consent was
/// granted; the sheet stays open on failure. Cancel or a swipe down closes it without consent.
/// </summary>
internal static class IntelligenceConsentPolicySheet
{
    public static Task<bool> PresentAsync(UIViewController presenter, Uri? policy, Func<Task<bool>> accept)
    {
        var form = new FormSheet(Translator.WinoAccount_IntelligenceConsentPolicyTitle, Translator.WinoAccount_IntelligenceConsentPolicyNotice,
            Translator.Intelligence_AcceptPrivacyPolicyButton) { PrefersLargeDetent = true };

        var web = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        if (policy is not null && NSUrl.FromString(policy.AbsoluteUri) is { } address) web.LoadRequest(new NSUrlRequest(address));
        var webFrame = new WinoSurfaceView { Stroke = WinoStyle.Separator, CornerRadius = 6, ClipsToBounds = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(web, webFrame);
        webFrame.HeightAnchor.ConstraintEqualTo(360).Active = true;
        form.AddFullWidth(webFrame);

        var check = IntelligenceViews.CheckButton(Translator.Intelligence_PrivacyPolicyAcknowledgement);
        if (check.Configuration is { } configuration)
        {
            configuration.TitleLineBreakMode = UILineBreakMode.WordWrap;
            check.Configuration = configuration;
        }
        check.PrimaryActionTriggered += (_, _) =>
        {
            IntelligenceViews.SetChecked(check, !check.Selected);
            form.Refresh();
        };
        form.AddFullWidth(check);

        form.CanConfirm = () => check.Selected;
        form.ConfirmAsync = async () =>
        {
            bool ok;
            try { ok = await accept(); }
            catch { ok = false; }
            return ok ? null : Translator.Intelligence_ConsentAcceptanceFailed;
        };

        var result = form.PresentAsync(presenter);
        _ = result.ContinueWith(_ => web.InvokeOnMainThread(web.StopLoading), TaskScheduler.Default);
        return result;
    }
}
