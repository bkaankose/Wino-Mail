// Portions adapted from Files Community's SystemTrayIcon implementation.
// Copyright (c) Files Community. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Serilog;

namespace Wino.Mail.WinUI.Services;

internal sealed class NativeTrayIcon : IDisposable
{
    private const uint TrayCallbackMessage = 2048u;
    private const uint MenuCommandOpen = 1u;
    private const int ImageIcon = 1;
    private const int LoadFromFile = 0x0010;
    private const uint NotifyIconVersion4 = 4;
    private const uint CsDblClks = 0x0008;
    private const uint WmDestroy = 0x0002;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const int SmMenuDropAlignment = 40;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmLeftButton = 0x0000;
    private const uint TpmRightAlign = 0x0008;
    private const uint MfByCommand = 0x0000;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfDisabled = 0x0002;
    private const uint MfGray = 0x0001;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifGuid = 0x00000020;
    private const uint NifShowTip = 0x00000080;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const int ErrorClassAlreadyExists = 1410;
    private const string WindowClassName = "WinoMail.NativeTrayIconWindow";
    private static readonly Guid TrayIconGuid = new("6E1330D0-22D5-4F0B-A3BF-C9B2AE536F77");

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string _iconPath;
    private readonly Func<IReadOnlyList<NativeTrayMenuItem>> _menuFactory;
    private readonly Func<Task> _primaryAction;
    private readonly uint _taskbarRestartMessageId;
    private readonly NativeTrayIconWindow _iconWindow;

    private nint _iconHandle;
    private string _toolTipText;
    private bool _isDisposed;
    private bool _isVisible;
    private bool _notifyIconCreated;
    private DateTime _lastPrimaryActionDate;

    public NativeTrayIcon(
        DispatcherQueue dispatcherQueue,
        string iconPath,
        string toolTipText,
        Func<IReadOnlyList<NativeTrayMenuItem>> menuFactory,
        Func<Task> primaryAction)
    {
        _dispatcherQueue = dispatcherQueue;
        _iconPath = iconPath;
        _toolTipText = toolTipText;
        _menuFactory = menuFactory;
        _primaryAction = primaryAction;
        _taskbarRestartMessageId = RegisterWindowMessageW("TaskbarCreated");
        _iconWindow = new NativeTrayIconWindow(this);
    }

    public Guid Id { get; } = TrayIconGuid;

    public string ToolTipText
    {
        get => _toolTipText;
        set
        {
            if (_toolTipText == value)
                return;

            _toolTipText = value;
            CreateOrModifyNotifyIcon();
        }
    }

    public void Create() => Show();

    public NativeTrayIcon Show()
    {
        if (_isDisposed)
            return this;

        IsVisible = true;
        return this;
    }

    public NativeTrayIcon Hide()
    {
        IsVisible = false;
        return this;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Hide();
        _iconWindow.Dispose();
        DestroyIconHandle();
    }

    private bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;

            _isVisible = value;

