using Wino.Core.Domain;
using Wino.Core.Domain.Models.Authentication;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// An identity provider's sign-in page in a modal window with its own WebKitGTK view (macOS EmbeddedBrowserSignInSheet,
/// Windows EmbeddedBrowserSignInDialog). The first navigation to the redirect address is stopped before it loads and
/// returned; the view runs on an ephemeral network session, so sign-in cookies die with the window and never meet the
/// reader's. Closing the window cancels. Create and use it on the main thread.
/// </summary>
internal sealed class EmbeddedBrowserSignInWindow
{
    private readonly Uri _startUri;
    private readonly Uri _redirectUri;
    private readonly TaskCompletionSource<EmbeddedBrowserAuthResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IDisposable> _owned = [];
    private Adw.Window? _window;
    private WebKit.WebView? _webView;
    private Gtk.ProgressBar? _progress;
    private Gtk.Label? _host;
    private bool _loadedOnce;

    public EmbeddedBrowserSignInWindow(Uri startUri, Uri redirectUri)
    {
        _startUri = startUri;
        _redirectUri = redirectUri;
    }

    public Task<EmbeddedBrowserAuthResult> Completion => _completion.Task;

    /// <summary>The host of the page on screen, for checks.</summary>
    public string CurrentHost => _host?.GetText() ?? string.Empty;

    public string Title => _webView?.GetTitle() ?? string.Empty;

    public void Present(Gtk.Window? parent)
    {
        WebKit.Module.Initialize();
        var window = Adw.Window.New();
        _window = window;
        window.SetTitle(Translator.EmbeddedBrowserSignInDialog_Title);
        window.SetModal(true);
        window.SetDestroyWithParent(true);
        if (parent is not null) window.SetTransientFor(parent);
        window.SetDefaultSize(520, 680);
        window.SetSizeRequest(360, 360);

        var title = Gtk.Label.New(Translator.EmbeddedBrowserSignInDialog_Title);
        title.AddCssClass("title");
        title.SetEllipsize(Pango.EllipsizeMode.End);
        _host = Gtk.Label.New(string.Empty);
        _host.AddCssClass("subtitle");
        _host.SetEllipsize(Pango.EllipsizeMode.Middle);
        var heading = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        heading.SetValign(Gtk.Align.Center);
        heading.Append(title);
        heading.Append(_host);

        var cancel = WinoA11y.Rename(Gtk.Button.NewWithLabel(Translator.Buttons_Cancel), Translator.Buttons_Cancel);
        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(heading);
        header.PackStart(cancel);

        _progress = Gtk.ProgressBar.New();
        _progress.AddCssClass("osd");
        _progress.SetValign(Gtk.Align.Start);

        // Same construction as the reader's session: an ephemeral network session keeps cookies and storage in memory.
        using var session = new GObject.Value(WebKit.NetworkSession.NewEphemeral());
        var webView = WebKit.WebView.NewWithProperties([new GObject.ConstructArgument("network-session", session)]);
        _webView = webView;
        var settings = webView.GetSettings();
        settings.SetEnableDeveloperExtras(false);
        settings.SetAllowFileAccessFromFileUrls(false);
        settings.SetJavascriptCanOpenWindowsAutomatically(false);
        webView.SetVexpand(true);
        webView.SetHexpand(true);

        var overlay = Gtk.Overlay.New();
        overlay.SetChild(webView);
        overlay.AddOverlay(_progress);

        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(overlay);
        window.SetContent(view);

        _owned.Add(GtkSignals.OnClicked(cancel, () => Complete(EmbeddedBrowserAuthResult.Canceled)));
        _owned.Add(GtkSignals.ConnectReturning<WebKit.WebView, WebKit.WebView.DecidePolicySignalArgs, bool>(
            h => webView.OnDecidePolicy += h, h => webView.OnDecidePolicy -= h, (_, args) => DecidePolicy(args), false));
        _owned.Add(GtkSignals.ConnectReturning<WebKit.WebView, WebKit.WebView.LoadFailedSignalArgs, bool>(
            h => webView.OnLoadFailed += h, h => webView.OnLoadFailed -= h, (_, args) => LoadFailed(args), false));
        _owned.Add(GtkSignals.Connect<WebKit.WebView, WebKit.WebView.LoadChangedSignalArgs>(
            h => webView.OnLoadChanged += h, h => webView.OnLoadChanged -= h, (_, args) =>
            {
                if (args.LoadEvent == WebKit.LoadEvent.Finished) _loadedOnce = true;
            }));
        _owned.Add(GtkSignals.OnNotify(webView, "uri", UpdateHost));
        _owned.Add(GtkSignals.OnNotify(webView, "estimated-load-progress", UpdateProgress));
        _owned.Add(GtkSignals.ConnectReturning<Gtk.Window, bool>(h => window.OnCloseRequest += h, h => window.OnCloseRequest -= h, (_, _) =>
        {
            Complete(EmbeddedBrowserAuthResult.Canceled);
            return true;
        }, false));

        window.Present();
        webView.LoadUri(_startUri.AbsoluteUri);
    }

    /// <summary>Ends the sign-in as cancelled and closes the window.</summary>
    public void Cancel() => Complete(EmbeddedBrowserAuthResult.Canceled);

    private bool DecidePolicy(WebKit.WebView.DecidePolicySignalArgs args)
    {
        if (args.DecisionType is not (WebKit.PolicyDecisionType.NavigationAction or WebKit.PolicyDecisionType.NewWindowAction)) return false;
        if (args.Decision is not WebKit.NavigationPolicyDecision navigation) return false;
        var address = navigation.GetNavigationAction()?.GetRequest()?.GetUri();
        if (EmbeddedBrowserAuthResult.IsRedirect(address, _redirectUri) && Uri.TryCreate(address, UriKind.Absolute, out var redirect))
        {
            args.Decision.Ignore();
            Complete(EmbeddedBrowserAuthResult.Success(redirect));
            return true;
        }
        if (args.DecisionType == WebKit.PolicyDecisionType.NewWindowAction)
        {
            args.Decision.Ignore();
            if (!string.IsNullOrEmpty(address)) _webView?.LoadUri(address);
            return true;
        }
        return false;
    }

    private bool LoadFailed(WebKit.WebView.LoadFailedSignalArgs args)
    {
        if (!_loadedOnce && !_completion.Task.IsCompleted)
        {
            Serilog.Log.Warning("The sign-in page on {Host} could not be loaded.", Uri.TryCreate(args.FailingUri, UriKind.Absolute, out var failing) ? failing.Host : "?");
            Complete(EmbeddedBrowserAuthResult.Failed);
        }
        return false;
    }

    private void UpdateHost()
    {
        var uri = _webView?.GetUri();
        _host?.SetText(Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : string.Empty);
    }

    private void UpdateProgress()
    {
        if (_webView is null || _progress is null) return;
        var progress = _webView.GetEstimatedLoadProgress();
        _progress.SetFraction(progress);
        _progress.SetVisible(progress < 1.0);
    }

    private void Complete(EmbeddedBrowserAuthResult result)
    {
        if (!_completion.TrySetResult(result)) return;
        // Completion can come from inside a WebKit signal; tear down after it returned.
        MainLoop.Post(Teardown);
    }

    private void Teardown()
    {
        foreach (var owned in _owned) owned.Dispose();
        _owned.Clear();
        _webView?.StopLoading();
        _webView = null;
        _progress = null;
        _host = null;
        _window?.Destroy();
        _window = null;
    }
}
