using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Serilog;
using WinUIEx;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Services;

public partial class WinoWindowManager : IWinoWindowManager
{
    public event EventHandler<WindowEx?>? ActiveWindowChanged;
    public event EventHandler<WindowEx>? WindowRemoved;

    private const uint ProcessPowerThrottlingInformation = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint NormalPriorityClass = 0x20;
    private const uint IdlePriorityClass = 0x40;

    private readonly object _syncLock = new();
    private readonly Dictionary<(WinoWindowKind Kind, string Name), WindowEx> _windows = [];
    private readonly Dictionary<WindowEx, (WinoWindowKind Kind, string Name)> _windowKeys = [];
    private readonly HashSet<WindowEx> _visibleWindows = [];
    private int _backgroundModeEpoch;
    private bool _isBackgroundResourceSavingEnabled;

    public WindowEx? ActiveWindow { get; private set; }

    public WindowEx CreateWindow(WinoWindowKind kind, Func<WindowEx> factory, string? name = null)
    {
        LeaveBackgroundResourceSavingMode();

        var key = CreateKey(kind, name);

        lock (_syncLock)
        {
            if (_windows.TryGetValue(key, out var existingWindow))
            {
                ActiveWindow = existingWindow;
                ActiveWindowChanged?.Invoke(this, existingWindow);
                return existingWindow;
            }
        }

        var newWindow = factory();

        lock (_syncLock)
        {
            if (_windows.TryGetValue(key, out var existingWindow))
            {
                ActiveWindow = existingWindow;
                ActiveWindowChanged?.Invoke(this, existingWindow);
                return existingWindow;
            }

            TrackWindow(key, newWindow);
            ActiveWindow = newWindow;
            ActiveWindowChanged?.Invoke(this, newWindow);
            return newWindow;
        }
    }

    public WindowEx? GetWindow(WinoWindowKind kind, string? name = null)
    {
        lock (_syncLock)
        {
            _windows.TryGetValue(CreateKey(kind, name), out var window);
            return window;
        }
    }

    public WindowEx? GetWindow(string name)
    {
        var normalizedName = NormalizeName(name);

        lock (_syncLock)
        {
            return _windows
                .Where(x => x.Key.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .FirstOrDefault();
        }
    }

    public void ActivateWindow(WindowEx window)
    {
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            window.DispatcherQueue.TryEnqueue(() => ActivateWindow(window));
            return;
        }

        LeaveBackgroundResourceSavingMode();

        window.Show();
        window.BringToFront();
        window.Activate();

        lock (_syncLock)
        {
            _visibleWindows.Add(window);
            ActiveWindow = window;
        }

        ActiveWindowChanged?.Invoke(this, window);
    }

    public bool ActivateWindow(WinoWindowKind kind, string? name = null)
    {
        var window = GetWindow(kind, name);
        if (window == null)
            return false;

        ActivateWindow(window);
        return true;
    }

    public void HideWindow(WindowEx window)
    {
        if (!window.DispatcherQueue.HasThreadAccess)
        {
            window.DispatcherQueue.TryEnqueue(() => HideWindow(window));
            return;
        }

        window.Hide();
        var hasVisibleWindows = false;

        lock (_syncLock)
        {
            _visibleWindows.Remove(window);

            if (ReferenceEquals(ActiveWindow, window))
            {
                ActiveWindow = null;
                ActiveWindowChanged?.Invoke(this, null);
            }

            hasVisibleWindows = HasVisibleWindowsLocked();
        }

        if (!hasVisibleWindows)
        {
            EnterBackgroundResourceSavingMode();
        }
    }

    public bool HideWindow(WinoWindowKind kind, string? name = null)
    {
        var window = GetWindow(kind, name);
        if (window == null)
            return false;

        HideWindow(window);
        return true;
    }

    private void TrackWindow((WinoWindowKind Kind, string Name) key, WindowEx window)
    {
        _windows[key] = window;
        _windowKeys[window] = key;
        window.Activated += WindowActivated;
        window.Closed += WindowClosed;
    }

    private void WindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (sender is not WindowEx window)
            return;

        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;

        LeaveBackgroundResourceSavingMode();

        lock (_syncLock)
        {
            if (_windowKeys.ContainsKey(window))
            {
                _visibleWindows.Add(window);
                ActiveWindow = window;
                ActiveWindowChanged?.Invoke(this, window);
            }
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not WindowEx window)
            return;

        var shouldEnterBackgroundResourceSavingMode = false;

