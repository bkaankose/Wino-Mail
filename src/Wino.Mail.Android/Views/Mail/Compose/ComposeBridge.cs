using CommunityToolkit.Mvvm.Messaging;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// The composer ViewModel's body hooks for the page's lifetime (the compose twin of the reader's ReaderBridge). A
/// fragment and its WebView are recreated on rotation, dark mode and resize, so the bridge keeps the last known body
/// HTML: the ViewModel's render goes to the view on screen (or waits for the next one), saving reads the body from the
/// editor on screen, and a view that goes away (<see cref="Pause"/>) hands its body back before its editor is disposed,
/// then saves the draft so the app never sits in the background without it.
/// </summary>
internal sealed class ComposeBridge : IDisposable
{
    public const string StateKey = "mail-compose";

    private readonly ComposePageViewModel _viewModel;
    private ComposeFragment? _view;
    private Task _capture = Task.CompletedTask;
    private bool _focusOnOpen;
    private bool _disposed;

    private ComposeBridge(ComposePageViewModel viewModel, MailItemViewModel? draft)
    {
        _viewModel = viewModel;
        // Windows ApplyInitialFocusAsync: the request is consumed once, by the first view.
        if (draft is { ShouldFocusComposerOnOpen: true })
        {
            draft.ShouldFocusComposerOnOpen = false;
            _focusOnOpen = true;
        }
        viewModel.RenderHtmlBodyAsyncFunc = RenderAsync;
        viewModel.GetHTMLBodyFunction = GetBodyAsync;
        viewModel.ApplySignatureHtmlFunc = ApplySignatureAsync;
        viewModel.CloseRequested += CloseRequested;
    }

    /// <summary>The body as last rendered or read from an editor; null before the draft loaded.</summary>
    public string? Html { get; private set; }

    /// <summary>True once the composer is closing (sent, discarded or closed): nothing is saved any more.</summary>
    public bool IsClosing { get; set; }

    /// <summary>The page's bridge, created on first use (the route's Prepare, or the first view). Main thread.</summary>
    public static ComposeBridge? For(PageEntry entry)
    {
        if (entry.State.TryGetValue(StateKey, out var existing)) return existing as ComposeBridge;
        if (entry.IsReleased || entry.ViewModel is not ComposePageViewModel viewModel) return null;
        var bridge = new ComposeBridge(viewModel, entry.Parameter as MailItemViewModel);
        entry.State[StateKey] = bridge;
        return bridge;
    }

    /// <summary>A new view: once the previous view's body is back, it shows the latest body.</summary>
    public void Attach(ComposeFragment view)
    {
        _view = view;
        view.Observe(RestoreAsync(view));
    }

    private async Task RestoreAsync(ComposeFragment view)
    {
        try { await _capture; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The composer body could not be read back."); }
        if (!ReferenceEquals(_view, view) || Html is not { } html) return;
        await view.RenderBodyAsync(html);
        await ApplyInitialFocusAsync(view);
    }

    public void Detach(ComposeFragment view)
    {
        if (ReferenceEquals(_view, view)) _view = null;
    }

    /// <summary>
    /// The view is pausing (backgrounded, rotated or replaced): read its body while its editor still exists, then save the
    /// draft. Main thread.
    /// </summary>
    public void Pause(ComposeFragment view)
    {
        if (_disposed || IsClosing) return;
        _capture = CaptureAsync(view);
        view.Observe(SaveAfterCaptureAsync());
    }

    private async Task CaptureAsync(ComposeFragment view)
    {
        if (await view.ReadEditorHtmlAsync() is { } html) Html = html;
    }

    private async Task SaveAfterCaptureAsync()
    {
        try { await _capture; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "The composer body could not be read before saving."); }
        if (_disposed || IsClosing) return;
        await _viewModel.UpdateMimeChangesAsync();
    }

    private async Task RenderAsync(string html)
    {
        Html = html;
        if (_view is not { } view) return;
        await view.RenderBodyAsync(html);
        await ApplyInitialFocusAsync(view);
    }

    private async Task ApplyInitialFocusAsync(ComposeFragment view)
    {
        var requested = _focusOnOpen;
        _focusOnOpen = false;
        await view.ApplyInitialFocusAsync(requested);
    }

    /// <summary>The editor on screen, else the body the last view handed back, else the draft's own body.</summary>
    private async Task<string> GetBodyAsync()
    {
        if (_view is { } view && await view.ReadEditorHtmlAsync() is { } html) Html = html;
        else
        {
            try { await _capture; }
            catch (Exception exception) { Serilog.Log.Warning(exception, "The composer body could not be read back."); }
        }
        return Html ?? _viewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
    }

    private Task ApplySignatureAsync(string html, string? previousHtml)
        => _view?.ApplySignatureAsync(html, previousHtml) ?? Task.CompletedTask;

    /// <summary>Sent, discarded or deleted elsewhere: close without asking (the ViewModel decided).</summary>
    private void CloseRequested(object? sender, EventArgs args)
    {
        IsClosing = true;
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.CloseRequested -= CloseRequested;
        // No-op hooks, as the reader leaves them: a late save keeps the body it knew.
        var body = Html ?? _viewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        _viewModel.RenderHtmlBodyAsyncFunc = static _ => Task.CompletedTask;
        _viewModel.ApplySignatureHtmlFunc = static (_, _) => Task.CompletedTask;
        _viewModel.GetHTMLBodyFunction = () => Task.FromResult(body);
        _view = null;
    }
}
