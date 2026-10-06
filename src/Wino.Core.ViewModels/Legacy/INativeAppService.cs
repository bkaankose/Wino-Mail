using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface INativeAppService
{
    string GetWebAuthenticationBrokerUri();
    Task LaunchFileAsync(string filePath);
    Task<bool> LaunchUriAsync(Uri uri);

    /// <summary>
    /// Puts the given text on the system clipboard.
    /// </summary>
    Task CopyClipboardAsync(string text);

    bool IsCtrlKeyPressed();
    bool IsShiftKeyPressed();

    /// <summary>
    /// Gets whether Wino is set to launch on startup or not.
    /// </summary>
    Task<StartupBehaviorResult> GetCurrentStartupBehaviorAsync();

    /// <summary>
    /// Enables/disables the launch on startup behavior and returns the resulting state.
    /// </summary>
    Task<StartupBehaviorResult> ToggleStartupBehavior(bool isEnabled);

    /// <summary>
    /// Validates whether WebView2 runtime is installed and available for use.
    /// </summary>
    Task<bool> IsWebView2RuntimeAvailableAsync();

    /// <summary>
    /// Plays the sound that confirms a task completion.
    /// </summary>
    void PlayTaskCompletionSound();

    WindowsTaskbarPosition GetTaskbarPosition();

    /// <summary>
    /// Gets or sets the function that returns a pointer for main window hwnd for UWP.
    /// This is used to display WAM broker dialog on running UWP app called by a windowless server code.
    /// </summary>
    Func<IntPtr> GetCoreWindowHwnd { get; set; }
}
