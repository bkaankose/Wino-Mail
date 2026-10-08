using AppKit;
using CoreGraphics;
using Foundation;
using WebKit;
using Wino.Core.Domain;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// The Windows ConsentPolicyDialog: title, notice, the policy page, an acknowledgement checkbox that
/// enables Accept, and an inline error when acceptance fails. <paramref name="accept"/> returns
/// whether consent was granted; the sheet stays open on failure.
/// </summary>
internal static class IntelligenceConsentPolicySheet
{
    public static Task<bool> PresentAsync(NSWindow parent, Uri? policy, Func<Task<bool>> accept)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sheet = new NSWindow(new CGRect(0, 0, 560, 600), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false);
        sheet.ReleaseWhenClosed(false);
        sheet.Title = Translator.WinoAccount_IntelligenceConsentPolicyTitle;

        var title = WinoStyle.Label(Translator.WinoAccount_IntelligenceConsentPolicyTitle, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        var notice = WinoStyle.Label(Translator.WinoAccount_IntelligenceConsentPolicyNotice, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var web = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        if (policy is not null) web.LoadRequest(new NSUrlRequest(new NSUrl(policy.AbsoluteUri)));
        var webFrame = new WinoSurfaceView { Stroke = WinoStyle.Separator, CornerRadius = 6 };
        webFrame.WantsLayer = true;
        webFrame.Layer!.MasksToBounds = true;
        WinoLayout.Fill(web, webFrame);
        webFrame.HeightAnchor.ConstraintGreaterThanOrEqualTo(320).Active = true;
        var check = WinoCheckbox.Create(Translator.Intelligence_PrivacyPolicyAcknowledgement, () => { });
        check.TranslatesAutoresizingMaskIntoConstraints = false;
        var error = WinoStyle.Label(Translator.Intelligence_ConsentAcceptanceFailed, NSFont.SystemFontOfSize(12), WinoStyle.Critical, 0);
        error.Hidden = true;
        var spinner = IntelligenceViews.Spinner(16);
        spinner.Hidden = true;
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b" };
        var acceptButton = new NSButton { Title = Translator.Intelligence_AcceptPrivacyPolicyButton, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\r", Enabled = false };
        check.Activated += (_, _) => acceptButton.Enabled = check.State == NSCellStateValue.On;

        void Finish(bool value)
        {
            if (completion.Task.IsCompleted) return;
            completion.TrySetResult(value);
            parent.EndSheet(sheet);
        }

        cancel.Activated += (_, _) => Finish(false);
        acceptButton.Activated += async (_, _) =>
        {
            acceptButton.Enabled = false;
            error.Hidden = true;
            IntelligenceViews.SetSpinning(spinner, true);
            bool ok = false;
            try { ok = await accept(); }
            catch { ok = false; }
            if (completion.Task.IsCompleted) return;
            IntelligenceViews.SetSpinning(spinner, false);
            if (ok) { Finish(true); return; }
            error.Hidden = false;
            acceptButton.Enabled = check.State == NSCellStateValue.On;
        };

        var buttons = WinoLayout.HStack(8, spinner, WinoLayout.Spacer(), cancel, acceptButton);
        var stack = WinoLayout.VStack(12, title, notice, webFrame, check, error, buttons);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(22, 24, 20, 24);
        foreach (var view in new NSView[] { title, notice, webFrame, check, error, buttons })
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -48).Active = true;
        webFrame.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, sheet.ContentView!);
        parent.BeginSheet(sheet, _ =>
        {
            completion.TrySetResult(false);
            web.StopLoading();
            sheet.Dispose();
        });
        return completion.Task;
    }
}
