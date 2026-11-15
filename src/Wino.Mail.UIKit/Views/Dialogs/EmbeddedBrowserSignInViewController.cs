using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Mail.UIKit.Views.Dialogs;

/// <summary>
/// Hosts an identity provider's sign-in page in a WKWebView on a sheet and captures the redirect before it loads (the
/// Windows EmbeddedBrowserSignInDialog, the Mac EmbeddedBrowserSignInSheet). The web view uses a non-persistent website
/// data store, so sign-in cookies never reach the reader's or the editor's web views and theirs never reach the sign-in
/// page. Cancel, a swipe down or the caller's cancellation end it as cancelled. Create and present it on the main thread.
/// </summary>
internal sealed class EmbeddedBrowserSignInViewController : UIViewController
{
    private readonly Uri _startUri;
    private readonly Uri _redirectUri;
    private readonly TaskCompletionSource<EmbeddedBrowserAuthResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UINavigationController? _sheet;
    private WKWebView? _webView;
    private UIActivityIndicatorView? _loading;
    private NavigationDelegate? _navigation;
    private UIDelegate? _ui;
    private SheetDelegate? _sheetDelegate;

    public EmbeddedBrowserSignInViewController(Uri startUri, Uri redirectUri)
    {
        _startUri = startUri;
        _redirectUri = redirectUri;
    }

    public Task<EmbeddedBrowserAuthResult> Completion => _completion.Task;

    /// <summary>Presents the sign-in in its own navigation sheet over <paramref name="presenter"/> and starts loading.</summary>
    public void Present(UIViewController presenter)
    {
        Title = Translator.EmbeddedBrowserSignInDialog_Title;
        var sheet = new UINavigationController(this)
        {
            ModalPresentationStyle = UIModalPresentationStyle.PageSheet
        };
        _sheet = sheet;
        _sheetDelegate = new SheetDelegate(this);
        if (sheet.PresentationController is { } presentation) presentation.Delegate = _sheetDelegate;
        presenter.PresentViewController(sheet, true, null);

        var url = NSUrl.FromString(_startUri.AbsoluteUri);
        if (url is null)
        {
            Complete(EmbeddedBrowserAuthResult.Failed);
            return;
        }
        _ = View;
        _webView?.LoadRequest(new NSUrlRequest(url));
    }

    public override void LoadView()
    {
        var configuration = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
        configuration.Preferences.JavaScriptCanOpenWindowsAutomatically = false;
        var webView = new WKWebView(CGRect.Empty, configuration);
        _navigation = new NavigationDelegate(this);
        _ui = new UIDelegate(this);
        webView.NavigationDelegate = _navigation;
        webView.UIDelegate = _ui;
        _webView = webView;
        View = webView;

        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.Buttons_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Cancel());
        _loading = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        _loading.StartAnimating();
        NavigationItem.RightBarButtonItem = new UIBarButtonItem(_loading);
    }

    /// <summary>Ends the sign-in as cancelled, for the Cancel button and the caller's cancellation token.</summary>
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
        if (_webView is not null)
        {
            _webView.StopLoading();
            _webView.NavigationDelegate = null!;
            _webView.UIDelegate = null!;
        }
        _loading?.StopAnimating();

        var sheet = _sheet;
        _sheet = null;
        if (sheet?.PresentationController is { } presentation) presentation.Delegate = null!;
        if (sheet?.PresentingViewController is not null) sheet.DismissViewController(true, () => Release(sheet));
        else Release(sheet);
    }

    private void Release(UINavigationController? sheet)
    {
        NavigationItem.LeftBarButtonItem = null;
        NavigationItem.RightBarButtonItem = null;
        _webView?.Dispose();
        _webView = null;
        _navigation?.Dispose();
        _navigation = null;
        _ui?.Dispose();
        _ui = null;
        _sheetDelegate?.Dispose();
        _sheetDelegate = null;
        _loading?.Dispose();
        _loading = null;
        sheet?.Dispose();
    }

    private void LoadingFinished() => _loading?.StopAnimating();

    private void LoadingStarted() => _loading?.StartAnimating();

    private sealed class NavigationDelegate(EmbeddedBrowserSignInViewController owner) : WKNavigationDelegate
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

        public override void DidStartProvisionalNavigation(WKWebView webView, WKNavigation navigation) => owner.LoadingStarted();

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => owner.LoadingFinished();

        public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error) => owner.LoadingFinished();

        public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error) => owner.LoadingFinished();
    }

    private sealed class UIDelegate(EmbeddedBrowserSignInViewController owner) : WKUIDelegate
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

    /// <summary>A swipe down closes the sheet without Cancel; it ends the sign-in as cancelled.</summary>
    private sealed class SheetDelegate(EmbeddedBrowserSignInViewController owner) : UIAdaptivePresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => owner.Cancel();
    }
}
