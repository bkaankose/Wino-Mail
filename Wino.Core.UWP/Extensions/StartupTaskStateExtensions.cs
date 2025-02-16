using Windows.ApplicationModel;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Extensions
{
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
}
