using AppKit;
using CoreGraphics;
using Foundation;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Authentication;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Dialogs;

/// <summary>
/// Hosts an identity provider's sign-in page in a WKWebView on a window sheet and captures the redirect before it
/// loads (the Windows EmbeddedBrowserSignInDialog). The web view uses a non-persistent website data store, so sign-in
/// cookies never reach the reader's or the editor's web views and theirs never reach the sign-in page. Create and
/// present it on the main thread.
/// </summary>
internal sealed class EmbeddedBrowserSignInSheet : NSObject
{
    private const double PreferredWidth = 520;
    private const double PreferredHeight = 640;

    private readonly Uri _startUri;
    private readonly Uri _redirectUri;
    private readonly TaskCompletionSource<EmbeddedBrowserAuthResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NSWindow? _sheet;
    private NSWindow? _parent;
    private WKWebView? _webView;
    private NSProgressIndicator? _loading;
    private NavigationDelegate? _navigation;
    private UIDelegate? _ui;
    private NSObject? _parentClosing;

    public EmbeddedBrowserSignInSheet(Uri startUri, Uri redirectUri)
    {
        _startUri = startUri;
        _redirectUri = redirectUri;
    }

    public Task<EmbeddedBrowserAuthResult> Completion => _completion.Task;

    public void Present(NSWindow parent)
    {
        _parent = parent;
        var size = new CGSize(
            Math.Min(PreferredWidth, Math.Max(320, parent.Frame.Width - 80)),
            Math.Min(PreferredHeight, Math.Max(320, parent.Frame.Height - 80)));

        var sheet = new NSWindow(new CGRect(CGPoint.Empty, size), NSWindowStyle.Titled | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = Translator.EmbeddedBrowserSignInDialog_Title,
            MinSize = new CGSize(320, 320)
        };
        sheet.ReleaseWhenClosed(false);
        _sheet = sheet;

        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
        configuration.Preferences.JavaScriptCanOpenWindowsAutomatically = false;
        var webView = new WKWebView(CGRect.Empty, configuration) { TranslatesAutoresizingMaskIntoConstraints = false };
        _navigation = new NavigationDelegate(this);
        _ui = new UIDelegate(this);
        webView.NavigationDelegate = _navigation;
        webView.UIDelegate = _ui;
        _webView = webView;

        var title = WinoStyle.Label(Translator.EmbeddedBrowserSignInDialog_Title, WinoStyle.Heading);
        _loading = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var cancel = new NSButton { Title = Translator.Buttons_Cancel, BezelStyle = NSBezelStyle.Rounded, KeyEquivalent = "\u001b", TranslatesAutoresizingMaskIntoConstraints = false };
        cancel.Activated += (_, _) => Complete(EmbeddedBrowserAuthResult.Canceled);
        var footer = WinoLayout.HStack(WinoStyle.Space2, _loading, WinoLayout.Spacer(), cancel);
        footer.Alignment = NSLayoutAttribute.CenterY;

        var separatorTop = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        var separatorBottom = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
        var content = sheet.ContentView!;
        foreach (var view in new NSView[] { title, separatorTop, webView, separatorBottom, footer })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            content.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            title.TopAnchor.ConstraintEqualTo(content.TopAnchor, 14),
            title.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),
            title.TrailingAnchor.ConstraintLessThanOrEqualTo(content.TrailingAnchor, -20),
            separatorTop.TopAnchor.ConstraintEqualTo(title.BottomAnchor, 12),
            separatorTop.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            separatorTop.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            webView.TopAnchor.ConstraintEqualTo(separatorTop.BottomAnchor),
            webView.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            webView.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            separatorBottom.TopAnchor.ConstraintEqualTo(webView.BottomAnchor),
            separatorBottom.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            separatorBottom.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            footer.TopAnchor.ConstraintEqualTo(separatorBottom.BottomAnchor, 10),
            footer.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor, 20),
            footer.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -20),
            footer.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -12)
        ]);

        // Closing the owning window ends the sign-in as cancelled.
        _parentClosing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => Complete(EmbeddedBrowserAuthResult.Canceled), parent);

        parent.BeginSheet(sheet, _ => { });
        _loading.StartAnimation(null);

        var url = NSUrl.FromString(_startUri.AbsoluteUri);
        if (url is null)
        {
            Complete(EmbeddedBrowserAuthResult.Failed);
            return;
        }
        webView.LoadRequest(new NSUrlRequest(url));
    }

    /// <summary>Ends the sign-in as cancelled, for the caller's cancellation token.</summary>
    public void Cancel() => Complete(EmbeddedBrowserAuthResult.Canceled);

    private bool TryCaptureRedirect(NSUrl? url)
    {
        var text = url?.AbsoluteString;
        if (!EmbeddedBrowserAuthResult.IsRedirect(text, _redirectUri) || !Uri.TryCreate(text, UriKind.Absolute, out var redirect))
            return false;

        Complete(EmbeddedBrowserAuthResult.Success(redirect));
        return true;
    }

    private void Complete(EmbeddedBrowserAuthResult result)
    {
        if (!_completion.TrySetResult(result)) return;

        // Completion can come from inside a WebKit delegate callback; tear down after it returns.
        BeginInvokeOnMainThread(Teardown);
    }

    private void Teardown()
    {
        if (_parentClosing is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_parentClosing);
            _parentClosing.Dispose();
            _parentClosing = null;
        }

        if (_webView is not null)
        {
            _webView.StopLoading();
            _webView.NavigationDelegate = null!;
            _webView.UIDelegate = null!;
        }
        _loading?.StopAnimation(null);

        if (_sheet is not null)
        {
            if (_parent is not null && ReferenceEquals(_sheet.SheetParent, _parent)) _parent.EndSheet(_sheet);
            _sheet.OrderOut(null);
        }

        _webView?.RemoveFromSuperview();
        _webView?.Dispose();
        _webView = null;
        _navigation?.Dispose();
        _navigation = null;
        _ui?.Dispose();
        _ui = null;
        _sheet?.Dispose();
        _sheet = null;
        _parent = null;
    }

    private void LoadingFinished() => _loading?.StopAnimation(null);

    private sealed class NavigationDelegate(EmbeddedBrowserSignInSheet owner) : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url;
            if (owner.TryCaptureRedirect(url))
            {
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }

            // A custom-scheme or other non-web address would hand the sign-in to another app; it never leaves the sheet.
            var scheme = url?.Scheme?.ToLowerInvariant();
            decisionHandler(scheme is "https" or "http" or "about" or "data" or "blob"
                ? WKNavigationActionPolicy.Allow
                : WKNavigationActionPolicy.Cancel);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => owner.LoadingFinished();

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) => owner.LoadingFinished();

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) => owner.LoadingFinished();
    }

    private sealed class UIDelegate(EmbeddedBrowserSignInSheet owner) : WKUIDelegate
    {
        // Pop-ups stay in this view: a separate window would sit outside the sheet and miss the redirect capture.
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            var url = navigationAction.Request.Url;
            if (!owner.TryCaptureRedirect(url) && url?.Scheme?.ToLowerInvariant() is "https" or "http")
                webView.LoadRequest(new NSUrlRequest(url));
            return null;
        }
    }
}
