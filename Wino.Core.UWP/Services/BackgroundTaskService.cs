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

        private readonly IConfigurationService _configurationService;

        public BackgroundTaskService(IConfigurationService configurationService)
        {
            _configurationService = configurationService;
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

        public Task RegisterBackgroundTasksAsync()
        {
            return RegisterToastNotificationHandlerBackgroundTaskAsync();
        }

        public async Task RegisterToastNotificationHandlerBackgroundTaskAsync()
        {
            // If background task is already registered, do nothing.
            if (BackgroundTaskRegistration.AllTasks.Any(i => i.Value.Name.Equals(ToastNotificationActivationHandlerTaskName)))
                return;

            // Otherwise request access
            BackgroundAccessStatus status = await BackgroundExecutionManager.RequestAccessAsync();

            // Create the background task
            BackgroundTaskBuilder builder = new BackgroundTaskBuilder()
            {
                Name = ToastNotificationActivationHandlerTaskName
            };

            // Assign the toast action trigger
            builder.SetTrigger(new ToastNotificationActionTrigger());

            // And register the task
            BackgroundTaskRegistration registration = builder.Register();
        }
    }
}
