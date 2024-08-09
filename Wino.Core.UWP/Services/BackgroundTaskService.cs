using Serilog;
using Windows.ApplicationModel.Background;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class BackgroundTaskService : IBackgroundTaskService
    {
        private const string IsBackgroundTasksUnregisteredKey = nameof(IsBackgroundTasksUnregisteredKey);

        private readonly IConfigurationService _configurationService;

        public BackgroundTaskService(IConfigurationService configurationService)
        {
            _configurationService = configurationService;
        }

        public void UnregisterAllBackgroundTask()
        {
            if (!_configurationService.Get(IsBackgroundTasksUnregisteredKey, false))
            {
                foreach (var task in BackgroundTaskRegistration.AllTasks)
                {
                    task.Value.Unregister(true);
                }

                Log.Information("Unregistered all background tasks.");
                _configurationService.Set(IsBackgroundTasksUnregisteredKey, true);
            }
        }
    }
}
