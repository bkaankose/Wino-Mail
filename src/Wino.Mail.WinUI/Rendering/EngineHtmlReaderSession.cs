using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Editor;
using Wino.Renderer;
using Wino.Renderer.Accessibility;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.Theme;
using Wino.Renderer.WinUI;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Shows an HTML string in a <see cref="MailView"/>, for surfaces that have no message (a calendar event description).
/// </summary>
public sealed class EngineHtmlReaderSession : IHtmlMailReaderSession
{
    private readonly MailView _view;
    private readonly MailEngineHost _engineHost;
    private readonly EngineReaderPresentation _presentation;
    private string _html = string.Empty;
    private bool _disposed;

    public EngineHtmlReaderSession(MailView view, MailEngineHost engineHost, EngineReaderPresentation presentation)
    {
        _view = view;
        _engineHost = engineHost;
        _presentation = presentation;
        _view.LinkActivated += OnLinkActivated;
    }

    public event EventHandler<RendererNavigationRequestedEventArgs>? NavigationRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_view.Engine is null)
            _view.Engine = _engineHost.Engine;

        return Task.CompletedTask;
    }

    public async Task RenderAsync(HtmlMailReaderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await InitializeAsync(cancellationToken);

        _html = request.Html;
        var isBlocked = request.ResourcePolicy == RemoteContentPolicy.Blocked;

        await _view.LoadAsync(HtmlMailContent.FromHtml(request.Html), new ReaderLoadOptions
        {
            User = new UserPolicyAdjustments
            {
                RemoteContent = isBlocked ? RemoteContentMode.BlockAll : RemoteContentMode.BlockTrackers,
                Linkify = request.ShouldLinkify,
                UnwrapRedirectsOnActivation = true
            },
            Mode = request.RenderMode == HtmlMailRenderMode.Readability ? ReaderRenderMode.ReaderView : ReaderRenderMode.Original,
            Accessibility = _view.AccessibilityContext,
            RemoteFonts = !isBlocked,
            FontStylesheets = !isBlocked,
            Presentation = _presentation.CreateOptions()
        }, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _html = string.Empty;

        if (!_disposed && _view.Engine is not null)
            _view.Clear();

        return Task.CompletedTask;
    }

    public Task<string> GetOriginalHtmlAsync(CancellationToken cancellationToken = default) => Task.FromResult(_html);

    public Task SetThemeAsync(bool isDarkMode, CancellationToken cancellationToken = default)
    {
        _presentation.IsDark = isDarkMode;
        _presentation.ApplyTo(_view);
        return Task.CompletedTask;
    }

    public Task SetDarkModeMethodAsync(HtmlMailDarkModeMethod method, CancellationToken cancellationToken = default)
    {
        _presentation.Method = method == HtmlMailDarkModeMethod.Inverted ? DarkModeMethod.Inverted : DarkModeMethod.Adaptive;
        _presentation.ApplyTo(_view);
        return Task.CompletedTask;
    }

    public Task SetReaderTypographyAsync(string? fontFamily, int fontSize, CancellationToken cancellationToken = default)
    {
        _presentation.SetTypography(fontFamily, fontSize);
        _presentation.ApplyTo(_view);
        return Task.CompletedTask;
    }

    public Task SetAccessibilityContextAsync(ReaderAccessibilityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var defaults = new AccessibilityContext();
        _view.AccessibilityContext = new AccessibilityContext
        {
            Subject = context.Subject,
            Sender = context.Sender,
            Date = context.Date,
            BodyName = context.BodyAutomationName ?? defaults.BodyName,
            PlainTextFallbackName = context.PlainTextFallbackAutomationName ?? defaults.PlainTextFallbackName,
            FallbackText = context.AccessibleText
        };

        return Task.CompletedTask;
    }

    public async Task EnterIdleAsync(CancellationToken cancellationToken = default)
    {
        await ClearAsync(cancellationToken);
        _engineHost.Trim(MemoryTrimLevel.Idle);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _view.LinkActivated -= OnLinkActivated;
        _view.Engine = null;
        return ValueTask.CompletedTask;
    }

    private void OnLinkActivated(object? sender, LinkEventArgs e)
    {
        var address = e.Link.Kind switch
        {
            LinkKind.Web => e.Link.Effective,
            LinkKind.Mailto or LinkKind.Tel => e.Link.Original,
            _ => null
        };

        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            NavigationRequested?.Invoke(this, new RendererNavigationRequestedEventArgs(uri, e.Text));
    }
}
