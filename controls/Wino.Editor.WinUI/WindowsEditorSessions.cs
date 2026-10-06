namespace Wino.Editor;

/// <summary>Page-owned portable access to the existing Windows editor. Invoke on its UI thread.</summary>
public sealed class WindowsHtmlMailEditorSession(WinoMailEditor control) : IHtmlMailEditorSession
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = control.DispatcherQueue;
    private readonly SessionOperationQueue _operations = new();
    public EditorState CurrentState => control.CurrentState;
    public EditorCommandCapabilities Capabilities => ((IEditorCommandTarget)control).Capabilities;
    private EventHandler<EditorState>? _StateChangedHandlers;
    public event EventHandler<EditorState>? StateChanged { add { _StateChangedHandlers += value; control.StateChanged += value; } remove { _StateChangedHandlers -= value; control.StateChanged -= value; } }
    private EventHandler<EditorShortcutKind>? _ShortcutRequestedHandlers;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested { add { _ShortcutRequestedHandlers += value; control.ShortcutRequested += value; } remove { _ShortcutRequestedHandlers -= value; control.ShortcutRequested -= value; } }
    private EventHandler? _ContentChangedHandlers;
    public event EventHandler? ContentChanged { add { _ContentChangedHandlers += value; control.ContentChanged += value; } remove { _ContentChangedHandlers -= value; control.ContentChanged -= value; } }
    private EventHandler<EditorApplicationShortcutGesture>? _ApplicationShortcutRequestedHandlers;
    public event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested { add { _ApplicationShortcutRequestedHandlers += value; control.ApplicationShortcutRequested += value; } remove { _ApplicationShortcutRequestedHandlers -= value; control.ApplicationShortcutRequested -= value; } }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => RunAsync(control.InitializeAsync, cancellationToken);
    public Task RenderHtmlAsync(string htmlBody, CancellationToken cancellationToken = default) => RunAsync(() => control.RenderHtmlAsync(htmlBody), cancellationToken);
    public Task<string?> GetHtmlBodyAsync(CancellationToken cancellationToken = default) => RunAsync(control.GetHtmlBodyAsync, cancellationToken);
    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) => RunAsync(() => control.SetThemeAsync(isDarkMode), cancellationToken);
    public Task SetDefaultTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) => RunAsync(() => control.SetDefaultTypographyAsync(fontFamily, fontSize), cancellationToken);
    public Task FocusEditorAsync(bool focusControlAsWell, CancellationToken cancellationToken = default) => RunAsync(() => control.FocusEditorAsync(focusControlAsWell), cancellationToken);
    public Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts, CancellationToken cancellationToken = default) => RunAsync(() => control.SetApplicationShortcutsAsync(shortcuts), cancellationToken);
    public Task InsertImagesAsync(IEnumerable<EditorImageInfo> images, CancellationToken cancellationToken = default) => RunAsync(() => control.InsertImagesAsync(images), cancellationToken);
    public Task ExecuteCommandAsync(EditorCommand command) => ExecuteCommandAsync(command, default);
    public Task ExecuteCommandAsync(EditorCommand command, CancellationToken cancellationToken) => RunAsync(() => control.ExecuteCommandAsync(command), cancellationToken);
    private Task RunAsync(Func<Task> operation, CancellationToken cancellationToken) =>
        _operations.RunAsync(token => WindowsSessionThread.InvokeAsync(_dispatcher, async () =>
        {
            await control.InitializeAsync(token);
            await operation();
        }), cancellationToken);
    private Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken) =>
        _operations.RunAsync(token => WindowsSessionThread.InvokeAsync(_dispatcher, async () =>
        {
            await control.InitializeAsync(token);
            return await operation();
        }), cancellationToken);
    public ValueTask DisposeAsync() => _operations.DisposeAsync(() => new ValueTask(WindowsSessionThread.InvokeAsync(_dispatcher, () =>
    {
        control.StateChanged -= _StateChangedHandlers;
        control.ShortcutRequested -= _ShortcutRequestedHandlers;
        control.ContentChanged -= _ContentChangedHandlers;
        control.ApplicationShortcutRequested -= _ApplicationShortcutRequestedHandlers;
        _StateChangedHandlers = null;
        _ShortcutRequestedHandlers = null;
        _ContentChangedHandlers = null;
        _ApplicationShortcutRequestedHandlers = null;
        control.Dispose();
        return Task.CompletedTask;
    })));
}

/// <summary>Page-owned portable reader access; Windows retains resource interception and delayed reuse.</summary>
public sealed class WindowsHtmlMailReaderSession(WinoMailRenderer control) : IHtmlMailReaderSession
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher = control.DispatcherQueue;
    private readonly SessionOperationQueue _operations = new();
    private EventHandler<RendererNavigationRequestedEventArgs>? _navigationHandlers;
    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested { add { _navigationHandlers += value; control.NavigationRequested += value; } remove { _navigationHandlers -= value; control.NavigationRequested -= value; } }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => RunAsync(control.InitializeAsync, cancellationToken);
    public Task RenderAsync(HtmlMailReaderRequest request, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        control.BlockRemoteResources = request.ResourcePolicy switch
        {
            RemoteContentPolicy.Blocked => true,
            RemoteContentPolicy.ImagesAndFontsAllowed => false,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        return control.RenderHtmlAsync(request.Html, request.RenderMode, request.ShouldLinkify);
    }, cancellationToken);
    public Task ClearAsync(CancellationToken cancellationToken = default) => RunAsync(control.ClearAsync, cancellationToken, initialize: false);
    public Task<string> GetOriginalHtmlAsync(CancellationToken cancellationToken = default) => _operations.RunAsync(_ => WindowsSessionThread.InvokeAsync(_dispatcher, control.GetOriginalHtmlAsync), cancellationToken);
    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default) => RunAsync(() => control.SetThemeAsync(isDarkMode), cancellationToken);
    public Task SetReaderTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default) => RunAsync(() => control.SetReaderTypographyAsync(fontFamily, fontSize), cancellationToken);
    public Task SetAccessibilityContextAsync(ReaderAccessibilityContext context, CancellationToken cancellationToken = default) => RunAsync(() => control.SetAccessibilityContextAsync(context.Subject, context.Sender, context.Date, context.BodyAutomationName, context.PlainTextFallbackAutomationName, context.AccessibleText), cancellationToken);
    public Task EnterIdleAsync(CancellationToken cancellationToken = default) => RunAsync(control.EnterIdleAsync, cancellationToken, initialize: false);
    private Task RunAsync(Func<Task> operation, CancellationToken cancellationToken, bool initialize = true) =>
        _operations.RunAsync(token => WindowsSessionThread.InvokeAsync(_dispatcher, async () =>
        {
            if (initialize) await control.WaitUntilLoadedAsync(token);
            await operation();
        }), cancellationToken);
    public ValueTask DisposeAsync() => _operations.DisposeAsync(() => new ValueTask(WindowsSessionThread.InvokeAsync(_dispatcher, async () =>
    {
        control.NavigationRequested -= _navigationHandlers;
        _navigationHandlers = null;
        await control.DisposeAsync();
    })));
}



internal static class WindowsSessionThread
{
    public static Task InvokeAsync(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<Task> operation) =>
        InvokeAsync(dispatcher, async () => { await operation(); return true; });

    public static Task<T> InvokeAsync<T>(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<Task<T>> operation)
    {
        if (dispatcher.HasThreadAccess) return operation();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await operation()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("The editor UI dispatcher is unavailable."));
        return completion.Task;
    }
}
