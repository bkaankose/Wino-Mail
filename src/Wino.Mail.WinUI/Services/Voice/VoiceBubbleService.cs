using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.ViewModels.Voice;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services.Voice;

/// <summary>
/// The Windows side of the voice bubble: the tray-anchored popup window, its global shortcut, the XAML frame clock
/// and the theme. The flow itself runs in the shared <see cref="VoiceBubbleController"/>.
/// </summary>
internal sealed class VoiceBubbleService : IVoiceBubbleHost, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly INativeAppService _nativeAppService;
    private readonly INewThemeService _themeService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly Func<RectInt32?> _getAnchor;
    private readonly VoiceBubbleHost _host;
    private readonly VoiceBubbleController _controller;
    private TimeSpan _lastRenderTime;
    private bool _isRendering;
    private bool _disposed;

    internal VoiceBubbleService(IServiceProvider services, DispatcherQueue dispatcher, INativeAppService nativeAppService, Func<RectInt32?> getAnchor)
    {
        _dispatcher = dispatcher;
        _nativeAppService = nativeAppService;
        _getAnchor = getAnchor;
        _themeService = services.GetRequiredService<INewThemeService>();
        _underlyingThemeService = services.GetRequiredService<IUnderlyingThemeService>();

        _host = new VoiceBubbleHost();
        _host.HotKeyPressed += VoiceHostHotKeyPressed;

        _themeService.ElementThemeChanged += ThemeServiceElementThemeChanged;
        ApplyTheme(_themeService.RootTheme);

        var planHost = WinoApplication.Current is App app ? new VoicePlanHost(app) : null;
        _controller = new VoiceBubbleController(this, planHost, services);
    }

    public IVoiceBubbleView Bubble => _host.Bubble;

    public event EventHandler<VoiceBubbleInputMode>? HotKeyPressed;

    public event EventHandler<double>? FrameRendered;

    /// <summary>Tries a shortcut before Settings saves it; see <see cref="VoiceBubbleController.TryConfigureHotKey(VoiceBubbleInputMode, bool, HotKeyGesture)"/>.</summary>
    internal bool TryConfigureHotKey(VoiceBubbleInputMode mode, bool enabled, HotKeyGesture gesture) => !_disposed && _controller.TryConfigureHotKey(mode, enabled, gesture);

    /// <summary>Opens the assistant for speaking or typing (tray menu, Settings).</summary>
    internal void Open(VoiceBubbleInputMode mode) => _controller.Open(mode);

    public void Show(VoiceBubblePosition position) => _host.Show(_getAnchor(), _nativeAppService.GetTaskbarPosition(), position);

    public void Hide() => _host.Hide();

    public bool CanRegisterHotKey(HotKeyGesture gesture) => VoiceBubbleHost.IsRegistrable(gesture, out _);

    public bool TrySetHotKey(VoiceBubbleInputMode mode, HotKeyGesture? gesture) => _host.TrySetHotKey(mode, gesture);

    public void StartFrames()
    {
        if (_isRendering)
            return;

        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    public void StopFrames()
    {
        if (!_isRendering)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
    }

    public void Post(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controller.Dispose();
        StopFrames();
        _themeService.ElementThemeChanged -= ThemeServiceElementThemeChanged;
        _host.HotKeyPressed -= VoiceHostHotKeyPressed;
        _host.Dispose();
    }

    private void VoiceHostHotKeyPressed(object? sender, VoiceBubbleInputMode mode) => HotKeyPressed?.Invoke(this, mode);

    [WinRT.DynamicWindowsRuntimeCast(typeof(RenderingEventArgs))]
    private void OnRendering(object? sender, object e)
    {
        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : _lastRenderTime;
        var elapsed = _lastRenderTime == TimeSpan.Zero ? 0 : (now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;
        FrameRendered?.Invoke(this, elapsed);
    }

    private void ThemeServiceElementThemeChanged(object? sender, ApplicationElementTheme theme)
        => Post(() => ApplyTheme(theme));

    private void ApplyTheme(ApplicationElementTheme theme)
    {
        if (_disposed)
            return;

        _host.ApplyTheme(theme == ApplicationElementTheme.Default
            ? _underlyingThemeService.IsUnderlyingThemeDark() ? ElementTheme.Dark : ElementTheme.Light
            : theme.ToWindowsElementTheme());
    }
}