            if (value)
                CreateOrModifyNotifyIcon();
            else
                DeleteNotifyIcon();
        }
    }

    private nint IconHandle
    {
        get
        {
            if (_iconHandle != nint.Zero)
                return _iconHandle;

            _iconHandle = LoadImageW(nint.Zero, _iconPath, ImageIcon, 0, 0, LoadFromFile);
            if (_iconHandle == nint.Zero)
                throw CreateWin32Exception($"Failed to load tray icon from '{_iconPath}'.");

            return _iconHandle;
        }
    }

    private void CreateOrModifyNotifyIcon()
    {
        if (!IsVisible || _isDisposed)
            return;

        var notifyIconData = CreateNotifyIconData();

        if (!_notifyIconCreated)
        {
            Shell_NotifyIconW(NimDelete, ref notifyIconData);

            if (!Shell_NotifyIconW(NimAdd, ref notifyIconData))
                throw CreateWin32Exception("Failed to add native tray icon.");

            _notifyIconCreated = true;
            notifyIconData.uTimeoutOrVersion = NotifyIconVersion4;

            if (!Shell_NotifyIconW(NimSetVersion, ref notifyIconData))
            {
                Log.Warning("Shell_NotifyIcon version setup failed. LastError: {LastError}, Hwnd: {Hwnd}",
                    Marshal.GetLastWin32Error(),
                    _iconWindow.WindowHandle);
            }

            Log.Information("Native tray icon created. Hwnd: {Hwnd}, IconHandle: {IconHandle}",
                _iconWindow.WindowHandle,
                _iconHandle);

            return;
        }

        if (!Shell_NotifyIconW(NimModify, ref notifyIconData))
            throw CreateWin32Exception("Failed to modify native tray icon.");
    }

    private void DeleteNotifyIcon()
    {
        if (!_notifyIconCreated)
            return;

        _notifyIconCreated = false;
        var notifyIconData = CreateNotifyIconData();

        if (!Shell_NotifyIconW(NimDelete, ref notifyIconData))
        {
            Log.Warning("Failed to delete native tray icon. LastError: {LastError}, Hwnd: {Hwnd}",
                Marshal.GetLastWin32Error(),
                _iconWindow.WindowHandle);
        }
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _iconWindow.WindowHandle,
            uID = MenuCommandOpen,
            uFlags = NifMessage | NifIcon | NifTip | NifGuid | NifShowTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = IconHandle,
            szTip = _toolTipText ?? string.Empty,
            guidItem = Id
        };
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var point))
            return;

        var menuHandle = CreatePopupMenu();
        if (menuHandle == nint.Zero)
            return;

        try
        {
            var menuItems = _menuFactory();
            var commandMap = new Dictionary<uint, Func<Task>>();
            var commandId = MenuCommandOpen;

            foreach (var menuItem in menuItems)
            {
                if (menuItem.IsSeparator)
                {
                    AppendMenuW(menuHandle, MfSeparator, 0, null);
                    continue;
                }

                var flags = MfString | MfByCommand;
                if (!menuItem.IsEnabled)
                    flags |= MfDisabled | MfGray;

                AppendMenuW(menuHandle, flags, commandId, menuItem.Text);

                if (menuItem.IsDefault)
                    SetMenuDefaultItem(menuHandle, commandId, false);

                if (menuItem.IsEnabled)
                    commandMap[commandId] = menuItem.Action;

                commandId++;
            }

            SetForegroundWindow(_iconWindow.WindowHandle);

            var menuFlags = TpmReturnCmd |
                            (GetSystemMetricsForDpi(SmMenuDropAlignment, GetDpiForWindow(_iconWindow.WindowHandle)) != 0
                                ? TpmRightAlign
                                : TpmLeftButton);

            var selectedCommandId = TrackPopupMenuEx(
                menuHandle,
                menuFlags,
                point.X,
                point.Y,
                _iconWindow.WindowHandle,
                nint.Zero);

            if (selectedCommandId != 0 && commandMap.TryGetValue((uint)selectedCommandId, out var action))
                InvokeAction(action);
        }
        finally
        {
            DestroyMenu(menuHandle);
        }
    }

    private void OnLeftClicked()
    {
        if (DateTime.Now - _lastPrimaryActionDate < TimeSpan.FromSeconds(1))
            return;

        _lastPrimaryActionDate = DateTime.Now;
        InvokeAction(_primaryAction);
    }

    private void InvokeAction(Func<Task> action)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Native tray icon action failed.");
            }
        });
    }

    private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case TrayCallbackMessage:
                switch (GetLowWord(lParam))
                {
                    case WmLButtonUp:
                        SetForegroundWindow(windowHandle);
                        OnLeftClicked();
                        break;
                    case WmRButtonUp:
                    case WmContextMenu:
                        ShowContextMenu();
                        break;
                }

                break;
            case WmDestroy:
                DeleteNotifyIcon();
                break;
            default:
                if (message == _taskbarRestartMessageId)
                {
                    DeleteNotifyIcon();
                    CreateOrModifyNotifyIcon();
                    break;
                }

                return DefWindowProcW(windowHandle, message, wParam, lParam);
        }

        return nint.Zero;
    }

    private void DestroyIconHandle()
    {
        if (_iconHandle == nint.Zero)
            return;

        DestroyIcon(_iconHandle);
        _iconHandle = nint.Zero;
    }

    private static uint GetLowWord(nint value) => (uint)((long)value & 0xFFFF);

    private static InvalidOperationException CreateWin32Exception(string message)
    {
        var lastError = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{message} LastError: {lastError}");
    }

    internal sealed record NativeTrayMenuItem(
        string Text,
        Func<Task> Action,
        bool IsSeparator = false,
        bool IsDefault = false,
        bool IsEnabled = true)
    {
        public static NativeTrayMenuItem Separator() => new(string.Empty, () => Task.CompletedTask, IsSeparator: true);
    }

    private sealed class NativeTrayIconWindow : IDisposable
    {
        private static readonly object ClassLock = new();
        private static readonly Dictionary<nint, NativeTrayIconWindow> Windows = [];
        private static readonly WindowProcDelegate SharedWindowProcedure = StaticWindowProc;
        private static bool _classRegistered;

        private readonly NativeTrayIcon _trayIcon;
        private readonly nint _instanceHandle;

        internal NativeTrayIconWindow(NativeTrayIcon trayIcon)
        {
            _trayIcon = trayIcon;
            _instanceHandle = GetModuleHandleW(null);
            EnsureWindowClassRegistered(_instanceHandle);

            WindowHandle = CreateWindowExW(
                0,
                WindowClassName,
                string.Empty,
                0,
                0,
                0,
                1,
                1,
                nint.Zero,
                nint.Zero,
                _instanceHandle,
                nint.Zero);

            if (WindowHandle == nint.Zero)
                throw CreateWin32Exception("Failed to create native tray icon message window.");

            lock (Windows)
            {
                Windows[WindowHandle] = this;
            }
        }

        internal nint WindowHandle { get; private set; }

        public void Dispose()
        {
            if (WindowHandle == nint.Zero)
                return;

            DestroyWindow(WindowHandle);
            lock (Windows)
            {
                Windows.Remove(WindowHandle);
            }

            WindowHandle = nint.Zero;
        }

        private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
            => _trayIcon.WindowProc(windowHandle, message, wParam, lParam);

        private static void EnsureWindowClassRegistered(nint instanceHandle)
        {
            lock (ClassLock)
            {
                if (_classRegistered)
                    return;

                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    style = CsDblClks,
                    lpfnWndProc = SharedWindowProcedure,
                    hInstance = instanceHandle,
                    lpszClassName = WindowClassName
                };

                if (RegisterClassExW(ref windowClass) == 0)
                {
                    var lastError = Marshal.GetLastWin32Error();
                    if (lastError != ErrorClassAlreadyExists)
                        throw new InvalidOperationException($"Failed to register native tray icon window class. LastError: {lastError}");
                }

                _classRegistered = true;
            }
        }

        private static nint StaticWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
        {
            lock (Windows)
            {
                if (Windows.TryGetValue(windowHandle, out var window))
                    return window.WindowProc(windowHandle, message, wParam, lParam);
            }

            return DefWindowProcW(windowHandle, message, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WindowProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcDelegate(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parentHandle,
        nint menuHandle,
        nint instanceHandle,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW notifyIconData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
        nint instanceHandle,
        string name,
        int imageType,
        int desiredWidth,
        int desiredHeight,
        int loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(nint menuHandle, uint flags, uint itemId, string? newItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint menuHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetMenuDefaultItem(nint menuHandle, uint itemId, bool byPosition);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        nint menuHandle,
        uint flags,
        int x,
        int y,
        nint windowHandle,
        nint reserved);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);
}
