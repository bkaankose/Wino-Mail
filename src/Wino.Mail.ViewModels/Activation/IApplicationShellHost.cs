#nullable enable
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Activation;

/// <summary>
/// What the shared activation and notification routing needs from a head's window and shell host.
/// Each head implements it over its navigation service (AppKit, UIKit, WinUI).
/// </summary>
public interface IApplicationShellHost
{
    /// <summary>The shell exists, finished activating and shows <paramref name="mode"/>.</summary>
    bool IsShellActive(WinoApplicationMode mode);

    /// <summary>
    /// Shows the shell in <paramref name="mode"/> (creating it when needed) and brings the window forward.
    /// False when the shell cannot be shown, for example while account setup owns the window.
    /// </summary>
    Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null);

    /// <summary>
    /// Shows the mail shell to select a message in it, like <see cref="EnsureShellAsync"/>. A head that refreshes the
    /// theme of a shell it brings forward (Windows) skips that here, so a visible shell does not flash while it
    /// navigates to the message.
    /// </summary>
    Task<bool> EnsureShellForMessageAsync() => EnsureShellAsync(WinoApplicationMode.Mail);

    /// <summary>Shows Settings on <paramref name="page"/>.</summary>
    Task ShowSettingsAsync(WinoPage page, object? parameter);

    /// <summary>Presents an opened message file read-only. False when it could not be shown.</summary>
    Task<bool> OpenMailFileAsync(string path, byte[] content);
}
