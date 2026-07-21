using Windows.ApplicationModel.Background;
using Wino.Mail.Uwp.Activation;

namespace Wino.Mail.Uwp.Services;

internal static class ToastBackgroundRegistration
{
    private const string MailTaskName = "Wino.Mail.ToastActions.v1";
    private const string LegacyCalendarTaskName = "Wino.Calendar.ToastActions.v1";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureRegisteredAsync()
    {
        await Gate.WaitAsync();
        try
        {
            foreach (var legacyTask in BackgroundTaskRegistration.AllTasks.Values
                         .Where(task => string.Equals(task.Name, LegacyCalendarTaskName, StringComparison.Ordinal)))
            {
                legacyTask.Unregister(cancelTask: true);
            }

            var registeredNames = BackgroundTaskRegistration.AllTasks.Values
                .Select(task => task.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (registeredNames.Contains(MailTaskName))
            {
                return;
            }

            var access = await BackgroundExecutionManager.RequestAccessAsync();
            if (access is BackgroundAccessStatus.DeniedBySystemPolicy or BackgroundAccessStatus.DeniedByUser)
            {
                return;
            }

            RegisterIfMissing(MailTaskName, AppEntryConstants.MailAppUserModelId, registeredNames);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void RegisterIfMissing(
        string taskName,
        string appUserModelId,
        IReadOnlySet<string> registeredNames)
    {
        if (registeredNames.Contains(taskName))
        {
            return;
        }

        try
        {
            var builder = new BackgroundTaskBuilder { Name = taskName };
            builder.SetTrigger(new ToastNotificationActionTrigger(appUserModelId));
            builder.Register();
        }
        catch (Exception) when (BackgroundTaskRegistration.AllTasks.Values.Any(task => task.Name == taskName))
        {
            // Another activation completed the same registration.
        }
    }
}
