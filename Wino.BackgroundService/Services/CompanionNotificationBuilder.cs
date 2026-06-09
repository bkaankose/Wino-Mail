using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
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
using Wino.Core.Domain.Models.Messaging;
using Wino.Messaging.UI;

namespace Wino.BackgroundService.Services;

/// <summary>
/// INotificationBuilder for the companion process. Posts toasts through the system
/// Windows.UI.Notifications stack (no Windows App SDK) with the same argument strings
/// the UI's AppNotification-based builder produces, so activation handling is shared.
/// Jump list updates are forwarded to the UI process as a JumpListUpdateRequested event
/// because the jump list is per-application UI state.
/// </summary>
public class CompanionNotificationBuilder : INotificationBuilder
{
    private static int _calendarTaskbarBadgeCount;

    private static readonly MailOperation[] SupportedMailNotificationActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IMailService _mailService;
    private readonly IPreferencesService _preferencesService;

    public CompanionNotificationBuilder(IAccountService accountService,
                                        IFolderService folderService,
                                        IMailService mailService,
                                        IPreferencesService preferencesService)
    {
        _accountService = accountService;
        _folderService = folderService;
        _mailService = mailService;
        _preferencesService = preferencesService;

        WeakReferenceMessenger.Default.Register<MailReadStatusChanged>(this, (r, msg) => RemoveNotification(msg.UniqueId));
        WeakReferenceMessenger.Default.Register<BulkMailReadStatusChanged>(this, (r, msg) =>
        {
            foreach (var uniqueId in msg.UniqueIds)
            {
                RemoveNotification(uniqueId);
            }
        });
    }

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        try
        {
            var inboxMailItems = new List<MailCopy>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var item in newMailItems)
            {
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false);

