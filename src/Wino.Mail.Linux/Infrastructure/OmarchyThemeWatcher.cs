using Wino.Platform.Linux.Desktop;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Watches Omarchy's <c>current</c> folder and calls back 250 ms after the last change. <c>omarchy-theme-set</c>
/// replaces <c>current/theme</c> with a new folder, so one monitor watches <c>current</c> (the swap and
/// <c>theme.name</c>) and another <c>current/theme</c> (a theme rewritten in place); the inner one is recreated after
/// each burst because the folder it watched may be gone. Main thread only.
/// </summary>
internal sealed class OmarchyThemeWatcher : IDisposable
{
    private const uint SettleMilliseconds = 250;

    private readonly string _currentFolder;
    private readonly Action _changed;
    private readonly MainLoop.Throttle _settle;
    private Gio.FileMonitor? _outer;
    private Gio.FileMonitor? _inner;
    private IDisposable? _outerSubscription;
    private IDisposable? _innerSubscription;
    private bool _disposed;

    public OmarchyThemeWatcher(string currentFolder, Action changed)
    {
        _currentFolder = currentFolder;
        _changed = changed;
        _settle = new MainLoop.Throttle(SettleMilliseconds, Settled);
        (_outer, _outerSubscription) = Monitor(currentFolder);
        WatchTheme();
    }

    public string CurrentFolder => _currentFolder;

    private void Settled()
    {
        if (_disposed) return;
        WatchTheme();
        _changed();
    }

    private void WatchTheme()
    {
        _innerSubscription?.Dispose();
        _inner?.Cancel();
        (_inner, _innerSubscription) = Monitor(Path.Combine(_currentFolder, OmarchyPalette.ThemeFolderName));
    }

    private (Gio.FileMonitor?, IDisposable?) Monitor(string folder)
    {
        if (!Directory.Exists(folder)) return (null, null);
        try
        {
            var monitor = Gio.FileHelper.NewForPath(folder).MonitorDirectory(Gio.FileMonitorFlags.WatchMoves, null);
            var subscription = GtkSignals.Connect<Gio.FileMonitor, Gio.FileMonitor.ChangedSignalArgs>(
                h => monitor.OnChanged += h, h => monitor.OnChanged -= h, (_, _) => _settle.Request());
            return (monitor, subscription);
        }
        catch (GLib.GException exception)
        {
            Serilog.Log.Warning("The Omarchy theme folder {Folder} cannot be watched: {Message}", folder, exception.Message);
            return (null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settle.Dispose();
        _innerSubscription?.Dispose();
        _outerSubscription?.Dispose();
        _inner?.Cancel();
        _outer?.Cancel();
        _inner = null;
        _outer = null;
    }
}
