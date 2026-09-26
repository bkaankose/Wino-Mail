// Adapted from DesktopFlyouts.WinUI 1.4.0 (MIT), commit
// bf5f4cf1e6bb23aff4bf4893f0de05635153eb3a.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

internal sealed partial class CompanionFlyoutHost : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint WmActivate = 0x0006;
    private const uint WmActivateApp = 0x001C;
    private const uint WmClose = 0x0010;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDpiChanged = 0x02E0;
    private const int WaInactive = 0;
    private const uint GaRootOwner = 3;
    private const uint MonitorDefaultToNearest = 2;
    private const int ErrorClassAlreadyExists = 1410;
    private const double FlyoutWidth = 400;
    private const double MaximumFlyoutHeight = 720;
    private const double PlacementGap = 8;

    private static readonly string WindowClassName = $"WinoMail.CompanionFlyout.{Guid.NewGuid():N}";
    private static readonly ConcurrentDictionary<nint, CompanionFlyoutHost> Hosts = new();
    private static readonly object RegistrationLock = new();
    private static bool _registered;

    private readonly FrameworkElement _content;
    private readonly DispatcherQueue _dispatcher;
    private DesktopWindowXamlSource? _xamlSource;
    private readonly CompanionFlyoutSurface _surface;
    private Storyboard? _transitionStoryboard;
    private nint _xamlWindowHandle;
    private DispatcherQueueTimer? _focusMonitorTimer;
    private nint _windowHandle;
    private RectInt32? _lastAnchor;
    private WindowsTaskbarPosition _lastTaskbarPosition;
    private CompanionRect _openBounds;
    private bool _disposed;
    private bool _isOpen;
    private bool _deactivationCheckPending;
    private bool _transitionIsOpening;
    private long _suppressDeactivationUntil;

    internal unsafe CompanionFlyoutHost(FrameworkElement content)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _dispatcher = content.DispatcherQueue;
        _surface = new CompanionFlyoutSurface(content);

        EnsureWindowClass();
        _windowHandle = CreateWindowExW(
            WsExToolWindow | WsExNoRedirectionBitmap | WsExTopmost,
            WindowClassName,
            string.Empty,
            WsPopup,
            0,
            0,
            1,
            1,
            nint.Zero,
            nint.Zero,
            GetModuleHandleW(null),
            nint.Zero);

        if (_windowHandle == nint.Zero)
            throw CreateWin32Exception("Could not create the companion flyout window.");

        Hosts[_windowHandle] = this;

        try
        {
            _xamlSource = new DesktopWindowXamlSource();
            _xamlSource.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
            _xamlSource.ShouldConstrainPopupsToWorkArea = true;
            _xamlSource.Content = _surface;
            _xamlWindowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(_xamlSource.SiteBridge.WindowId);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal event EventHandler? HideRequested;

    internal event EventHandler? PlacementInvalidated;

    internal event Action<Exception>? FatalError;

    internal bool IsOpen => _isOpen;

    internal void ApplyTheme(ElementTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Application themes replace a merged resource dictionary without necessarily
        // changing the light/dark theme. Re-enter the requested theme so ThemeResource
        // references in this XAML island resolve against the new dictionary in place.
        if (_surface.RequestedTheme == theme)
        {
            var refreshTheme = theme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;

            _surface.RequestedTheme = refreshTheme;
            _content.RequestedTheme = refreshTheme;
        }

        _surface.RequestedTheme = theme;
        _content.RequestedTheme = theme;
        _surface.ApplyBackdropTheme(theme);
    }

    internal async Task ShowAsync(RectInt32? anchor, WindowsTaskbarPosition taskbarPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdatePlacement(anchor, taskbarPosition);

        Reposition();
        // Upstream yields between layout, the closed transform, and showing the island.
        // This lets XAML commit the offscreen starting frame before the storyboard begins.
        await Task.Delay(1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetClosedTransform();
        _surface.UpdateLayout();
        await Task.Delay(1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShowWindow(_windowHandle, SwShow);
        _xamlSource?.SiteBridge.Show();
        _isOpen = true;
        SetForegroundWindow(_windowHandle);
        _xamlSource?.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.First));
        StartFocusMonitor();
        BeginTransition(isOpening: true);
    }

    internal void UpdatePlacement(RectInt32? anchor, WindowsTaskbarPosition taskbarPosition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastAnchor = anchor;
        _lastTaskbarPosition = taskbarPosition;
    }

    internal void SuppressDeactivationForTrayInteraction()
    {
        if (_disposed)
            return;

        _suppressDeactivationUntil = Environment.TickCount64 + GetDoubleClickTime() + 100L;
    }

    internal void Reposition()
    {
        if (_disposed || _windowHandle == nint.Zero || _xamlSource is null)
            return;

        var scale = Math.Max(1D, _xamlSource.SiteBridge.SiteView.RasterizationScale);
        _surface.Measure(new Size(FlyoutWidth + 26, MaximumFlyoutHeight + 26));
        var desiredHeight = Math.Clamp(_surface.DesiredSize.Height, 1D, MaximumFlyoutHeight + 26);
        var width = Math.Max(1, (int)Math.Ceiling((FlyoutWidth + 26) * scale));
        var height = Math.Max(1, (int)Math.Ceiling(desiredHeight * scale));
        var gap = Math.Max(1, (int)Math.Round(PlacementGap * scale));

        var anchorRect = _lastAnchor ?? new RectInt32(0, 0, 1, 1);
        var monitorRect = new RECT
        {
            Left = anchorRect.X,
            Top = anchorRect.Y,
            Right = anchorRect.X + anchorRect.Width,
            Bottom = anchorRect.Y + anchorRect.Height
        };
        var monitor = _lastAnchor is not null
            ? MonitorFromRect(ref monitorRect, MonitorDefaultToNearest)
            : MonitorFromWindow(FindWindowW("Shell_TrayWnd", null), MonitorDefaultToNearest);
        var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref monitorInfo))
            throw CreateWin32Exception("Could not resolve the companion flyout monitor.");

        var workArea = new CompanionRect(
            monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Top,
            monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top);
        var iconRect = _lastAnchor is { } icon
            ? new CompanionRect(icon.X, icon.Y, icon.Width, icon.Height)
            : (CompanionRect?)null;
        var bounds = CompanionFlyoutPlacementCalculator.Calculate(
            workArea,
            iconRect,
            _lastTaskbarPosition,
            width,
            height,
            gap);
        _openBounds = bounds;

        _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, width, height));
        StopTransition();
        PositionWindow(_openBounds, preserveZOrder: false);
        _surface.AnimationTransform.TranslateX = 0;
        _surface.AnimationTransform.TranslateY = 0;
        SetRectRegion(_windowHandle, width, height);
        SetRectRegion(_xamlWindowHandle, width, height);
        _surface.UpdateLayout();
    }

    internal void Hide()
    {
        if (_disposed || !_isOpen)
            return;

        _isOpen = false;
        StopFocusMonitor();
        BeginTransition(isOpening: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _isOpen = false;
        StopFocusMonitor();
        StopTransition();

        try
        {
            if (_xamlSource is not null)
            {
                _surface.Detach();
                _xamlSource.Content = null;
                _xamlSource.Dispose();
                _xamlSource = null;
            }
        }
        finally
        {
            if (_windowHandle != nint.Zero)
            {
                Hosts.TryRemove(_windowHandle, out _);
                DestroyWindow(_windowHandle);
                _windowHandle = nint.Zero;
            }
        }
    }

    private void ScheduleDeactivationCheck()
    {
        if (_deactivationCheckPending || _disposed)
            return;

        _deactivationCheckPending = true;
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(50);
                _deactivationCheckPending = false;

                CheckForLostFocus();
            }
            catch (Exception ex)
            {
                _deactivationCheckPending = false;
                RaiseFatalError(ex);
            }
        });
    }

    private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmActivate when ((int)wParam & 0xFFFF) == WaInactive:
                case WmActivateApp when wParam == 0:
                    ScheduleDeactivationCheck();
                    return nint.Zero;
                case WmClose:
                    RaiseHideRequested();
                    return nint.Zero;
                case WmDisplayChange:
                case WmSettingChange:
                case WmDpiChanged:
                    RaisePlacementInvalidated();
                    return nint.Zero;
            }
        }
        catch (Exception ex)
        {
            RaiseFatalError(ex);
            return nint.Zero;
        }

        return DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    private void RaiseHideRequested()
    {
        try
        {
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void RaisePlacementInvalidated()
    {
        try
        {
            PlacementInvalidated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            RaiseHideRequested();
        }
    }

    private void RaiseFatalError(Exception exception)
    {
        try
        {
            FatalError?.Invoke(exception);
        }
        catch
        {
            RaiseHideRequested();
        }
    }

    private void BeginTransition(bool isOpening)
    {
        // The upstream storyboard moves the complete island, including SystemBackdropElement.
        // The transparent host HWND stays at its final placement for the whole transition.
        StopTransition();
        _transitionIsOpening = isOpening;
        var transform = _surface.AnimationTransform;
        var vertical = _lastTaskbarPosition is WindowsTaskbarPosition.Top or WindowsTaskbarPosition.Bottom;
        var distance = vertical ? _surface.DesiredSize.Height : _surface.DesiredSize.Width;
        var closed = _lastTaskbarPosition is WindowsTaskbarPosition.Top or WindowsTaskbarPosition.Left
            ? -distance : distance;
        if (vertical)
        {
            _transitionStoryboard = isOpening
                ? TransitionHelpers.GetWindows11BottomToTopTransitionStoryboard(transform, closed, 0)
                : TransitionHelpers.GetWindows11TopToBottomTransitionStoryboard(transform, transform.TranslateY, closed);
        }
        else
        {
            _transitionStoryboard = isOpening
                ? TransitionHelpers.GetWindows11RightToLeftTransitionStoryboard(transform, closed, 0)
                : TransitionHelpers.GetWindows11LeftToRightTransitionStoryboard(transform, transform.TranslateX, closed);
        }

        _transitionStoryboard.Completed += TransitionCompleted;
        _transitionStoryboard.Begin();
    }

    private void TransitionCompleted(object? sender, object args)
    {
        try
        {
            if (!ReferenceEquals(sender, _transitionStoryboard) || _disposed)
                return;

            var hide = !_transitionIsOpening;
            StopTransition();
            _surface.AnimationTransform.TranslateX = 0;
            _surface.AnimationTransform.TranslateY = 0;
            if (hide)
            {
                _xamlSource?.SiteBridge.Hide();
                ShowWindow(_windowHandle, SwHide);
            }
        }
        catch (Exception ex)
        {
            RaiseFatalError(ex);
        }
    }

    private void StopTransition()
    {
        if (_transitionStoryboard is null)
            return;

        // Preserve the sampled position when reversing an unfinished transition.
        var x = _surface.AnimationTransform.TranslateX;
        var y = _surface.AnimationTransform.TranslateY;
        _transitionStoryboard.Completed -= TransitionCompleted;
        _transitionStoryboard.Stop();
        _transitionStoryboard = null;
        _surface.AnimationTransform.TranslateX = x;
        _surface.AnimationTransform.TranslateY = y;
    }

    private void SetClosedTransform()
    {
        _surface.AnimationTransform.TranslateX = _lastTaskbarPosition switch
        {
            WindowsTaskbarPosition.Left => -_surface.DesiredSize.Width,
            WindowsTaskbarPosition.Right => _surface.DesiredSize.Width,
            _ => 0
        };
        _surface.AnimationTransform.TranslateY = _lastTaskbarPosition switch
        {
            WindowsTaskbarPosition.Top => -_surface.DesiredSize.Height,
            WindowsTaskbarPosition.Bottom => _surface.DesiredSize.Height,
            _ => 0
        };
    }

    private static void SetRectRegion(nint hwnd, int width, int height)
    {
        var region = CreateRectRgn(0, 0, width, height);
        if (region == nint.Zero)
            throw CreateWin32Exception("Could not create the companion host region.");

        if (SetWindowRgn(hwnd, region, false) == 0)
        {
            DeleteObject(region);
            throw CreateWin32Exception("Could not set the companion host region.");
        }
        // Windows owns the region after a successful SetWindowRgn.
    }

    private void StartFocusMonitor()
    {
        StopFocusMonitor();
        _focusMonitorTimer = _dispatcher.CreateTimer();
        _focusMonitorTimer.Interval = TimeSpan.FromMilliseconds(100);
        _focusMonitorTimer.IsRepeating = true;
        _focusMonitorTimer.Tick += FocusMonitorTimerTick;
        _focusMonitorTimer.Start();
    }

    private void StopFocusMonitor()
    {
        if (_focusMonitorTimer is null)
            return;

        _focusMonitorTimer.Tick -= FocusMonitorTimerTick;
        _focusMonitorTimer.Stop();
        _focusMonitorTimer = null;
    }

    private void FocusMonitorTimerTick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            CheckForLostFocus();
        }
        catch (Exception ex)
        {
            RaiseFatalError(ex);
        }
    }

    private void CheckForLostFocus()
    {
        if (!_isOpen || Environment.TickCount64 <= _suppressDeactivationUntil)
            return;

        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
            return;

        _ = GetWindowThreadProcessId(foreground, out var processId);
        var belongsToCompanion = foreground == _windowHandle || foreground == _xamlWindowHandle ||
                                 IsChild(_windowHandle, foreground) ||
                                 GetAncestor(foreground, GaRootOwner) == _windowHandle;
        if (processId != (uint)Environment.ProcessId || !belongsToCompanion)
            RaiseHideRequested();
    }

    private void PositionWindow(CompanionRect bounds, bool preserveZOrder)
    {
        var flags = SwpNoActivate | (preserveZOrder ? SwpNoZOrder : 0);
        if (!SetWindowPos(
                _windowHandle,
                new nint(-1),
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags))
        {
            throw CreateWin32Exception("Could not position the companion flyout.");
        }

    }

    private static unsafe void EnsureWindowClass()
    {
        lock (RegistrationLock)
        {
            if (_registered)
                return;

            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWindowProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = Marshal.StringToHGlobalUni(WindowClassName)
            };

            try
            {
                if (RegisterClassExW(ref windowClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                    throw CreateWin32Exception("Could not register the companion flyout window class.");
            }
            finally
            {
                Marshal.FreeHGlobal(windowClass.lpszClassName);
            }

            _registered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StaticWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return Hosts.TryGetValue(windowHandle, out var host)
                ? host.WindowProc(windowHandle, message, wParam, lParam)
                : DefWindowProcW(windowHandle, message, wParam, lParam);
        }
        catch
        {
            return DefWindowProcW(windowHandle, message, wParam, lParam);
        }
    }

    private static InvalidOperationException CreateWin32Exception(string message)
        => new($"{message} LastError: {Marshal.GetLastWin32Error()}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint windowHandle, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint windowHandle, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint windowHandle, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(nint parentWindowHandle, nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref RECT rect, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint windowHandle, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO monitorInfo);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

}
