using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// The high contrast state of the window that shows an engine view, for the reader and the composer alike. The state
/// is read per window, so a view that moves to another window is watched again there. <see cref="Start"/> is paired
/// with <see cref="Stop"/> when the view leaves the tree, because the system object keeps its subscribers alive.
/// </summary>
internal sealed partial class WindowHighContrastWatcher
{
    private readonly DispatcherQueue _dispatcherQueue;
    private ThemeSettings? _themeSettings;

    public WindowHighContrastWatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool IsHighContrast => _themeSettings?.HighContrast ?? false;

    /// <summary>Raised on the UI thread when Windows turns high contrast on or off.</summary>
    public event EventHandler? Changed;

    public void Start(XamlRoot? xamlRoot)
    {
        Stop();

        if (xamlRoot?.ContentIslandEnvironment is not { } environment)
            return;

        try
        {
            _themeSettings = ThemeSettings.CreateForWindowId(environment.AppWindowId);
            _themeSettings.Changed += OnThemeSettingsChanged;
        }
        catch (Exception ex)
        {
            _themeSettings = null;
            Serilog.Log.Debug(ex, "High contrast state is unavailable for the mail view.");
        }
    }

    public void Stop()
    {
        if (_themeSettings is not { } themeSettings)
            return;

        _themeSettings = null;
        themeSettings.Changed -= OnThemeSettingsChanged;
    }

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
        => _dispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _themeSettings))
                Changed?.Invoke(this, EventArgs.Empty);
        });
}