        lock (_syncLock)
        {
            if (!_windowKeys.TryGetValue(window, out var key))
                return;

            window.Activated -= WindowActivated;
            window.Closed -= WindowClosed;

            var wasActiveWindow = ReferenceEquals(ActiveWindow, window);

            if (wasActiveWindow)
            {
                ActiveWindow = null;
            }

            _windowKeys.Remove(window);
            _windows.Remove(key);
            _visibleWindows.Remove(window);
            WindowRemoved?.Invoke(this, window);

            if (wasActiveWindow)
            {
                ActiveWindowChanged?.Invoke(this, null);
            }

            shouldEnterBackgroundResourceSavingMode = !HasVisibleWindowsLocked();
        }

        if (shouldEnterBackgroundResourceSavingMode)
        {
            EnterBackgroundResourceSavingMode();
        }
    }

    public void CloseAllWindows()
    {
        List<WindowEx> windows;
        lock (_syncLock)
        {
            windows = _windows.Values.Distinct().ToList();
        }

        foreach (var window in windows)
        {
            try
            {
                if (window.DispatcherQueue.HasThreadAccess)
                {
                    window.Activated -= WindowActivated;
                    window.Closed -= WindowClosed;
                    window.Close();
                }
                else
                {
                    window.DispatcherQueue.TryEnqueue(() =>
                    {
                        window.Activated -= WindowActivated;
                        window.Closed -= WindowClosed;
                        window.Close();
                    });
                }
            }
            catch
            {
                // Best effort shutdown for all tracked windows.
            }
        }

        lock (_syncLock)
        {
            _windowKeys.Clear();
            _windows.Clear();
            _visibleWindows.Clear();
            ActiveWindow = null;
        }

        LeaveBackgroundResourceSavingMode();
        ActiveWindowChanged?.Invoke(this, null);
    }

    private bool HasVisibleWindowsLocked() => _visibleWindows.Any(window => _windowKeys.ContainsKey(window));

    private void EnterBackgroundResourceSavingMode()
    {
        if (IsApplicationExiting())
        {
            LeaveBackgroundResourceSavingMode();
            return;
        }

        var epoch = 0;

        lock (_syncLock)
        {
            if (HasVisibleWindowsLocked())
                return;

            _isBackgroundResourceSavingEnabled = true;
            epoch = ++_backgroundModeEpoch;
        }

        SetEfficiencyMode(true);
        ScheduleWorkingSetTrim(epoch);
    }

    private void LeaveBackgroundResourceSavingMode()
    {
        var shouldDisable = false;

        lock (_syncLock)
        {
            _backgroundModeEpoch++;
            shouldDisable = _isBackgroundResourceSavingEnabled;
            _isBackgroundResourceSavingEnabled = false;
        }

        if (shouldDisable)
        {
            SetEfficiencyMode(false);
        }
    }

    private void ScheduleWorkingSetTrim(int epoch)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            lock (_syncLock)
            {
                if (epoch != _backgroundModeEpoch ||
                    !_isBackgroundResourceSavingEnabled ||
                    HasVisibleWindowsLocked() ||
                    IsApplicationExiting())
                {
                    return;
                }
            }

            TrimWorkingSet();
        });
    }

    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (!SetProcessWorkingSetSizeEx(GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue, 0))
            {
                Log.Debug("SetProcessWorkingSetSizeEx failed while trimming Wino working set. ErrorCode: {ErrorCode}", Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to trim Wino working set after entering background mode.");
        }
    }

    private static void SetEfficiencyMode(bool enabled)
    {
        try
        {
            var processHandle = GetCurrentProcess();
            var state = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0,
                StateMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0
            };

            if (!SetProcessInformation(
                processHandle,
                ProcessPowerThrottlingInformation,
                ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
            {
                Log.Debug("SetProcessInformation failed while {Action} Wino efficiency mode. ErrorCode: {ErrorCode}",
                    enabled ? "enabling" : "disabling",
                    Marshal.GetLastPInvokeError());
            }

            if (!SetPriorityClass(processHandle, enabled ? IdlePriorityClass : NormalPriorityClass))
            {
                Log.Debug("SetPriorityClass failed while {Action} Wino efficiency mode. ErrorCode: {ErrorCode}",
                    enabled ? "enabling" : "disabling",
                    Marshal.GetLastPInvokeError());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to {Action} Wino efficiency mode.", enabled ? "enable" : "disable");
        }
    }

    private static bool IsApplicationExiting()
        => (Application.Current as App)?.IsExiting == true;

    private static (WinoWindowKind Kind, string Name) CreateKey(WinoWindowKind kind, string? name)
    {
        var resolvedName = NormalizeName(name ?? kind.ToString());
        return (kind, string.IsNullOrWhiteSpace(resolvedName) ? kind.ToString() : resolvedName);
    }

    private static string NormalizeName(string name) => name.Trim();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess,
        nuint dwMinimumWorkingSetSize,
        nuint dwMaximumWorkingSetSize,
        uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        IntPtr hProcess,
        uint processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
