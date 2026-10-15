#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.Notifications;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>
/// Debug-only notification checks through the real builder (the Mac debug bridge <c>notify-test</c> twin):
/// <c>DebugCommand --es command testnotification --es kind mail|calendar|attention|people|task</c>, a reminder that is due
/// in a minute (<c>seedreminder</c>) and the background state (<c>backgroundstate</c>).
/// </summary>
internal static class DebugNotifications
{
    public static async Task<string> PostTestAsync(IServiceProvider services, string kind)
    {
        var builder = services.GetRequiredService<INotificationBuilder>();
        switch (string.IsNullOrEmpty(kind) ? "mail" : kind)
        {
            case "mail":
            {
                // The inbox's three newest mails, like the mail list's "Test notification" command on several rows.
                foreach (var account in await services.GetRequiredService<IAccountService>().GetAccountsAsync())
                {
                    var inbox = await services.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox);
                    if (inbox is null) continue;
                    var mails = (await services.GetRequiredService<IMailService>().GetMailsByFolderIdAsync(inbox.Id))
                        .OrderByDescending(mail => mail.CreationDate).Take(3).ToList();
                    if (mails.Count == 0) continue;
                    await builder.CreateTestNotificationsAsync(mails);
                    return "ok " + string.Join(",", mails.Select(mail => mail.UniqueId));
                }
                return "no mail";
            }
            case "calendar":
            {
                var item = new CalendarItem
                {
                    Id = Guid.NewGuid(),
                    Title = "Wino test reminder",
                    Location = "Wino lab",
                    StartDate = DateTime.UtcNow.AddMinutes(15),
                    StartTimeZone = TimeZoneInfo.Utc.Id,
                    DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds
                };
                await builder.CreateTestCalendarReminderNotificationAsync(item);
                return "ok " + item.Id;
            }
            case "attention":
            {
                var account = (await services.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault();
                if (account is null) return "no account";
                builder.CreateAttentionRequiredNotification(account);
                return "ok " + account.Id;
            }
            case "people":
                await builder.CreateTestPeopleNotificationAsync(new AccountContact { DisplayName = "Wino test contact" });
                return "ok";
            case "task":
                await builder.CreateTestTaskReminderNotificationAsync(new AccountTask { Title = "Wino test task" });
                return "ok";
            default:
                return "usage: --es kind mail|calendar|attention|people|task";
        }
    }

    /// <summary>A local calendar on the first account (calendar access switched on) with an event whose reminder is due in a minute.</summary>
    public static async Task<string> SeedReminderAsync(IServiceProvider services, int reminderMinutes)
    {
        var accounts = services.GetRequiredService<IAccountService>();
        var calendars = services.GetRequiredService<ICalendarService>();
        var account = (await accounts.GetAccountsAsync()).FirstOrDefault();
        if (account is null) return "no account";
        if (!account.IsCalendarAccessGranted)
        {
            account.IsCalendarAccessGranted = true;
            await accounts.UpdateAccountAsync(account);
        }

        var calendar = (await calendars.GetAccountCalendarsAsync(account.Id)).FirstOrDefault();
        if (calendar is null)
        {
            calendar = new AccountCalendar
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                RemoteCalendarId = "wino-debug-calendar",
                Name = "Wino debug",
                IsPrimary = true,
                BackgroundColorHex = "#0F6CBD",
                TextColorHex = "#FFFFFF",
                TimeZone = TimeZoneInfo.Local.Id
            };
            await calendars.InsertAccountCalendarAsync(calendar);
        }

        var start = DateTime.UtcNow.AddMinutes(reminderMinutes + 1);
        var item = new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = calendar.Id,
            RemoteEventId = "wino-debug-" + Guid.NewGuid().ToString("N"),
            Title = $"Wino reminder check {DateTime.Now:HH:mm}",
            Location = "Wino lab",
            StartDate = start,
            StartTimeZone = TimeZoneInfo.Utc.Id,
            DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds
        };
        await calendars.CreateNewCalendarItemAsync(item, []);
        await calendars.SaveRemindersAsync(item.Id,
        [
            new Reminder { Id = Guid.NewGuid(), CalendarItemId = item.Id, DurationInSeconds = reminderMinutes * 60L, ReminderType = CalendarItemReminderType.Popup }
        ]);
        var next = await calendars.GetNextReminderTriggerAsync(DateTime.Now);
        return $"ok {item.Id} start(utc)={start:HH:mm:ss} nextTrigger(local)={next:HH:mm:ss}";
    }

    public static async Task<string> DescribeAsync(IServiceProvider services)
    {
        var reminders = services.GetRequiredService<AndroidReminderScheduler>();
        var next = await services.GetRequiredService<ICalendarService>().GetNextReminderTriggerAsync(DateTime.Now);
        var runtime = services.GetRequiredService<IApplicationRuntime>();
        var queued = runtime.State is ApplicationRuntimeState.Initialized or ApplicationRuntimeState.Running
            && services.GetRequiredService<ISynchronizationManager>().GetAllSynchronizers().Any(synchronizer => synchronizer.HasQueuedRequests());
        var builder = services.GetRequiredService<AndroidNotificationBuilder>();
        return $"runtime={runtime.State} queuedChanges={queued} exactAlarms={reminders.CanScheduleExactAlarms} " +
               $"alarm={reminders.ScheduledForLocal:HH:mm:ss}({(reminders.IsScheduledExact ? "exact" : "inexact")}) nextReminder={next:HH:mm:ss} " +
               $"notificationsAllowed={Infrastructure.AndroidNotificationPermission.IsGranted(services.GetRequiredService<global::Android.Content.Context>())} " +
               $"badge={builder.BadgeCount}";
    }
}
#endif
