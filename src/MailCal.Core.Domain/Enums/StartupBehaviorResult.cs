namespace MailCal.Core.Domain.Enums;

public enum StartupBehaviorResult
{
    Enabled,
    Disabled,
    DisabledByUser,
    DisabledByPolicy,
    Fatal,
    Unavailable
}
