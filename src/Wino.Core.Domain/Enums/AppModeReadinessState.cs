namespace Wino.Core.Domain.Enums;

/// <summary>
/// Whether an app mode's main page can create or change anything.
/// </summary>
public enum AppModeReadinessState
{
    /// <summary>At least one account can serve the mode and has data to work with.</summary>
    Ready,

    /// <summary>There are no accounts at all.</summary>
    NoAccounts,

    /// <summary>Accounts exist, but none of them has the mode turned on.</summary>
    FeatureDisabled,

    /// <summary>The mode is on, but an account must be signed in before it has anything to show.</summary>
    SignInRequired,

    /// <summary>The mode is on and signed in, but the first synchronization has not produced data yet.</summary>
    WaitingForSynchronization
}
