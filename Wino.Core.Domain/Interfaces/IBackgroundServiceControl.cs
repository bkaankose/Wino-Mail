using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Control surface of the background companion process, exposed over RPC.
/// Implemented only inside Wino.BackgroundService; the UI talks to it through
/// the generated remote proxy.
/// </summary>
[Wino.Core.Domain.Attributes.WinoRpcService]
public interface IBackgroundServiceControl
{
    /// <summary>
    /// Asks the companion process to exit gracefully (replaces the old
    /// TerminateServerRequested messenger message).
    /// </summary>
    Task TerminateAsync();

    /// <summary>
    /// Handles toast quick actions (mark read, delete, …) that need no UI.
    /// Arguments are the raw toast activation argument string.
    /// </summary>
    Task HandleToastActionsAsync(string toastArguments);

    /// <summary>
    /// Returns the companion app version for diagnostics and update mismatch checks.
    /// </summary>
    Task<string> GetServerVersionAsync();

    /// <summary>
    /// Preference values live in the package-shared settings store, but change events do
    /// not cross processes. The UI calls this after changing a preference so companion
    /// loops (sync interval, tray, lifecycle) can react.
    /// </summary>
    Task NotifyPreferenceChangedAsync(string propertyName);
}
