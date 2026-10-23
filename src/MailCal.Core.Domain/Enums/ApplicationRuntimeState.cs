namespace MailCal.Core.Domain.Enums;

public enum ApplicationRuntimeState
{
    Uninitialized,
    Initialized,
    Running,
    Stopping,
    Stopped,
    Faulted
}
