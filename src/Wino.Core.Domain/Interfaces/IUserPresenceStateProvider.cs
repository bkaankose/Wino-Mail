namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Reports whether the user is in a state where notifications should not interrupt.
/// Abstracted so notification policy stays testable without the shell.
/// </summary>
public interface IUserPresenceStateProvider
{
    /// <summary>
    /// Whether a full-screen app, a presentation or a screen share is currently active.
    /// </summary>
    bool IsPresenting();

    /// <summary>
    /// Whether Windows is currently in its own quiet time (Do not disturb / Focus assist).
    /// Windows does not expose when that window ends, only that it is active.
    /// </summary>
    bool IsSystemQuietTimeActive();
}
