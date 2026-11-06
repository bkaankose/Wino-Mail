using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;
using Windows.Storage;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Dialogs;

/// <summary>
/// Hosts an identity provider's sign-in page in a WebView2 and captures the redirect before it loads. Presented only
/// by the dialog service. The browser profile lives in its own user data folder, so sign-in cookies never reach the
/// reader's or the editor's WebView2 and theirs never reach the sign-in page.
/// </summary>
public sealed partial class EmbeddedBrowserSignInDialog : ContentDialog
{
    private const string UserDataFolderName = "exchange-signin";

    private const double PreferredWidth = 520;
    private const double PreferredHeight = 620;
    private const double MinimumWidth = 280;
    private const double MinimumHeight = 240;

    // The dialog's padding, title and button row, and its distance from the window edges.
    private const double HorizontalChrome = 96;
    private const double VerticalChrome = 240;

    private readonly Uri _startUri;
    private readonly Uri _redirectUri;
    private CoreWebView2? _coreWebView;
    private bool _isCompleted;

    public EmbeddedBrowserAuthResult Result { get; private set; } = EmbeddedBrowserAuthResult.Failed;

    public EmbeddedBrowserSignInDialog(Uri startUri, Uri redirectUri)
    {
        _startUri = startUri;
        _redirectUri = redirectUri;

        InitializeComponent();
    }

    /// <summary>Ends the sign-in as cancelled. Before the dialog opens, it closes as soon as it does.</summary>
    public void Cancel() => Complete(EmbeddedBrowserAuthResult.Canceled);

    private async void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_isCompleted)
        {
            Hide();
            return;
        }

        try
        {
            var userDataFolder = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, UserDataFolderName);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, userDataFolder, new CoreWebView2EnvironmentOptions());
            await SignInWebView.EnsureCoreWebView2Async(environment);

            if (_isCompleted)
                return;

            var coreWebView = SignInWebView.CoreWebView2 ?? throw new ObjectDisposedException(nameof(SignInWebView));
            var settings = coreWebView.Settings;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
#endif

            coreWebView.FrameNavigationStarting += CoreFrameNavigationStarting;
            coreWebView.NewWindowRequested += CoreNewWindowRequested;
            coreWebView.LaunchingExternalUriScheme += CoreLaunchingExternalUriScheme;
            _coreWebView = coreWebView;

            coreWebView.Navigate(_startUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            // An async void handler must not let the failure end the process; the caller reports the failed sign-in.
            Log.Error(ex, "The embedded sign-in browser could not start.");
            Complete(EmbeddedBrowserAuthResult.Failed);
        }
    }

    private void WebViewNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (TryCaptureRedirect(args.Uri))
            args.Cancel = true;
    }

    private void CoreFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (TryCaptureRedirect(args.Uri))
            args.Cancel = true;
    }

    private void CoreLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        // A custom-scheme redirect would otherwise start another app; no other scheme may leave the sign-in page.
        args.Cancel = true;
        TryCaptureRedirect(args.Uri);
    }

    private void CoreNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Pop-ups stay in this view: a separate window would sit outside the dialog and miss the redirect capture.
        args.Handled = true;

        if (TryCaptureRedirect(args.Uri))
            return;

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) &&
            (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
        {
            sender.Navigate(target.AbsoluteUri);
        }
    }

    private void WebViewNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private bool TryCaptureRedirect(string? uri)
    {
        if (!EmbeddedBrowserAuthResult.IsRedirect(uri, _redirectUri) || !Uri.TryCreate(uri, UriKind.Absolute, out var redirect))
            return false;

        Complete(EmbeddedBrowserAuthResult.Success(redirect));
        return true;
    }

    private void Complete(EmbeddedBrowserAuthResult result)
    {
        if (_isCompleted)
            return;

        _isCompleted = true;
        Result = result;
        Hide();
    }

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Cancel, Escape and the window closing all end here without a result of their own.
        if (_isCompleted)
            return;

        _isCompleted = true;
        Result = EmbeddedBrowserAuthResult.Canceled;
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (XamlRoot is not null)
            XamlRoot.Changed -= XamlRootChanged;

        if (_coreWebView is not null)
        {
            _coreWebView.FrameNavigationStarting -= CoreFrameNavigationStarting;
            _coreWebView.NewWindowRequested -= CoreNewWindowRequested;
            _coreWebView.LaunchingExternalUriScheme -= CoreLaunchingExternalUriScheme;
            _coreWebView = null;
        }

        SignInWebView.Close();
    }

    private void DialogLoaded(object sender, RoutedEventArgs e)
    {
        FitToWindow(XamlRoot);
        XamlRoot.Changed += XamlRootChanged;
    }

    private void XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => FitToWindow(sender);

    private void FitToWindow(XamlRoot? root)
    {
        if (root is null)
            return;

        SignInContent.Width = Math.Clamp(root.Size.Width - HorizontalChrome, MinimumWidth, PreferredWidth);
        SignInContent.Height = Math.Clamp(root.Size.Height - VerticalChrome, MinimumHeight, PreferredHeight);
    }
}
