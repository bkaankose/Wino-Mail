namespace Wino.Core.Domain.Enums;

public enum ApplicationRuntimeState
{
    Uninitialized,
    Initialized,
    Running,
    Stopping,
    Stopped,
    Faulted
}
