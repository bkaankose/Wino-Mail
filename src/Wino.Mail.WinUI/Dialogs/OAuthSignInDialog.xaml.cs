using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Wino.Authentication.Oidc;
using WebView2Control = Microsoft.UI.Xaml.Controls.WebView2;

namespace Wino.Mail.Dialogs;

/// <summary>Hosts OIDC sign-in in a WebView2 and captures the redirect in-process.</summary>
public sealed partial class OAuthSignInDialog : ContentDialog
{
    private readonly string _authorizationUrl;
    private readonly string _redirectUri;

    public RedirectResult Result { get; private set; } = new(null, null, null);

    private bool _redirectCaptured;

    public OAuthSignInDialog(string authorizationUrl, string redirectUri)
    {
        InitializeComponent();

        _authorizationUrl = authorizationUrl;
        _redirectUri = redirectUri;
    }

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            await AuthWebView.EnsureCoreWebView2Async();

            try
            {
                AuthWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                AuthWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            }
            catch (Exception settingsEx)
            {
                // Not all settings are implemented on every WebView2 runtime; non-fatal.
                Log.Warning(settingsEx, "Could not apply OAuth WebView settings.");
            }

            AuthWebView.NavigationStarting += OnNavigationStarting;
            AuthWebView.NavigationCompleted += OnNavigationCompleted;

            // The control-level NavigationStarting does not fire for every server-side redirect into the
            // reply URL. The core-level event and the post-navigation Source check below are fallbacks;
            // whichever sees the redirect URI first wins.
            AuthWebView.CoreWebView2.NavigationStarting += OnCoreNavigationStarting;
            AuthWebView.CoreWebView2.SourceChanged += OnCoreSourceChanged;

            AuthWebView.CoreWebView2.Navigate(_authorizationUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize the OAuth sign-in WebView2.");
            Result = new RedirectResult(null, null, "webview_initialization_failed");
            Hide();
        }
    }

    private void OnNavigationStarting(WebView2Control sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // Cancel before the trusted redirect page loads; the code is already on the URL.
        if (IsRedirectUri(args.Uri) && TryCaptureRedirect(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnCoreNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (IsRedirectUri(args.Uri) && TryCaptureRedirect(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void OnCoreSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
    {
        var currentUri = sender.Source;
        if (IsRedirectUri(currentUri))
        {
            TryCaptureRedirect(currentUri);
        }
    }

    private void OnNavigationCompleted(WebView2Control sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;

        // Fallback: if no starting event flagged the redirect (server-side redirects are not
        // surfaced on all runtimes), the reply page has loaded but the code is still in the URL.
        var currentUri = sender.Source?.AbsoluteUri;
        if (currentUri != null && IsRedirectUri(currentUri))
        {
            TryCaptureRedirect(currentUri);
        }
    }

    private bool IsRedirectUri(string uri)
    {
        // Compare scheme+host+port+path exactly (the auth code rides in the query, which we ignore here). A plain
        // StartsWith would let https://host/callbackEvil match a https://host/callback redirect URI.
        if (string.IsNullOrEmpty(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(_redirectUri, UriKind.Absolute, out var expected))
            return false;

        return Uri.Compare(actual, expected,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private bool TryCaptureRedirect(string uri)
    {
        if (_redirectCaptured)
            return true;

        _redirectCaptured = true;
        Result = OAuthRedirectParser.ParseUrl(uri);
        Hide();

        return true;
    }
}
