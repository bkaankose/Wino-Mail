namespace Wino.Core.Domain.Enums;

public enum SynchronizationCompletedState
{
    Success, // All succeeded.
    Canceled, // Canceled by user or HTTP call.
    Failed, // Exception.
    PartiallyCompleted // Some folders succeeded, some failed.
}
