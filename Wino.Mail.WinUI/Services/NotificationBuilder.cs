using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
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
                builder.SetAudioUri(new Uri("ms-winsoundevent:Notification.Mail"));

                ShowNotification(builder);
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
            AppNotificationManager.Default.RemoveByTagAsync(mailUniqueId.ToString()).AsTask().GetAwaiter().GetResult();
        }
        catch (ArgumentException)
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
        builder.AddButton(new AppNotificationButton(Translator.Buttons_FixAccount)
            .AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail));

        ShowNotification(builder);
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
        var builder = CreateBuilder();
        builder.AddText(Translator.Exception_WebView2RuntimeMissing_Title);
        builder.AddText(Translator.Exception_WebView2RuntimeMissing_Message);
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        ShowNotification(builder);
    }

    public void CreateStoreUpdateNotification()
    {
        var builder = CreateBuilder();
        builder.AddText(Translator.Notifications_StoreUpdateAvailableTitle);
        builder.AddText(Translator.Notifications_StoreUpdateAvailableMessage);
        builder.AddArgument(Constants.ToastStoreUpdateActionKey, Constants.ToastStoreUpdateActionInstall);
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        ShowNotification(builder, "store-update-available");
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem == null)
            return Task.CompletedTask;

        var builder = CreateBuilder(AppNotificationScenario.Reminder);
        var localStart = calendarItem.GetLocalStartDate();
        var reminderContext = GetCalendarReminderContext(localStart, DateTime.Now);

        builder.AddText(calendarItem.Title);
        builder.AddText($"{reminderContext} - {localStart:g}");

        if (!string.IsNullOrWhiteSpace(calendarItem.Location))
            builder.AddText(calendarItem.Location);

        builder.AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction);
        builder.AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar);
        builder.SetAudioUri(new Uri("ms-winsoundevent:Notification.Reminder"));

        var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds,
            _preferencesService.DefaultReminderDurationInSeconds);

        if (allowedSnoozeMinutes.Count > 0)
        {
            var preferredSnoozeMinutes = _preferencesService.DefaultSnoozeDurationInMinutes;
            var defaultSnoozeMinutes = allowedSnoozeMinutes.Contains(preferredSnoozeMinutes)
                ? preferredSnoozeMinutes
                : allowedSnoozeMinutes[0];

            var selectionBox = new AppNotificationComboBox(Constants.ToastCalendarSnoozeDurationInputId)
                .SetSelectedItem(defaultSnoozeMinutes.ToString());

            foreach (var snoozeMinutes in allowedSnoozeMinutes)
            {
                selectionBox.AddItem(
                    snoozeMinutes.ToString(),
                    string.Format(Translator.CalendarReminder_SnoozeMinutesOption, snoozeMinutes));
            }

            builder.AddComboBox(selectionBox);
            builder.AddButton(new AppNotificationButton(Translator.CalendarReminder_SnoozeAction)
                .SetIcon(GetNotificationIconUri("calendar-snooze"))
                .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarSnoozeAction)
                .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
        }

        builder.AddButton(new AppNotificationButton(Translator.Buttons_Open)
            .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction)
            .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));

        if (Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out _))
        {
            builder.AddButton(new AppNotificationButton(Translator.CalendarEventDetails_JoinOnline)
                .SetIcon(GetNotificationIconUri("calendar-join"))
                .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarJoinOnlineAction)
                .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
        }

        var tag = $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}";
        ShowNotification(builder, tag);

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

            builder.SetAppLogoOverride(new Uri($"ms-appdata:///temp/{tempFile.Name}"), AppNotificationImageCrop.Default);
        }

        builder.SetTimeStamp(mailItem.CreationDate.ToLocalTime());
        builder.AddText(mailItem.FromName);
        builder.AddText(mailItem.Subject);
        builder.AddText(mailItem.PreviewText);
        builder.AddArgument(Constants.ToastMailUniqueIdKey, mailItem.UniqueId.ToString());
        builder.AddArgument(Constants.ToastActionKey, MailOperation.Navigate.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);
        builder.AddButton(GetMarkAsReadButton(mailItem.UniqueId));
        builder.AddButton(GetDeleteButton(mailItem.UniqueId));
        builder.AddButton(GetArchiveButton(mailItem.UniqueId));
        builder.SetAudioUri(new Uri("ms-winsoundevent:Notification.Mail"));

        ShowNotification(builder, mailItem.UniqueId.ToString());
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

    private AppNotificationButton GetArchiveButton(Guid mailUniqueId)
        => new AppNotificationButton(Translator.MailOperation_Archive)
            .SetIcon(GetNotificationIconUri("mail-archive"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.Archive.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private AppNotificationButton GetDeleteButton(Guid mailUniqueId)
        => new AppNotificationButton(Translator.MailOperation_Delete)
            .SetIcon(GetNotificationIconUri("mail-delete"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.SoftDelete.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private AppNotificationButton GetMarkAsReadButton(Guid mailUniqueId)
        => new AppNotificationButton(Translator.MailOperation_MarkAsRead)
            .SetIcon(GetNotificationIconUri("mail-markread"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.MarkAsRead.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private static AppNotificationBuilder CreateBuilder(AppNotificationScenario scenario = AppNotificationScenario.Default)
        => new AppNotificationBuilder().SetScenario(scenario);

    private static void ShowNotification(AppNotificationBuilder builder, string? tag = null)
    {
        var notification = builder.BuildNotification();

        if (!string.IsNullOrWhiteSpace(tag))
        {
            notification.Tag = tag;
        }

        AppNotificationManager.Default.Show(notification);
    }

    private static Uri GetNotificationIconUri(string iconName)
        => new($"{NotificationIconRootUri}{iconName}.png");
}
