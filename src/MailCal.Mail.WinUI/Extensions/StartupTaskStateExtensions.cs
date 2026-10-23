using Windows.ApplicationModel;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Extensions;

public static class StartupTaskStateExtensions
{
    public static StartupBehaviorResult AsStartupBehaviorResult(this StartupTaskState state)
    {
        switch (state)
        {
            case StartupTaskState.Disabled:
            case StartupTaskState.DisabledByPolicy:
                return StartupBehaviorResult.Disabled;
            case StartupTaskState.DisabledByUser:
                return StartupBehaviorResult.DisabledByUser;
            case StartupTaskState.Enabled:
            case StartupTaskState.EnabledByPolicy:
                return StartupBehaviorResult.Enabled;
            default:
                return StartupBehaviorResult.Fatal;
        }
    }
}
