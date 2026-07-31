namespace Wino.Core.Domain.Enums;

/// <summary>
/// Indicates the state of synchronizer.
/// </summary>
public enum AccountSynchronizerState
{
    Idle,
    ExecutingRequests,
    Synchronizing
}
