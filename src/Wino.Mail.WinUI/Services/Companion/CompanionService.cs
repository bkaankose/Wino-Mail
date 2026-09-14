using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Controls.Companion;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

namespace Wino.Mail.WinUI.Services.Companion;

internal sealed class CompanionService : ICompanionService
{
    private readonly IServiceProvider _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly INativeAppService _nativeAppService;
    private readonly INewThemeService _themeService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly Func<(bool Success, RectInt32 Rect)> _getTrayIconRect;
    private readonly CompanionNavigationCallbacks _navigation;
    private readonly IWinoLogger _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CompanionDashboardViewModel? _viewModel;
    private CompanionFlyoutView? _view;
    private CompanionFlyoutHost? _host;
    private CompanionReadinessState _readiness = CompanionReadinessState.Initializing;
    private bool _enabled;
    private bool _sessionAvailable = true;
    private bool _disposed;

    internal CompanionService(
        IServiceProvider services,
        DispatcherQueue dispatcher,
        INativeAppService nativeAppService,
        DateTimeOffset sessionStartedAtUtc,
        Func<(bool Success, RectInt32 Rect)> getTrayIconRect,
        CompanionNavigationCallbacks navigation)
    {
        _services = services;
        _dispatcher = dispatcher;
        _nativeAppService = nativeAppService;
        _sessionStartedAtUtc = sessionStartedAtUtc;
        _getTrayIconRect = getTrayIconRect;
        _navigation = navigation;
        _logger = services.GetRequiredService<IWinoLogger>();
        _themeService = services.GetRequiredService<INewThemeService>();
        _underlyingThemeService = services.GetRequiredService<IUnderlyingThemeService>();
        _themeService.ElementThemeChanged += ThemeServiceElementThemeChanged;
    }

    public event EventHandler<string>? SessionDisabled;

    public bool IsAvailableForSession => _sessionAvailable && !_disposed;

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
                return;

            _enabled = enabled;
            if (!enabled)
                DisposeSurface();
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Changing companion enablement failed.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task SetReadinessAsync(
        CompanionReadinessState state,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _readiness = state;
        return Task.CompletedTask;
    }

    public void PrepareForTrayInteraction()
    {
        try
        {
            _host?.SuppressDeactivationForTrayInteraction();
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Preparing the companion tray interaction failed.");
        }
    }

    public async Task ToggleAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled || !IsAvailableForSession)
            return;

        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!_enabled || !IsAvailableForSession)
                return;

            EnsureUIThread();
            EnsureSurface();
            if (_host!.IsOpen)
            {
                HideCore();
                return;
            }

            var anchor = GetAnchor();
            var taskbarPosition = _nativeAppService.GetTaskbarPosition();
            await _viewModel!.OpenAsync(_readiness, cancellationToken);
            await _host.ShowAsync(anchor, taskbarPosition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Opening the companion flyout failed.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Hide()
    {
        try
        {
            HideCore();
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Hiding the companion flyout failed.");
        }
    }

    public void RepositionIfOpen()
    {
        try
        {
            if (_host?.IsOpen != true)
                return;

            _host.UpdatePlacement(GetAnchor(), _nativeAppService.GetTaskbarPosition());
            _host.Reposition();
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Repositioning the companion flyout failed.");
        }
    }

    public async Task ShutdownAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _enabled = false;
            _themeService.ElementThemeChanged -= ThemeServiceElementThemeChanged;
            DisposeSurface();
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, nameof(ShutdownAsync));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void EnsureSurface()
    {
        if (_host is not null)
            return;

        var actions = new CompanionActionHandler(_services, _navigation);
        _viewModel = new CompanionDashboardViewModel(_services, actions, _dispatcher, _sessionStartedAtUtc);
        _viewModel.NavigationCompleted += SurfaceHideRequested;
        _view = new CompanionFlyoutView(_viewModel);
        _host = new CompanionFlyoutHost(_view);
        _host.ApplyTheme(ResolveCompanionTheme(_themeService.RootTheme));
        _view.HideRequested += SurfaceHideRequested;
        _host.HideRequested += SurfaceHideRequested;
        _host.PlacementInvalidated += PlacementInvalidated;
        _host.FatalError += HostFatalError;
    }

    private RectInt32? GetAnchor()
    {
        var result = _getTrayIconRect();
        return result.Success ? result.Rect : null;
    }

    private void SurfaceHideRequested(object? sender, EventArgs args) => Hide();

    private void PlacementInvalidated(object? sender, EventArgs args) => RepositionIfOpen();

    private void ThemeServiceElementThemeChanged(object? sender, ApplicationElementTheme theme)
    {
        if (_disposed)
            return;

        if (_dispatcher.HasThreadAccess)
            ApplyTheme(theme);
        else
            _dispatcher.TryEnqueue(() => ApplyTheme(theme));
    }

    private void ApplyTheme(ApplicationElementTheme theme)
    {
        if (_disposed)
            return;

        try
        {
            _host?.ApplyTheme(ResolveCompanionTheme(theme));
        }
        catch (Exception ex)
        {
            DisableForSession(ex, "Applying the companion theme failed.");
        }
    }

    private ElementTheme ResolveCompanionTheme(ApplicationElementTheme theme)
        => theme == ApplicationElementTheme.Default
            ? _underlyingThemeService.IsUnderlyingThemeDark() ? ElementTheme.Dark : ElementTheme.Light
            : theme.ToWindowsElementTheme();

    private void HideCore()
    {
        _host?.Hide();
        _viewModel?.Close();
    }

    private void DisposeSurface()
    {
        if (_view is not null)
            _view.HideRequested -= SurfaceHideRequested;
        if (_host is not null)
        {
            _host.HideRequested -= SurfaceHideRequested;
            _host.PlacementInvalidated -= PlacementInvalidated;
            _host.FatalError -= HostFatalError;
        }

        if (_viewModel is not null)
            _viewModel.NavigationCompleted -= SurfaceHideRequested;
        _viewModel?.Close();
        _host?.Dispose();
        _viewModel?.Dispose();
        _host = null;
        _view = null;
        _viewModel = null;
    }

    private void DisableForSession(Exception exception, string message)
    {
        _logger.CaptureException(exception, message);
        if (!_sessionAvailable)
            return;

        _sessionAvailable = false;
        try
        {
            DisposeSurface();
        }
        catch (Exception disposeException)
        {
            _logger.CaptureException(disposeException, "Companion surface cleanup after guarded error");
        }

        try
        {
            SessionDisabled?.Invoke(this, Translator.Companion_DisabledMessage);
        }
        catch (Exception notificationException)
        {
            _logger.CaptureException(notificationException, "Companion disabled notification");
        }
    }

    private void HostFatalError(Exception exception)
    {
        if (!_dispatcher.TryEnqueue(() => DisableForSession(exception, "The companion native host failed.")))
        {
            _sessionAvailable = false;
            _logger.CaptureException(exception, "The companion native host failed after dispatcher shutdown");
        }
    }

    private void EnsureUIThread()
    {
        if (!_dispatcher.HasThreadAccess)
            throw new InvalidOperationException("The companion flyout must be controlled by the application UI thread.");
    }
}
