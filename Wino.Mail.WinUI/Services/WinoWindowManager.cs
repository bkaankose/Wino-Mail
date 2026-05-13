using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Services;

public class WinoWindowManager : IWinoWindowManager
{
    public event EventHandler<WindowEx?>? ActiveWindowChanged;
    public event EventHandler<WindowEx>? WindowRemoved;

    private readonly object _syncLock = new();
    private readonly Dictionary<(WinoWindowKind Kind, string Name), WindowEx> _windows = [];
    private readonly Dictionary<WindowEx, (WinoWindowKind Kind, string Name)> _windowKeys = [];
    private readonly Dictionary<(WinoWindowKind Kind, string Name), Frame> _primaryNavigationFrames = [];

    public WindowEx? ActiveWindow { get; private set; }

    public WindowEx CreateWindow(WinoWindowKind kind, Func<WindowEx> factory, string? name = null)
    {
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
        window.Show();
        window.BringToFront();
        window.Activate();

        lock (_syncLock)
        {
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
        window.Hide();

        lock (_syncLock)
        {
            if (ReferenceEquals(ActiveWindow, window))
            {
                ActiveWindow = null;
                ActiveWindowChanged?.Invoke(this, null);
            }
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

    public void SetPrimaryNavigationFrame(WinoWindowKind kind, Frame frame, string? name = null)
    {
        lock (_syncLock)
        {
            _primaryNavigationFrames[CreateKey(kind, name)] = frame;
        }
    }

    public Frame? GetPrimaryNavigationFrame(WinoWindowKind kind, string? name = null)
    {
        lock (_syncLock)
        {
            _primaryNavigationFrames.TryGetValue(CreateKey(kind, name), out var frame);
            return frame;
        }
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

        lock (_syncLock)
        {
            if (_windowKeys.ContainsKey(window))
            {
                ActiveWindow = window;
                ActiveWindowChanged?.Invoke(this, window);
            }
        }
    }

    private void WindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not WindowEx window)
            return;

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
            _primaryNavigationFrames.Remove(key);
            WindowRemoved?.Invoke(this, window);

            if (wasActiveWindow)
            {
                ActiveWindowChanged?.Invoke(this, null);
            }
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
                window.Activated -= WindowActivated;
                window.Closed -= WindowClosed;
                window.Close();
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
            _primaryNavigationFrames.Clear();
            ActiveWindow = null;
        }

        ActiveWindowChanged?.Invoke(this, null);
    }

    private static (WinoWindowKind Kind, string Name) CreateKey(WinoWindowKind kind, string? name)
    {
        var resolvedName = NormalizeName(name ?? kind.ToString());
        return (kind, string.IsNullOrWhiteSpace(resolvedName) ? kind.ToString() : resolvedName);
    }

    private static string NormalizeName(string name) => name.Trim();
}
