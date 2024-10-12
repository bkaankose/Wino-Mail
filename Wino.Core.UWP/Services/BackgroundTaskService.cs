using System;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel.Background;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class BackgroundTaskService : IBackgroundTaskService
    {
        private const string IsBackgroundTasksUnregisteredKey = nameof(IsBackgroundTasksUnregisteredKey);
        public const string ToastNotificationActivationHandlerTaskName = "ToastNotificationActivationHandlerTask";
        public const string AppUpdatedTaskName = "AppUpdatedTask";

        private readonly IConfigurationService _configurationService;
        private readonly IPreferencesService _preferencesService;

        public BackgroundTaskService(IConfigurationService configurationService, IPreferencesService preferencesService)
        {
            _configurationService = configurationService;
            _preferencesService = preferencesService;
        }

        public void UnregisterAllBackgroundTask()
        {
            if (_configurationService.Get(IsBackgroundTasksUnregisteredKey, false))
            {
                foreach (var task in BackgroundTaskRegistration.AllTasks)
                {
                    task.Value.Unregister(true);
                }

                Log.Information("Unregistered all background tasks.");
                _configurationService.Set(IsBackgroundTasksUnregisteredKey, true);
            }
        }

        public async Task RegisterBackgroundTasksAsync()
        {
            await RegisterToastNotificationHandlerBackgroundTaskAsync();

            if (_preferencesService.IsUpdateNotificationEnabled)
            {
                await RegisterAppUpdatedTaskAsync();
            }
        }

        public Task RegisterToastNotificationHandlerBackgroundTaskAsync()
        {
            return RegisterBackgroundTaskAsync(ToastNotificationActivationHandlerTaskName, new ToastNotificationActionTrigger());
        }

        public Task RegisterAppUpdatedTaskAsync()
        {
            return RegisterBackgroundTaskAsync(AppUpdatedTaskName, new SystemTrigger(SystemTriggerType.ServicingComplete, false));
        }

        private async Task RegisterBackgroundTaskAsync(string taskName, IBackgroundTrigger trigger)
        {
            // If background task is already registered, do nothing.
            if (BackgroundTaskRegistration.AllTasks.Any(i => i.Value.Name.Equals(taskName)))
                return;

            // Otherwise request access
            BackgroundAccessStatus status = await BackgroundExecutionManager.RequestAccessAsync();

            // Create the background task
            BackgroundTaskBuilder builder = new BackgroundTaskBuilder()
            {
                Name = taskName
            };

            // Assign the trigger
            builder.SetTrigger(trigger);

            // And register the task
            BackgroundTaskRegistration registration = builder.Register();
        }
    }
}
