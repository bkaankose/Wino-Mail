using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWinoShellWindow
{
    /// <summary>True while the window shows the app lock page instead of the shell.</summary>
    bool IsAppLocked { get; }

    /// <summary>
    /// The one gate for anything that would put the shell or its content on screen. Runs <paramref name="continuation"/>
    /// now when the window is unlocked; otherwise it replaces the pending continuation and runs after the unlock.
    /// Call on the UI thread.
    /// </summary>
    /// <returns>
    /// A task that completes after the continuation ran. It never completes when a later continuation replaces this
    /// one or the window closes while locked, so a caller's follow-up work is dropped with it.
    /// </returns>
    Task RunWhenUnlockedAsync(Action continuation);

    /// <summary>
    /// Completes after the pending continuation ran, or at once when the window is unlocked. Call on the UI thread.
    /// The same never-completes rule as <see cref="RunWhenUnlockedAsync"/> applies.
    /// </summary>
    Task WhenUnlockedAsync();

    void HandleAppActivation(string? launchArguments, string? tileId = null, string? appId = null);
    TitleBar GetTitleBar();
    Frame GetMainFrame();
    FrameworkElement GetRootContent();
}
