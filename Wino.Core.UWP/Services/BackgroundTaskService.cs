using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using Windows.ApplicationModel.Background;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.UWP.Services
{
    public class BackgroundTaskService : IBackgroundTaskService
    {
        private const string IsBackgroundExecutionDeniedMessageKey = nameof(IsBackgroundExecutionDeniedMessageKey);

        public const string BackgroundSynchronizationTimerTaskNameEx = nameof(BackgroundSynchronizationTimerTaskNameEx);
        public const string ToastActivationTaskEx = nameof(ToastActivationTaskEx);

        private const string SessionConnectedTaskEntryPoint = "Wino.BackgroundTasks.SessionConnectedTask";
        private const string SessionConnectedTaskName = "SessionConnectedTask";

        private readonly IConfigurationService _configurationService;
        private readonly List<string> registeredBackgroundTaskNames = new List<string>();

        public BackgroundTaskService(IConfigurationService configurationService)
        {
            _configurationService = configurationService;

            LoadRegisteredTasks();
        }

        // Calling WinRT all the time for registered tasks might be slow. Cache them on ctor.
        private void LoadRegisteredTasks()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                registeredBackgroundTaskNames.Add(task.Value.Name);
            }

            Log.Information($"Found {registeredBackgroundTaskNames.Count} registered background tasks. [{string.Join(',', registeredBackgroundTaskNames)}]");
        }

        public async Task HandleBackgroundTaskRegistrations()
        {
            var response = await BackgroundExecutionManager.RequestAccessAsync();

            if (response == BackgroundAccessStatus.DeniedBySystemPolicy ||
                response == BackgroundAccessStatus.DeniedByUser)
            {
                // Only notify users about disabled background execution once.

                bool isNotifiedBefore = _configurationService.Get(IsBackgroundExecutionDeniedMessageKey, false);

                if (!isNotifiedBefore)
                {
                    _configurationService.Set(IsBackgroundExecutionDeniedMessageKey, true);

                    throw new BackgroundTaskExecutionRequestDeniedException();
                }
            }
            else
            {
                RegisterSessionConnectedTask();
                RegisterTimerSynchronizationTask();
                RegisterToastNotificationHandlerBackgroundTask();
            }
        }

        private bool IsBackgroundTaskRegistered(string taskName)
            => registeredBackgroundTaskNames.Contains(taskName);

        public void UnregisterAllBackgroundTask()
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                task.Value.Unregister(true);
            }
        }

        private void LogBackgroundTaskRegistration(string taskName)
        {
            Log.Information($"Registered new background task -> {taskName}");

            registeredBackgroundTaskNames.Add($"{taskName}");
        }

        private BackgroundTaskRegistration RegisterSessionConnectedTask()
        {
            if (IsBackgroundTaskRegistered(SessionConnectedTaskName)) return null;

            var builder = new BackgroundTaskBuilder
            {
                Name = SessionConnectedTaskName,
                TaskEntryPoint = SessionConnectedTaskEntryPoint
            };

            builder.SetTrigger(new SystemTrigger(SystemTriggerType.SessionConnected, false));

            LogBackgroundTaskRegistration(SessionConnectedTaskName);

            return builder.Register();
        }

        private BackgroundTaskRegistration RegisterToastNotificationHandlerBackgroundTask()
        {
            if (IsBackgroundTaskRegistered(ToastActivationTaskEx)) return null;

            var builder = new BackgroundTaskBuilder
            {
                Name = ToastActivationTaskEx
            };

            builder.SetTrigger(new ToastNotificationActionTrigger());

            LogBackgroundTaskRegistration(ToastActivationTaskEx);

            return builder.Register();
        }

        private BackgroundTaskRegistration RegisterTimerSynchronizationTask()
        {
            if (IsBackgroundTaskRegistered(BackgroundSynchronizationTimerTaskNameEx)) return null;

            var builder = new BackgroundTaskBuilder
            {
                Name = BackgroundSynchronizationTimerTaskNameEx
            };

            builder.SetTrigger(new TimeTrigger(15, false));
            builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));

            LogBackgroundTaskRegistration(BackgroundSynchronizationTimerTaskNameEx);

            return builder.Register();
        }
    }
}
