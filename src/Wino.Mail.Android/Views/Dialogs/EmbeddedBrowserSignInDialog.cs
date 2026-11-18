using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.WebKit;
using Google.Android.Material.AppBar;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Authentication;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Dialogs;

/// <summary>
/// Hosts an identity provider's sign-in page in a WebView in a full-screen dialog and captures the redirect before it
/// loads (the Mac EmbeddedBrowserSignInSheet, the Windows EmbeddedBrowserSignInDialog). Where WebView supports profiles
/// the page runs in its own profile, emptied before and after, so sign-in cookies never reach the reader's or
/// the editor's web views and theirs never reach the sign-in page. Older WebViews share one cookie jar per process, so
/// the cookies and web storage are cleared before and after instead. Create, show and cancel on the main thread.
/// </summary>
internal sealed class EmbeddedBrowserSignInDialog
{
    private const string ProfileName = "wino-embedded-sign-in";

    private readonly Uri _startUri;
    private readonly Uri _redirectUri;
    private readonly TaskCompletionSource<EmbeddedBrowserAuthResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _usesProfile;
    private AppCompatDialog? _dialog;
    private WebView? _webView;
    private LinearProgressIndicator? _loading;

    public EmbeddedBrowserSignInDialog(Uri startUri, Uri redirectUri)
    {
        _startUri = startUri;
        _redirectUri = redirectUri;
        _usesProfile = WebViewFeature.IsFeatureSupported(WebViewFeature.MultiProfile);
    }

    public Task<EmbeddedBrowserAuthResult> Completion => _completion.Task;

    public void Show(Activity activity)
    {
        PrepareStorage();

        var dialog = new AppCompatDialog(activity, Resource.Style.Theme_Wino_FullScreenDialog);
        _dialog = dialog;
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(WinoColors.ZoneFill(activity));

        var toolbar = new MaterialToolbar(activity) { Title = Translator.EmbeddedBrowserSignInDialog_Title };
        toolbar.SetNavigationIcon(global::Android.Resource.Drawable.IcMenuCloseClearCancel);
        toolbar.NavigationContentDescription = Translator.Buttons_Cancel;
        toolbar.NavigationIcon?.SetTint(WinoColors.PrimaryText(activity));
        toolbar.SetTitleTextColor(WinoColors.PrimaryText(activity));
        toolbar.NavigationClick += (_, _) => Complete(EmbeddedBrowserAuthResult.Canceled);
        root.AddView(toolbar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoUnits.Dp(activity, 64)));

        _loading = new LinearProgressIndicator(activity) { Indeterminate = true };
        root.AddView(_loading, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var webView = new WebView(activity);
        _webView = webView;
        // The profile must be set before the WebView loads anything.
        if (_usesProfile) WebViewCompat.SetProfile(webView, ProfileName);
        if (webView.Settings is { } settings)
        {
            settings.JavaScriptEnabled = true;
            settings.DomStorageEnabled = true;
            settings.JavaScriptCanOpenWindowsAutomatically = false;
            // Pop-ups load in this view: a second window would miss the redirect capture.
            settings.SetSupportMultipleWindows(false);
            settings.AllowFileAccess = false;
            settings.AllowContentAccess = false;
            settings.CacheMode = CacheModes.NoCache;
        }
        webView.SetBackgroundColor(Color.White);
        webView.SetWebViewClient(new SignInClient(this));
        root.AddView(webView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        dialog.SetContentView(root);
        dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        WinoInsets.Apply(root);
        // Back, or the activity going away (rotation recreates it), ends the sign-in as cancelled.
        dialog.DismissEvent += (_, _) => Complete(EmbeddedBrowserAuthResult.Canceled);
        dialog.Show();

        webView.LoadUrl(_startUri.AbsoluteUri);
    }

    /// <summary>Ends the sign-in as cancelled, for the caller's cancellation token.</summary>
    public void Cancel() => Complete(EmbeddedBrowserAuthResult.Canceled);

    private bool TryCaptureRedirect(string? url)
    {
        if (!EmbeddedBrowserAuthResult.IsRedirect(url, _redirectUri) || !Uri.TryCreate(url, UriKind.Absolute, out var redirect))
            return false;

        Complete(EmbeddedBrowserAuthResult.Success(redirect));
        return true;
    }

    private void Complete(EmbeddedBrowserAuthResult result)
    {
        if (!_completion.TrySetResult(result)) return;

        // Completion can come from inside a WebView callback; tear down after it returns.
        new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).Post(Teardown);
    }

    private void Teardown()
    {
        var webView = _webView;
        _webView = null;
        if (webView is not null)
        {
            webView.StopLoading();
            webView.SetWebViewClient(new WebViewClient());
            (webView.Parent as ViewGroup)?.RemoveView(webView);
            webView.Destroy();
        }

        var dialog = _dialog;
        _dialog = null;
        if (dialog?.IsShowing == true) dialog.Dismiss();

        ClearStorage();
    }

    /// <summary>Starts from empty storage: the sign-in page sees no earlier session.</summary>
    private void PrepareStorage()
    {
        if (_usesProfile)
        {
            try { IProfileStore.Instance.GetOrCreateProfile(ProfileName); }
            catch (Java.Lang.Exception exception) { Serilog.Log.Warning(exception, "Embedded sign-in could not create its WebView profile."); }
        }
        ClearStorage();
    }

    /// <summary>
    /// Empties the sign-in storage. A WebView profile stays loaded for the life of the process once a WebView used it, so
    /// it cannot be deleted; its cookies and web storage are removed instead, before and after every sign-in.
    /// </summary>
    private void ClearStorage()
    {
        try
        {
            if (_usesProfile && IProfileStore.Instance.GetProfile(ProfileName) is { } profile)
            {
                profile.CookieManager.RemoveAllCookies(null);
                profile.CookieManager.Flush();
                profile.WebStorage.DeleteAllData();
            }
            else if (!_usesProfile) ClearSharedStorage();
        }
        catch (Java.Lang.Exception exception)
        {
            Serilog.Log.Warning(exception, "Embedded sign-in could not clear its WebView storage.");
        }
    }

    /// <summary>
    /// Without profiles every WebView in the process shares one cookie jar. The reader and editor keep no sign-in of
    /// their own (remote content loads without credentials), so emptying it loses nothing they need.
    /// </summary>
    private static void ClearSharedStorage()
    {
        CookieManager.Instance?.RemoveAllCookies(null);
        CookieManager.Instance?.Flush();
        WebStorage.Instance?.DeleteAllData();
    }

    private void LoadingFinished()
    {
        if (_loading is not null) _loading.Visibility = ViewStates.Gone;
    }

    private void LoadingStarted()
    {
        if (_loading is not null) _loading.Visibility = ViewStates.Visible;
    }

    private sealed class SignInClient(EmbeddedBrowserSignInDialog owner) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (owner.TryCaptureRedirect(url)) return true;

            // A custom-scheme or other non-web address would hand the sign-in to another app; it never leaves the dialog.
            var scheme = request?.Url?.Scheme?.ToLowerInvariant();
            return scheme is not ("https" or "http" or "about" or "data" or "blob");
        }

        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
        {
            // The first load and some server redirects do not pass ShouldOverrideUrlLoading.
            if (owner.TryCaptureRedirect(url))
            {
                view?.StopLoading();
                return;
            }
            owner.LoadingStarted();
            base.OnPageStarted(view, url, favicon);
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            owner.LoadingFinished();
            base.OnPageFinished(view, url);
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            if (request?.IsForMainFrame == true) owner.LoadingFinished();
            base.OnReceivedError(view, request, error);
        }
    }
}
