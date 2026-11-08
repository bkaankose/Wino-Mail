using Wino.Mail.Controls.Core.VoiceBubble;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.VoiceBubble;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;

namespace Wino.Mail.WinUI.Services.Voice;

/// <summary>
/// A topmost, transparent popup window next to the tray icon that hosts the voice bubble, placed the way
/// the companion flyout is placed. It also owns the global voice shortcut.
/// </summary>
internal sealed partial class VoiceBubbleHost : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExNoRedirectionBitmap = 0x00200000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint SwpNoActivate = 0x0010;
    private const uint WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    // Each shortcut alternates between two ids so a new gesture is registered before the old one is removed.
    private readonly HotKeySlot _voiceHotKey = new(0x574B, 0x574C);
    private readonly HotKeySlot _textHotKey = new(0x574D, 0x574E);
    private const uint MonitorDefaultToNearest = 2;
    private const int ErrorClassAlreadyExists = 1410;
    private const int MdtEffectiveDpi = 0;

    // The window follows the bubble's size; this reference size only fixes the corner it grows from.
    private const double ReferenceSize = 420;
    private const double InitialSize = 92;
    private const double PlacementGap = 8;
    private const double ScreenMargin = 12;

    private static readonly string WindowClassName = $"WinoMail.VoiceBubble.{Guid.NewGuid():N}";
    private static readonly ConcurrentDictionary<nint, VoiceBubbleHost> Hosts = new();
    private static readonly object RegistrationLock = new();
    private static bool _registered;

    private readonly Grid _root;
    private DesktopWindowXamlSource? _xamlSource;
    private nint _windowHandle;
    private readonly DispatcherQueueTimer _shrinkTimer;
    private HorizontalAlignment _horizontalAnchor = HorizontalAlignment.Right;
    private VerticalAlignment _verticalAnchor = VerticalAlignment.Bottom;
    private RectInt32 _referenceBounds;
    private double _scale = 1;
    private Size _targetSize = new(InitialSize, InitialSize);
    private (int Width, int Height) _windowSize;
    private bool _isPlaced;
    private bool _disposed;

    internal VoiceBubbleHost()
    {
        Bubble = new WinoVoiceBubble();
        _root = new Grid();
        _root.Children.Add(Bubble);
        Bubble.TargetSizeChanged += BubbleTargetSizeChanged;

        _shrinkTimer = _root.DispatcherQueue.CreateTimer();
        _shrinkTimer.IsRepeating = false;
        _shrinkTimer.Interval = WinoVoiceBubble.ResizeDuration + TimeSpan.FromMilliseconds(40);
        _shrinkTimer.Tick += ShrinkTimerTick;

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
            throw CreateWin32Exception("Could not create the voice bubble window.");

        Hosts[_windowHandle] = this;

        try
        {
            _xamlSource = new DesktopWindowXamlSource();
            _xamlSource.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle));
            _xamlSource.Content = _root;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal event EventHandler<VoiceBubbleInputMode>? HotKeyPressed;

    internal WinoVoiceBubble Bubble { get; }

    internal bool IsOpen { get; private set; }

    /// <summary>
    /// Registers <paramref name="gesture"/> as the shortcut that opens the bubble in <paramref name="mode"/>, or removes it
    /// when it is null. A failed registration keeps the previous shortcut, the same rule as the companion shortcut in
    /// <see cref="NativeTrayIcon"/>.
    /// </summary>
    internal bool TrySetHotKey(VoiceBubbleInputMode mode, HotKeyGesture? gesture)
    {
        if (_disposed)
            return false;

        var slot = mode == VoiceBubbleInputMode.Text ? _textHotKey : _voiceHotKey;
        var normalized = gesture?.Normalize();
        if (normalized == slot.Gesture)
            return true;

        if (normalized is null)
        {
            Unregister(slot);
            return true;
        }

        if (!IsRegistrable(normalized.Value, out var key))
            return false;

        var candidateId = slot.ActiveId == slot.PrimaryId ? slot.SecondaryId : slot.PrimaryId;
        if (!RegisterHotKey(_windowHandle, candidateId, ToNativeModifiers(normalized.Value.Modifiers) | ModNoRepeat, (uint)key))
            return false;

        if (slot.ActiveId != 0)
            UnregisterHotKey(_windowHandle, slot.ActiveId);

        slot.ActiveId = candidateId;
        slot.Gesture = normalized;
        return true;
    }

    private sealed class HotKeySlot(int primaryId, int secondaryId)
    {
        public int PrimaryId { get; } = primaryId;
        public int SecondaryId { get; } = secondaryId;
        public int ActiveId { get; set; }
        public HotKeyGesture? Gesture { get; set; }
    }

    internal static bool IsRegistrable(HotKeyGesture gesture, out Windows.System.VirtualKey key)
    {
        key = Windows.System.VirtualKey.None;
        return gesture.IsValid &&
               !gesture.Modifiers.HasFlag(ModifierKeys.Command) &&
               Enum.TryParse(gesture.Key, true, out key) &&
               key is not (Windows.System.VirtualKey.None or Windows.System.VirtualKey.F12);
    }

    private void Unregister(HotKeySlot slot)
    {
        if (slot.ActiveId != 0)
            UnregisterHotKey(_windowHandle, slot.ActiveId);

        slot.ActiveId = 0;
        slot.Gesture = null;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= ModWin;
        return result;
    }

    internal void ApplyTheme(ElementTheme theme)
    {
        if (!_disposed)
            _root.RequestedTheme = theme;
    }

    /// <summary>
    /// Opens the bubble. At <see cref="VoiceBubblePosition.BottomRight"/> it sits next to the tray icon like the
    /// companion flyout; the other positions use the work area of the monitor under the pointer.
    /// </summary>
    internal void Show(RectInt32? anchor, WindowsTaskbarPosition taskbarPosition, VoiceBubblePosition position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_xamlSource is null)
            return;

        var isTrayPlacement = position == VoiceBubblePosition.BottomRight;
        nint monitorHandle;
        if (!isTrayPlacement && GetCursorPos(out var cursor))
            monitorHandle = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        else if (anchor is { } icon)
            monitorHandle = MonitorFromPoint(new POINT { X = icon.X + (icon.Width / 2), Y = icon.Y + (icon.Height / 2) }, MonitorDefaultToNearest);
        else
            monitorHandle = MonitorFromWindow(FindWindowW("Shell_TrayWnd", null), MonitorDefaultToNearest);

        var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitorHandle, ref monitorInfo))
            throw CreateWin32Exception("Could not resolve the voice bubble monitor.");

        // The island reports a rasterization scale of 1 until it has been shown, so size from the monitor's DPI.
        _scale = GetDpiForMonitor(monitorHandle, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0
            ? dpiX / 96D
            : Math.Max(1D, _xamlSource.SiteBridge.SiteView.RasterizationScale);

        var work = monitorInfo.rcWork;
        if (isTrayPlacement)
        {
            var reference = (int)Math.Ceiling(ReferenceSize * _scale);
            var gap = Math.Max(1, (int)Math.Round(PlacementGap * _scale));
            var workArea = new CompanionRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            var iconRect = anchor is { } anchorRect
                ? new CompanionRect(anchorRect.X, anchorRect.Y, anchorRect.Width, anchorRect.Height)
                : (CompanionRect?)null;

            var bounds = CompanionFlyoutPlacementCalculator.Calculate(workArea, iconRect, taskbarPosition, reference, reference, gap);
            _referenceBounds = new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _horizontalAnchor = taskbarPosition == WindowsTaskbarPosition.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _verticalAnchor = taskbarPosition switch
            {
                WindowsTaskbarPosition.Top => VerticalAlignment.Top,
                WindowsTaskbarPosition.Left or WindowsTaskbarPosition.Right => VerticalAlignment.Center,
                _ => VerticalAlignment.Bottom
            };
        }
        else
        {
            // The bubble draws its own shadow margin, so a small margin keeps it visually off the screen edge.
            var margin = (int)Math.Round(ScreenMargin * _scale);
            _referenceBounds = new RectInt32(
                work.Left + margin,
                work.Top + margin,
                Math.Max(1, work.Right - work.Left - (2 * margin)),
                Math.Max(1, work.Bottom - work.Top - (2 * margin)));
            _horizontalAnchor = position switch
            {
                VoiceBubblePosition.TopLeft or VoiceBubblePosition.BottomLeft => HorizontalAlignment.Left,
                VoiceBubblePosition.Center => HorizontalAlignment.Center,
                _ => HorizontalAlignment.Right
            };
            _verticalAnchor = position switch
            {
                VoiceBubblePosition.TopLeft or VoiceBubblePosition.TopRight => VerticalAlignment.Top,
                VoiceBubblePosition.Center => VerticalAlignment.Center,
                _ => VerticalAlignment.Bottom
            };
        }

        Bubble.HorizontalAlignment = _horizontalAnchor;
        Bubble.VerticalAlignment = _verticalAnchor;
        _windowSize = default;
        _isPlaced = true;
        _shrinkTimer.Stop();
        ApplySize(_targetSize);

        if (!IsOpen)
        {
            ShowWindow(_windowHandle, SwShow);
            _xamlSource.SiteBridge.Show();
            IsOpen = true;
        }

        SetForegroundWindow(_windowHandle);
        _xamlSource.NavigateFocus(new XamlSourceFocusNavigationRequest(XamlSourceFocusNavigationReason.First));
    }

    internal void Hide()
    {
        if (_disposed || !IsOpen)
            return;

        IsOpen = false;
        _xamlSource?.SiteBridge.Hide();
        ShowWindow(_windowHandle, SwHide);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsOpen = false;
        Bubble.TargetSizeChanged -= BubbleTargetSizeChanged;
        _shrinkTimer.Stop();
        _shrinkTimer.Tick -= ShrinkTimerTick;

        try
        {
            Unregister(_voiceHotKey);
            Unregister(_textHotKey);

            if (_xamlSource is not null)
            {
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

    private void BubbleTargetSizeChanged(object? sender, Size size)
    {
        var growing = size.Width > _targetSize.Width || size.Height > _targetSize.Height;
        var shrinking = size.Width < _targetSize.Width || size.Height < _targetSize.Height;
        var previous = _targetSize;
        _targetSize = size;

        if (!_isPlaced)
            return;

        // Grow before the bubble animates outwards and shrink after it has finished, so it is never clipped.
        if (growing)
            ApplySize(new Size(Math.Max(size.Width, previous.Width), Math.Max(size.Height, previous.Height)));

        if (shrinking)
        {
            _shrinkTimer.Stop();
            _shrinkTimer.Start();
        }
    }

    private void ShrinkTimerTick(DispatcherQueueTimer sender, object args) => ApplySize(_targetSize);

    private void ApplySize(Size size)
    {
        if (_disposed || _xamlSource is null || !_isPlaced)
            return;

        var width = Math.Max(1, (int)Math.Ceiling(size.Width * _scale));
        var height = Math.Max(1, (int)Math.Ceiling(size.Height * _scale));
        if ((width, height) == _windowSize)
            return;

        var reference = _referenceBounds;
        var x = _horizontalAnchor switch
        {
            HorizontalAlignment.Left => reference.X,
            HorizontalAlignment.Center => reference.X + ((reference.Width - width) / 2),
            _ => reference.X + reference.Width - width
        };
        var y = _verticalAnchor switch
        {
            VerticalAlignment.Top => reference.Y,
            VerticalAlignment.Center => reference.Y + ((reference.Height - height) / 2),
            _ => reference.Y + reference.Height - height
        };

        _windowSize = (width, height);
        _xamlSource.SiteBridge.MoveAndResize(new RectInt32(0, 0, width, height));
        SetWindowPos(_windowHandle, new nint(-1), x, y, width, height, SwpNoActivate);
    }
    private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        var hotKeyId = unchecked((int)wParam);
        if (message == WmHotKey && hotKeyId != 0 && (hotKeyId == _voiceHotKey.ActiveId || hotKeyId == _textHotKey.ActiveId))
        {
            try
            {
                HotKeyPressed?.Invoke(this, hotKeyId == _textHotKey.ActiveId ? VoiceBubbleInputMode.Text : VoiceBubbleInputMode.Voice);
            }
            catch
            {
            }

            return nint.Zero;
        }

        return DefWindowProcW(windowHandle, message, wParam, lParam);
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
                    throw CreateWin32Exception("Could not register the voice bubble window class.");
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
    private struct POINT
    {
        public int X;
        public int Y;
    }

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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint windowHandle, int id);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint windowHandle, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO monitorInfo);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);
}