                if (mailItem?.AssignedFolder != null &&
                    accounts.FirstOrDefault(a => a.Id == mailItem.AssignedFolder.MailAccountId)?.Preferences?.IsNotificationsEnabled == true)
                {
                    inboxMailItems.Add(mailItem);
                }
            }

            if (inboxMailItems.Count == 0)
                return;

            if (inboxMailItems.Count > 3)
            {
                var arguments = EncodeArguments(new Dictionary<string, string>
                {
                    [Constants.ToastModeKey] = Constants.ToastModeMail,
                });

                var xml = new ToastXmlBuilder(arguments)
                    .AddText(Translator.Notifications_MultipleNotificationsTitle)
                    .AddText(string.Format(Translator.Notifications_MultipleNotificationsMessage, inboxMailItems.Count))
                    .SetAudio("ms-winsoundevent:Notification.Mail")
                    .AddDismissButton()
                    .Build();

                ShowToast(xml);
            }
            else
            {
                foreach (var mailItem in inboxMailItems)
                {
                    CreateSingleMailNotification(mailItem);
                }
            }

            await UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to create companion mail notifications.");
        }
    }

    private void CreateSingleMailNotification(MailCopy mailItem)
    {
        var launchArguments = EncodeArguments(new Dictionary<string, string>
        {
            [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
            [Constants.ToastActionKey] = MailOperation.Navigate.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeMail,
        });

        var builder = new ToastXmlBuilder(launchArguments)
            .AddText(mailItem.FromName)
            .AddText(mailItem.Subject)
            .AddText(mailItem.PreviewText)
            .SetTimestamp(mailItem.CreationDate.ToLocalTime())
            .SetAudio("ms-winsoundevent:Notification.Mail");

        var (firstAction, secondAction) = GetConfiguredMailNotificationActions();

        builder.AddButton(GetOperationDisplayString(firstAction), EncodeArguments(new Dictionary<string, string>
        {
            [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
            [Constants.ToastActionKey] = firstAction.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeMail,
        }));

        builder.AddButton(GetOperationDisplayString(secondAction), EncodeArguments(new Dictionary<string, string>
        {
            [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
            [Constants.ToastActionKey] = secondAction.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeMail,
        }));

        builder.AddDismissButton();

        ShowToast(builder.Build(), mailItem.UniqueId.ToString());
    }

    public async Task UpdateTaskbarIconBadgeAsync()
    {
        var totalUnreadCount = 0;

        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var account in accounts)
            {
                if (!account.Preferences.IsTaskbarBadgeEnabled)
                    continue;

                var accountInbox = await _folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox).ConfigureAwait(false);
                if (accountInbox == null)
                    continue;

                totalUnreadCount += await _folderService.GetFolderNotificationBadgeAsync(accountInbox.Id).ConfigureAwait(false);
            }

            UpdateBadge(AppEntryConstants.MailApplicationId, totalUnreadCount > 0 ? totalUnreadCount : null);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Error while updating taskbar badge from companion.");
        }
    }

    public Task UpdateJumpListOptionsAsync()
    {
        // Jump lists belong to the UI app entry; ask the UI process to rebuild them.
        UIMessagePublisherProvider.Current.Publish(new JumpListUpdateRequested());
        return Task.CompletedTask;
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
        if (mailUniqueId == Guid.Empty)
            return;

        try
        {
            ToastNotificationManager.History.Remove(mailUniqueId.ToString());
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Failed to remove toast for mail {MailUniqueId}.", mailUniqueId);
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true)
            return;

        var arguments = EncodeArguments(new Dictionary<string, string>
        {
            [Constants.ToastMailAccountIdKey] = account.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeMail,
        });

        var xml = new ToastXmlBuilder(arguments)
            .AddText(Translator.Exception_AccountNeedsAttention_Title)
            .AddText(string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name))
            .AddButton(Translator.Buttons_FixAccount, arguments)
            .AddDismissButton()
            .Build();

        ShowToast(xml);
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
        // WebView2 validation is a UI process concern; never triggered in the companion.
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem == null)
            return Task.CompletedTask;

        var localStart = calendarItem.GetLocalStartDate();
        var reminderContext = GetCalendarReminderContext(localStart, DateTime.Now);

        var navigateArguments = EncodeArguments(new Dictionary<string, string>
        {
            [Constants.ToastCalendarActionKey] = Constants.ToastCalendarNavigateAction,
            [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeCalendar,
        });

        var builder = new ToastXmlBuilder(navigateArguments, scenario: "reminder")
            .AddText(calendarItem.Title)
            .AddText($"{reminderContext} - {localStart:g}")
            .SetAudio("ms-winsoundevent:Notification.Reminder");

        if (!string.IsNullOrWhiteSpace(calendarItem.Location))
            builder.AddText(calendarItem.Location);

        var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds,
            _preferencesService.DefaultReminderDurationInSeconds);

        if (allowedSnoozeMinutes.Count > 0)
        {
            var preferredSnoozeMinutes = _preferencesService.DefaultSnoozeDurationInMinutes;
            var defaultSnoozeMinutes = allowedSnoozeMinutes.Contains(preferredSnoozeMinutes)
                ? preferredSnoozeMinutes
                : allowedSnoozeMinutes[0];

            builder.AddSelectionInput(
                Constants.ToastCalendarSnoozeDurationInputId,
                defaultSnoozeMinutes.ToString(),
                allowedSnoozeMinutes.Select(minutes => (minutes.ToString(), string.Format(Translator.CalendarReminder_SnoozeMinutesOption, minutes))));

            builder.AddButton(Translator.CalendarReminder_SnoozeAction, EncodeArguments(new Dictionary<string, string>
            {
                [Constants.ToastCalendarActionKey] = Constants.ToastCalendarSnoozeAction,
                [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeCalendar,
            }));
        }

        builder.AddButton(Translator.Buttons_Open, navigateArguments);

        if (Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out _))
        {
            builder.AddButton(Translator.CalendarEventDetails_JoinOnline, EncodeArguments(new Dictionary<string, string>
            {
                [Constants.ToastCalendarActionKey] = Constants.ToastCalendarJoinOnlineAction,
                [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeCalendar,
            }));
        }

        builder.AddDismissButton();

        ShowToast(builder.Build(), $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}");

        return Task.CompletedTask;
    }

    private (MailOperation FirstAction, MailOperation SecondAction) GetConfiguredMailNotificationActions()
    {
        var firstAction = ResolveMailNotificationAction(_preferencesService.FirstMailNotificationAction, MailOperation.MarkAsRead);
        var secondAction = ResolveMailNotificationAction(_preferencesService.SecondMailNotificationAction, MailOperation.SoftDelete);

        if (secondAction == firstAction)
        {
            secondAction = SupportedMailNotificationActions.First(action => action != firstAction);
        }

        return (firstAction, secondAction);
    }

    private static MailOperation ResolveMailNotificationAction(MailOperation configuredAction, MailOperation fallbackAction)
        => SupportedMailNotificationActions.Contains(configuredAction) ? configuredAction : fallbackAction;

    private static string GetOperationDisplayString(MailOperation operation)
        => operation switch
        {
            MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
            MailOperation.SoftDelete => Translator.MailOperation_Delete,
            MailOperation.Archive => Translator.MailOperation_Archive,
            MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
            MailOperation.Reply => Translator.MailOperation_Reply,
            MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
            MailOperation.Forward => Translator.MailOperation_Forward,
            _ => operation.ToString()
        };

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

    private static string EncodeArguments(IReadOnlyDictionary<string, string> arguments)
        => string.Join(';', arguments.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));

    private static void UpdateBadge(string applicationId, int? badgeCount)
    {
        try
        {
            var badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication(applicationId);

            if (!badgeCount.HasValue || badgeCount.Value <= 0)
            {
                badgeUpdater.Clear();
                return;
            }

            var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
            if (badgeXml.SelectSingleNode("/badge") is not XmlElement badgeElement)
            {
                badgeUpdater.Clear();
                return;
            }

            badgeElement.SetAttribute("value", badgeCount.Value.ToString());
            badgeUpdater.Update(new BadgeNotification(badgeXml));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to update {ApplicationId} taskbar badge.", applicationId);
        }
    }

    private static void ShowToast(string toastXml, string? tag = null)
    {
        try
        {
            var document = new XmlDocument();
            document.LoadXml(toastXml);

            var toast = new ToastNotification(document);

            if (!string.IsNullOrWhiteSpace(tag))
            {
                toast.Tag = tag;
            }

            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to show companion toast.");
        }
    }

    /// <summary>
    /// Minimal toast XML writer for the schema subset the companion needs.
    /// </summary>
    private sealed class ToastXmlBuilder
    {
        private readonly string _launchArguments;
        private readonly string? _scenario;
        private readonly List<string> _texts = [];
        private readonly List<string> _actions = [];
        private string? _audioSource;
        private DateTimeOffset? _timestamp;

        public ToastXmlBuilder(string launchArguments, string? scenario = null)
        {
            _launchArguments = launchArguments;
            _scenario = scenario;
        }

        public ToastXmlBuilder AddText(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                _texts.Add($"<text>{SecurityElement.Escape(text)}</text>");

            return this;
        }

        public ToastXmlBuilder SetAudio(string source)
        {
            _audioSource = source;
            return this;
        }

        public ToastXmlBuilder SetTimestamp(DateTimeOffset timestamp)
        {
            _timestamp = timestamp;
            return this;
        }

        public ToastXmlBuilder AddButton(string content, string arguments)
        {
            _actions.Add($"<action content=\"{SecurityElement.Escape(content)}\" arguments=\"{SecurityElement.Escape(arguments)}\" activationType=\"foreground\"/>");
            return this;
        }

        public ToastXmlBuilder AddDismissButton()
        {
            _actions.Add($"<action content=\"{SecurityElement.Escape(Translator.Buttons_Dismiss)}\" arguments=\"{SecurityElement.Escape($"{Constants.ToastDismissActionKey}={bool.TrueString}")}\" activationType=\"foreground\"/>");
            return this;
        }

        public ToastXmlBuilder AddSelectionInput(string inputId, string defaultValue, IEnumerable<(string Id, string Content)> selections)
        {
            var selectionsXml = string.Concat(selections.Select(s => $"<selection id=\"{SecurityElement.Escape(s.Id)}\" content=\"{SecurityElement.Escape(s.Content)}\"/>"));
            _actions.Insert(0, $"<input id=\"{SecurityElement.Escape(inputId)}\" type=\"selection\" defaultInput=\"{SecurityElement.Escape(defaultValue)}\">{selectionsXml}</input>");
            return this;
        }

        public string Build()
        {
            var builder = new StringBuilder();
            builder.Append($"<toast launch=\"{SecurityElement.Escape(_launchArguments)}\" activationType=\"foreground\"");

            if (_scenario != null)
                builder.Append($" scenario=\"{_scenario}\"");

            if (_timestamp.HasValue)
                builder.Append($" displayTimestamp=\"{_timestamp.Value.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"");

            builder.Append("><visual><binding template=\"ToastGeneric\">");

            foreach (var text in _texts.Take(3))
            {
                builder.Append(text);
            }

            builder.Append("</binding></visual>");

            if (_actions.Count > 0)
            {
                builder.Append("<actions>");

                foreach (var action in _actions)
                {
                    builder.Append(action);
                }

                builder.Append("</actions>");
            }

            if (_audioSource != null)
                builder.Append($"<audio src=\"{_audioSource}\"/>");

            builder.Append("</toast>");

            return builder.ToString();
        }
    }
}
