namespace Wino.Mail.ViewModels.Companion;

/// <summary>What the host knows about the app when the companion opens.</summary>
public enum CompanionReadinessState
{
    Initializing,
    NoAccounts,
    Ready
}

/// <summary>The state the companion surface shows.</summary>
public enum CompanionSurfaceState
{
    Initializing,
    NoAccounts,
    Ready,
    Unavailable
}
