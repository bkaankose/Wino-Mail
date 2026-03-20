using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Serilog;

namespace Wino.Mail.WinUI.Services;

internal sealed class NativeTrayIcon : IDisposable
{
    private const int ImageIcon = 1;
    private const int LoadFromFile = 0x0010;
    private const int WmApp = 0x8000;
    private const int WmNull = 0x0000;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmCommand = 0x0111;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonDblClk = 0x0203;
    private const int TpmLeftAlign = 0x0000;
    private const int TpmBottomAlign = 0x0020;
    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;
    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfDisabled = 0x0002;
    private const int MfGray = 0x0001;
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifGuid = 0x00000020;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int TrayCallbackMessage = WmApp + 1;
    private const string WindowClassName = "WinoMail.NativeTrayIconWindow";
    private static readonly Guid TrayIconGuid = new("6E1330D0-22D5-4F0B-A3BF-C9B2AE536F77");
    private static readonly object ClassLock = new();
    private static readonly Dictionary<nint, NativeTrayIcon> Instances = [];
    private static bool _windowClassRegistered;
    private static ushort _windowClassAtom;
    private static uint _taskbarCreatedMessage;
    private static readonly WindowProcDelegate WindowProc = StaticWindowProc;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly string _iconPath;
    private readonly Func<IReadOnlyList<NativeTrayMenuItem>> _menuFactory;
    private readonly Func<Task> _primaryAction;
    private readonly string _toolTipText;

    private nint _messageWindowHandle;
    private nint _iconHandle;
    private bool _isCreated;
    private bool _isDisposed;

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
    }

    public void Create()
    {
        if (_isDisposed || _isCreated)
            return;

        EnsureWindowClassRegistered();

        _messageWindowHandle = CreateWindowExW(
            0,
            WindowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            GetModuleHandleW(null),
            nint.Zero);

        if (_messageWindowHandle == nint.Zero)
            throw new InvalidOperationException("Failed to create native tray icon message window.");

        lock (Instances)
        {
            Instances[_messageWindowHandle] = this;
        }

        _iconHandle = LoadImageW(nint.Zero, _iconPath, ImageIcon, 0, 0, LoadFromFile);
        if (_iconHandle == nint.Zero)
            throw new InvalidOperationException($"Failed to load tray icon from '{_iconPath}'.");

        AddOrUpdateIcon(NimAdd);
        _isCreated = true;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_messageWindowHandle != nint.Zero)
        {
            RemoveIcon();
            DestroyWindow(_messageWindowHandle);
            lock (Instances)
            {
                Instances.Remove(_messageWindowHandle);
            }

            _messageWindowHandle = nint.Zero;
        }

        if (_iconHandle != nint.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
        }

        _isCreated = false;
    }

    private void AddOrUpdateIcon(int message)
    {
        var notifyIconData = CreateNotifyIconData();
        Shell_NotifyIconW(message, ref notifyIconData);
    }

    private void RemoveIcon()
    {
        var notifyIconData = CreateNotifyIconData();
        Shell_NotifyIconW(NimDelete, ref notifyIconData);
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _messageWindowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifGuid,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = _toolTipText,
            guidItem = TrayIconGuid
        };
    }

    private void ShowContextMenu()
    {
        var menuHandle = CreatePopupMenu();
        if (menuHandle == nint.Zero)
            return;

        try
        {
            var menuItems = _menuFactory();
            var commandMap = new Dictionary<uint, Func<Task>>();
            uint commandId = 1;

            foreach (var menuItem in menuItems)
            {
                if (menuItem.IsSeparator)
                {
                    AppendMenuW(menuHandle, MfSeparator, 0, null);
                    continue;
                }

                uint flags = MfString;
                if (!menuItem.IsEnabled)
                    flags |= MfDisabled | MfGray;

                AppendMenuW(menuHandle, flags, commandId, menuItem.Text);

                if (menuItem.IsDefault)
                    SetMenuDefaultItem(menuHandle, commandId, false);

                if (menuItem.IsEnabled)
                    commandMap[commandId] = menuItem.Action;

                commandId++;
            }

            SetForegroundWindow(_messageWindowHandle);

            if (!GetCursorPos(out var point))
                return;

            var selectedCommandId = TrackPopupMenuEx(
                menuHandle,
                TpmLeftAlign | TpmBottomAlign | TpmRightButton | TpmReturnCmd,
                point.X,
                point.Y,
                _messageWindowHandle,
                nint.Zero);

            PostMessageW(_messageWindowHandle, WmNull, 0, 0);

            if (selectedCommandId != 0 && commandMap.TryGetValue((uint)selectedCommandId, out var action))
                InvokeAction(action);
        }
        finally
        {
            DestroyMenu(menuHandle);
        }
    }

    private void InvokePrimaryAction() => InvokeAction(_primaryAction);

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

    private nint HandleWindowMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            if (_isCreated && !_isDisposed)
                AddOrUpdateIcon(NimAdd);

            return nint.Zero;
        }

        if (message == TrayCallbackMessage)
        {
            switch ((int)lParam)
            {
                case WmRButtonUp:
                    ShowContextMenu();
                    return nint.Zero;
                case WmLButtonDblClk:
                    InvokePrimaryAction();
                    return nint.Zero;
            }
        }

        if (message == WmCommand || message == WmClose || message == WmDestroy)
            return nint.Zero;

        return DefWindowProcW(_messageWindowHandle, message, wParam, lParam);
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (ClassLock)
        {
            if (_windowClassRegistered)
                return;

            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

            var windowClass = new WNDCLASSW
            {
                lpfnWndProc = WindowProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = WindowClassName
            };

            _windowClassAtom = RegisterClassW(ref windowClass);
            if (_windowClassAtom == 0)
                throw new InvalidOperationException("Failed to register native tray icon window class.");

            _windowClassRegistered = true;
        }
    }

    private static nint StaticWindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        lock (Instances)
        {
            if (Instances.TryGetValue(windowHandle, out var instance))
                return instance.HandleWindowMessage(message, wParam, lParam);
        }

        return DefWindowProcW(windowHandle, message, wParam, lParam);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
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
    private static extern ushort RegisterClassW(ref WNDCLASSW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        int style,
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW notifyIconData);

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
        int flags,
        int x,
        int y,
        nint windowHandle,
        nint reserved);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);
}
