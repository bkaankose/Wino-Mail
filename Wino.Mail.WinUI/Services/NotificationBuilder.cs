using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Notifications;
using Serilog;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Activation;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Services;

public class NotificationBuilder : INotificationBuilder
{
    private const string NotificationIconRootUri = "ms-appx:///Assets/NotificationIcons/";
    private static int _calendarTaskbarBadgeCount;

    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IPreferencesService _preferencesService;

    public NotificationBuilder(IAccountService accountService,
                               IFolderService folderService,
                               IMailService mailService,
                               IThumbnailService thumbnailService,
                               IPreferencesService preferencesService)
    {
        _accountService = accountService;
        _folderService = folderService;
        _mailService = mailService;
        _thumbnailService = thumbnailService;
        _preferencesService = preferencesService;

        WeakReferenceMessenger.Default.Register<MailReadStatusChanged>(this, (r, msg) =>
        {
            RemoveNotification(msg.UniqueId);
        });
    }

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> downloadedMailItems)
    {
        try
        {
            var inboxMailItems = new List<MailCopy>();

            foreach (var item in downloadedMailItems)
            {
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId);
                if (mailItem != null)
                {
                    inboxMailItems.Add(mailItem);
                }
            }

            var mailCount = inboxMailItems.Count;
            if (mailCount == 0)
                return;

            if (mailCount > 3)
            {
                var builder = CreateBuilder();
                builder.AddText(Translator.Notifications_MultipleNotificationsTitle);
                builder.AddText(string.Format(Translator.Notifications_MultipleNotificationsMessage, mailCount));
                builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);
                builder.AddAudio(new Uri("ms-winsoundevent:Notification.Mail"));

                ShowNotification(builder, WinoApplicationMode.Mail);
            }
            else
            {
                foreach (var mailItem in inboxMailItems)
                {
                    await CreateSingleNotificationAsync(mailItem);
                }

                await UpdateTaskbarIconBadgeAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create notifications.");
        }
    }

    public async Task UpdateTaskbarIconBadgeAsync()
    {
        var totalUnreadCount = 0;

        try
        {
            var accounts = await _accountService.GetAccountsAsync();

            foreach (var account in accounts)
            {
                if (!account.Preferences.IsTaskbarBadgeEnabled)
                    continue;

                var accountInbox = await _folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox);
                if (accountInbox == null)
                    continue;

                var inboxUnreadCount = await _folderService.GetFolderNotificationBadgeAsync(accountInbox.Id);
                totalUnreadCount += inboxUnreadCount;
            }

            UpdateBadge(AppEntryConstants.MailApplicationId, totalUnreadCount > 0 ? totalUnreadCount : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while updating taskbar badge.");
        }
    }

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount)
    {
        if (newlyDownloadedCount <= 0)
            return Task.CompletedTask;

        var badgeCount = Interlocked.Add(ref _calendarTaskbarBadgeCount, newlyDownloadedCount);
        UpdateBadge(AppEntryConstants.CalendarApplicationId, badgeCount > 0 ? badgeCount : null);
        return Task.CompletedTask;
    }

    public Task ClearCalendarTaskbarBadgeAsync()
    {
        Interlocked.Exchange(ref _calendarTaskbarBadgeCount, 0);
        UpdateBadge(AppEntryConstants.CalendarApplicationId, null);
        return Task.CompletedTask;
    }

    public void RemoveNotification(Guid mailUniqueId)
    {
        try
        {
            ToastNotificationManager.History.Remove(mailUniqueId.ToString());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to remove notification for mail {mailUniqueId}");
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        var builder = CreateBuilder();
        builder.AddText(Translator.Exception_AccountNeedsAttention_Title);
        builder.AddText(string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name));
        builder.AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);
        builder.AddButton(new ToastButton()
            .SetContent(Translator.Buttons_FixAccount)
            .AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail));

        ShowNotification(builder, WinoApplicationMode.Mail);
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
        var builder = CreateBuilder();
        builder.AddText(Translator.Exception_WebView2RuntimeMissing_Title);
        builder.AddText(Translator.Exception_WebView2RuntimeMissing_Message);
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        ShowNotification(builder, WinoApplicationMode.Mail);
    }

    public void CreateStoreUpdateNotification()
    {
        var builder = CreateBuilder();
        builder.AddText(Translator.Notifications_StoreUpdateAvailableTitle);
        builder.AddText(Translator.Notifications_StoreUpdateAvailableMessage);
        builder.AddArgument(Constants.ToastStoreUpdateActionKey, Constants.ToastStoreUpdateActionInstall);
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        ShowNotification(builder, WinoApplicationMode.Mail, "store-update-available");
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem == null)
            return Task.CompletedTask;

        var builder = CreateBuilder(ToastScenario.Reminder);
        var localStart = calendarItem.GetLocalStartDate();
        var reminderContext = GetCalendarReminderContext(localStart, DateTime.Now);

        builder.AddText(calendarItem.Title);
        builder.AddText($"{reminderContext} - {localStart:g}");

        if (!string.IsNullOrWhiteSpace(calendarItem.Location))
            builder.AddText(calendarItem.Location);

        builder.AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction);
        builder.AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar);
        builder.AddAudio(new Uri("ms-winsoundevent:Notification.Reminder"));

        var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds,
            _preferencesService.DefaultReminderDurationInSeconds);

        if (allowedSnoozeMinutes.Count > 0)
        {
            var preferredSnoozeMinutes = _preferencesService.DefaultSnoozeDurationInMinutes;
            var defaultSnoozeMinutes = allowedSnoozeMinutes.Contains(preferredSnoozeMinutes)
                ? preferredSnoozeMinutes
                : allowedSnoozeMinutes[0];

            builder.AddButton(new ToastButton()
                .SetContent(Translator.CalendarReminder_SnoozeAction)
                .SetImageUri(GetNotificationIconUri("calendar-snooze"))
                .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarSnoozeAction)
                .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                .AddArgument(Constants.ToastCalendarSnoozeDurationMinutesKey, defaultSnoozeMinutes.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
        }

        builder.AddButton(new ToastButton()
            .SetContent(Translator.Buttons_Open)
            .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction)
            .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));

        if (Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out _))
        {
            builder.AddButton(new ToastButton()
                .SetContent(Translator.CalendarEventDetails_JoinOnline)
                .SetImageUri(GetNotificationIconUri("calendar-join"))
                .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarJoinOnlineAction)
                .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
        }

        var tag = $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}";
        ShowNotification(builder, WinoApplicationMode.Calendar, tag);

        return Task.CompletedTask;
    }

    private async Task CreateSingleNotificationAsync(MailCopy mailItem)
    {
        var builder = CreateBuilder();

        var avatarThumbnail = await _thumbnailService.GetThumbnailAsync(mailItem.FromAddress, awaitLoad: true);
        if (!string.IsNullOrEmpty(avatarThumbnail))
        {
            var tempFile = await Windows.Storage.ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                $"{Guid.NewGuid()}.png",
                Windows.Storage.CreationCollisionOption.ReplaceExisting);

            await using (var stream = await tempFile.OpenStreamForWriteAsync())
            {
                var bytes = Convert.FromBase64String(avatarThumbnail);
                await stream.WriteAsync(bytes);
            }

            builder.AddAppLogoOverride(new Uri($"ms-appdata:///temp/{tempFile.Name}"), ToastGenericAppLogoCrop.Default);
        }

        builder.AddCustomTimeStamp(mailItem.CreationDate.ToLocalTime());
        builder.AddText(mailItem.FromName);
        builder.AddText(mailItem.Subject);
        builder.AddText(mailItem.PreviewText);
        builder.AddArgument(Constants.ToastMailUniqueIdKey, mailItem.UniqueId.ToString());
        builder.AddArgument(Constants.ToastActionKey, MailOperation.Navigate.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);
        builder.AddButton(GetMarkAsReadButton(mailItem.UniqueId));
        builder.AddButton(GetDeleteButton(mailItem.UniqueId));
        builder.AddButton(GetArchiveButton(mailItem.UniqueId));
        builder.AddAudio(new Uri("ms-winsoundevent:Notification.Mail"));

        ShowNotification(builder, WinoApplicationMode.Mail, mailItem.UniqueId.ToString());
    }

    private void UpdateBadge(string applicationId, int? badgeCount)
    {
        var badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication(applicationId);

        if (!badgeCount.HasValue || badgeCount.Value <= 0)
        {
            badgeUpdater.Clear();
            return;
        }

        XmlDocument badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        if (badgeXml.SelectSingleNode("/badge") is not XmlElement badgeElement)
        {
            badgeUpdater.Clear();
            return;
        }

        badgeElement.SetAttribute("value", badgeCount.Value.ToString());
        badgeUpdater.Update(new BadgeNotification(badgeXml));
    }

    private static string GetCalendarReminderContext(DateTime localStart, DateTime nowLocal)
    {
        var delta = localStart - nowLocal;
        var absDelta = delta.Duration();

        if (absDelta < TimeSpan.FromMinutes(1))
            return delta.TotalSeconds >= 0 ? Translator.CalendarReminder_StartingNow : Translator.CalendarReminder_StartedNow;

        if (delta.TotalSeconds > 0)
        {
            if (delta.TotalHours >= 1)
            {
                var hours = Math.Max(1, (int)Math.Floor(delta.TotalHours));
                return string.Format(Translator.CalendarReminder_StartsInHours, hours);
            }

            var minutes = Math.Max(1, (int)Math.Floor(delta.TotalMinutes));
            return string.Format(Translator.CalendarReminder_StartsInMinutes, minutes);
        }

        if (absDelta.TotalHours >= 1)
        {
            var hoursAgo = Math.Max(1, (int)Math.Floor(absDelta.TotalHours));
            return string.Format(Translator.CalendarReminder_StartedHoursAgo, hoursAgo);
        }

        var minutesAgo = Math.Max(1, (int)Math.Floor(absDelta.TotalMinutes));
        return string.Format(Translator.CalendarReminder_StartedMinutesAgo, minutesAgo);
    }

    private ToastButton GetArchiveButton(Guid mailUniqueId)
        => new ToastButton()
            .SetContent(Translator.MailOperation_Archive)
            .SetImageUri(GetNotificationIconUri("mail-archive"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.Archive.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private ToastButton GetDeleteButton(Guid mailUniqueId)
        => new ToastButton()
            .SetContent(Translator.MailOperation_Delete)
            .SetImageUri(GetNotificationIconUri("mail-delete"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.SoftDelete.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private ToastButton GetMarkAsReadButton(Guid mailUniqueId)
        => new ToastButton()
            .SetContent(Translator.MailOperation_MarkAsRead)
            .SetImageUri(GetNotificationIconUri("mail-markread"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.MarkAsRead.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private static ToastContentBuilder CreateBuilder(ToastScenario scenario = ToastScenario.Default)
        => new ToastContentBuilder().SetToastScenario(scenario);

    private static void ShowNotification(ToastContentBuilder builder, WinoApplicationMode mode, string? tag = null)
    {
        var notification = new ToastNotification(builder.GetXml());

        if (!string.IsNullOrWhiteSpace(tag))
        {
            notification.Tag = tag;
        }

        ToastNotificationManager.CreateToastNotifier(AppEntryConstants.GetAppUserModelId(mode)).Show(notification);
    }

    private static Uri GetNotificationIconUri(string iconName)
        => new($"{NotificationIconRootUri}{iconName}.png");
}
