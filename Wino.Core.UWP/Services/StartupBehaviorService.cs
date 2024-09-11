using System;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Extensions;

namespace Wino.Core.UWP.Services
{
    public class StartupBehaviorService : IStartupBehaviorService
    {
        private const string WinoServerTaskId = "WinoServer";

        public async Task<StartupBehaviorResult> ToggleStartupBehavior(bool isEnabled)
        {
            try
            {
                var task = await StartupTask.GetAsync(WinoServerTaskId);

                if (isEnabled)
                {
                    await task.RequestEnableAsync();
                }
                else
                {
                    task.Disable();
                }
            }
            catch (Exception)
            {
                Log.Error("Error toggling startup behavior");

            }

            return await GetCurrentStartupBehaviorAsync();
        }

        public async Task<StartupBehaviorResult> GetCurrentStartupBehaviorAsync()
        {
            try
            {
                var task = await StartupTask.GetAsync(WinoServerTaskId);

                return task.State.AsStartupBehaviorResult();

            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting startup behavior");

                return StartupBehaviorResult.Fatal;
            }
        }
    }
}
