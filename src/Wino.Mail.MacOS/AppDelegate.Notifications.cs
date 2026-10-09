using Microsoft.Extensions.DependencyInjection;
using UserNotifications;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Platform.MacOS.Services;
#if DEBUG
using AppKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
#endif

namespace Wino.Mail.MacOS;

/// <summary>
/// Notification responses and calendar reminders. The response handler is the notification
/// center delegate from launch (cold-launch taps); it starts handling once the services exist.
/// </summary>
public sealed partial class AppDelegate
{
    private readonly MacNotificationResponseHandler _notificationResponses = new();

    /// <summary>Called from <see cref="ActivationLaunching"/>, before launching finishes.</summary>
    private void InstallNotificationResponseHandler()
    {
        try { UNUserNotificationCenter.Current.Delegate = _notificationResponses; }
        catch (Exception error) { Serilog.Log.Warning(error, "Could not install the notification delegate."); }
    }

    partial void NotificationsServicesReady()
    {
        if (_services is null) return;
        var builder = _services.GetRequiredService<INotificationBuilder>();
        // Categories exist before any request so actions show on the first notification.
        (builder as MacNotificationBuilder)?.RegisterCategories();
        _notificationResponses.Attach(_services);
        Observe(_services.GetRequiredService<ICalendarReminderServer>().StartAsync());
        Observe(builder.UpdateTaskbarIconBadgeAsync());
#if DEBUG
        RegisterNotificationDebugCommands(_services);
#endif
    }

    partial void NotificationsStopping(ref Task? stopping)
    {
        _notificationResponses.Detach();
        stopping = _services?.GetRequiredService<ICalendarReminderServer>().StopAsync();
    }

#if DEBUG
    private static void RegisterNotificationDebugCommands(IServiceProvider services)
    {
        var builder = services.GetRequiredService<INotificationBuilder>();
        // "notify-test mail|calendar [search]|people|task" posts a test notification through the real builder.
        MacDebugBridge.Register("notify-test", async args =>
        {
            var kind = args.Length > 0 ? args[0].ToLowerInvariant() : "mail";
            switch (kind)
            {
                case "mail":
                {
                    var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
                    foreach (var account in accounts.Where(account => account.IsMailAccessGranted))
                    {
                        var inbox = await services.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox);
                        if (inbox is null) continue;
                        var mails = await services.GetRequiredService<IMailService>().GetMailsByFolderIdAsync(inbox.Id);
                        var latest = mails.OrderByDescending(mail => mail.CreationDate).FirstOrDefault();
                        if (latest is null) continue;
                        await builder.CreateTestNotificationsAsync([latest]);
                        return "ok " + latest.UniqueId;
                    }
                    return "no mail";
                }
                case "calendar":
                {
                    CalendarItem? item = null;
                    if (args.Length > 1)
                        item = (await services.GetRequiredService<ICalendarService>().SearchCalendarItemsAsync(string.Join(' ', args[1..]), 1)).FirstOrDefault();
                    item ??= new CalendarItem
                    {
                        Id = Guid.NewGuid(),
                        Title = "Wino test reminder",
                        Location = "Wino lab",
                        StartDate = DateTime.UtcNow.AddMinutes(10),
                        StartTimeZone = TimeZoneInfo.Utc.Id,
                        DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds
                    };
                    await builder.CreateTestCalendarReminderNotificationAsync(item);
                    return "ok " + item.Id;
                }
                case "people":
                    await builder.CreateTestPeopleNotificationAsync(new AccountContact { DisplayName = "Wino test contact" });
                    return "ok";
                case "task":
                    await builder.CreateTestTaskReminderNotificationAsync(new AccountTask { Title = "Wino test task" });
                    return "ok";
                default:
                    return "usage: notify-test mail|calendar [search]|people|task";
            }
        });
        MacDebugBridge.Register("notify-cats", async _ =>
            builder is MacNotificationBuilder mac ? await mac.DescribeAsync() : "not the macOS builder");
        MacDebugBridge.Register("badge", async _ =>
        {
            await builder.UpdateTaskbarIconBadgeAsync();
            var label = NSApplication.SharedApplication.DockTile.BadgeLabel;
            return $"badge='{label}' unread={MacNotificationBuilder.LastSnapshot.TaskbarUnreadCount}";
        });
        MacDebugBridge.Register("notify-sounds", async _ =>
        {
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
            return services.GetRequiredService<MacNotificationSounds>().Describe(accounts.Where(account => account.Preferences is not null).Select(account => account.Preferences!));
        });
        MacDebugBridge.Register("notify-sound-play", args =>
        {
            var name = args.Length > 0 ? args[0] : MacNotificationSounds.DefaultSound;
            MacNotificationSounds.Play(name);
            return Task.FromResult($"played '{name}' known={MacNotificationSounds.IsSystemSound(name)}");
        });
        MacDebugBridge.Register("app-version", _ =>
        {
            var metadata = services.GetRequiredService<IAppMetadataService>();
            return Task.FromResult($"version={metadata.AppVersion} release={metadata.SentryRelease} dist={metadata.SentryDist}");
        });
    }
#endif
}
