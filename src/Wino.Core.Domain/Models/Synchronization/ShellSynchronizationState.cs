namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// What the shell title bar's synchronization button should show for the active mode.
/// Always derived from live <see cref="AccountSynchronizationProgress"/> values, never
/// stored, so the button cannot drift away from the synchronization manager.
/// </summary>
/// <param name="IsSynchronizing">Whether anything the mode cares about is running.</param>
/// <param name="IsIndeterminate">True when no meaningful unit count is available.</param>
/// <param name="ProgressPercentage">Aggregate completion (0-100). Meaningless while indeterminate.</param>
public readonly record struct ShellSynchronizationState(
    bool IsSynchronizing,
    bool IsIndeterminate,
    double ProgressPercentage)
{
    public static ShellSynchronizationState Idle { get; } = new(false, false, 0d);
}
