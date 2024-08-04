using System;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel.Background;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services
{
    public class BackgroundTaskService : IBackgroundTaskService
    {
        private const string Is180BackgroundTasksRegisteredKey = nameof(Is180BackgroundTasksRegisteredKey);

        public const string ToastActivationTaskEx = nameof(ToastActivationTaskEx);

        private const string SessionConnectedTaskEntryPoint = "Wino.BackgroundTasks.SessionConnectedTask";
        private const string SessionConnectedTaskName = "SessionConnectedTask";

        private readonly IConfigurationService _configurationService;

        public BackgroundTaskService(IConfigurationService configurationService)
        {
            _configurationService = configurationService;
        }

        public async Task HandleBackgroundTaskRegistrations()
        {
            bool is180BackgroundTaskRegistered = _configurationService.Get<bool>(Is180BackgroundTasksRegisteredKey);

            // Don't re-register tasks.
            if (is180BackgroundTaskRegistered) return;

            var response = await BackgroundExecutionManager.RequestAccessAsync();

            if (response != BackgroundAccessStatus.DeniedBySystemPolicy ||
                response != BackgroundAccessStatus.DeniedByUser)
            {
                // Unregister all tasks and register new ones.

                UnregisterAllBackgroundTask();
                RegisterSessionConnectedTask();

                _configurationService.Set(Is180BackgroundTasksRegisteredKey, true);
            }
        }

        public void UnregisterAllBackgroundTask()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                task.Value.Unregister(true);
            }

            Log.Information("Unregistered all background tasks.");
        }

        private BackgroundTaskRegistration RegisterSessionConnectedTask()
        {
            var builder = new BackgroundTaskBuilder
            {
                Name = SessionConnectedTaskName,
                TaskEntryPoint = SessionConnectedTaskEntryPoint
            };

            builder.SetTrigger(new SystemTrigger(SystemTriggerType.SessionConnected, false));

            return builder.Register();
        }
    }
}
