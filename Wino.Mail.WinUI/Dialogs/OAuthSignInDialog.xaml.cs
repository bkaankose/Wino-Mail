using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Wino.Core.Authentication;
using Wino.Mail.WinUI.Extensions;

namespace Wino.Mail.WinUI.Dialogs;

/// <summary>Hosts OIDC sign-in in WebView2 and captures the redirect in-process.</summary>
public sealed partial class OAuthSignInDialog : ContentDialog
{
    private readonly string _authorizationUrl;
    private readonly string _redirectUri;

    public RedirectResult Result { get; private set; } = new(null, null, null);

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
            var environment = await WebViewExtensions.GetIsolatedAuthEnvironmentAsync();
            await AuthWebView.EnsureCoreWebView2Async(environment);

            AuthWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            AuthWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            AuthWebView.NavigationStarting += OnNavigationStarting;
            AuthWebView.NavigationCompleted += OnNavigationCompleted;

            AuthWebView.CoreWebView2.Navigate(_authorizationUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize the OAuth sign-in WebView2.");
            Result = new RedirectResult(null, null, "webview_initialization_failed");
            Hide();
        }
    }

    private void OnNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (string.IsNullOrEmpty(args.Uri))
            return;

        // Cancel before the trusted redirect page loads; the code is already on the URL.
        if (args.Uri.StartsWith(_redirectUri, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
            Result = OAuthRedirectParser.ParseUrl(args.Uri);
            Hide();
        }
    }

    private void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        => LoadingRing.IsActive = false;
}
