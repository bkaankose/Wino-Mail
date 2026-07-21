using System.Net;
using Windows.Data.Xml.Dom;
using Windows.UI.StartScreen;
using Windows.UI.Notifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Companion.Notifications;

/// <summary>
/// UWP toast and badge owner. Every notification is emitted under the single Mail
/// AUMID even though this code runs in the packaged full-trust companion.
/// </summary>
public sealed class CompanionNotificationBuilder(
    IAccountService accountService,
    IFolderService folderService,
    IMailService mailService,
    IPreferencesService preferencesService) : INotificationBuilder
{
    private static int mailBadgeCount;
    private static int calendarBadgeCount;

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        foreach (var downloaded in newMailItems)
        {
            var mail = await mailService.GetSingleMailItemAsync(downloaded.UniqueId).ConfigureAwait(false);
            if (mail?.AssignedFolder is null)
            {
                continue;
            }

            var account = accounts.FirstOrDefault(candidate => candidate.Id == mail.AssignedFolder.MailAccountId);
            if (account?.Preferences?.IsNotificationsEnabled != true)
            {
                continue;
            }

            var launch = BuildArguments(
                (Constants.ToastModeKey, Constants.ToastModeMail),
                (Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString()),
                (Constants.ToastActionKey, MailOperation.Navigate.ToString()));
            var actions = GetMailActions(mail);
            ShowToast(CompanionAppIds.MailAumid, mail.FromName, mail.Subject, launch, mail.UniqueId.ToString(), actions: actions);
        }

        await UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
    }

    public async Task UpdateTaskbarIconBadgeAsync()
    {
        var unread = 0;
        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
        foreach (var account in accounts.Where(candidate => candidate.Preferences.IsTaskbarBadgeEnabled))
        {
            var inbox = await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox).ConfigureAwait(false);
            if (inbox is not null)
            {
                unread += await folderService.GetFolderNotificationBadgeAsync(inbox.Id).ConfigureAwait(false);
            }
        }

        Interlocked.Exchange(ref mailBadgeCount, unread);
        UpdateUnifiedBadge();
    }

    public async Task UpdateJumpListOptionsAsync()
    {
        if (!JumpList.IsSupported())
        {
            return;
        }

        try
        {
            var jumpList = await JumpList.LoadCurrentAsync();
            foreach (var removedItem in jumpList.Items.Where(item => item.RemovedByUser).ToArray())
            {
                if (TryGetJumpListFolderId(removedItem.Arguments, out var folderId))
                {
                    await folderService.ChangeFolderJumpListStateAsync(folderId, false).ConfigureAwait(false);
                }
            }

            jumpList.SystemGroupKind = JumpListSystemGroupKind.None;
            jumpList.Items.Clear();

            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
            foreach (var account in accounts.Where(account =>
                         account.IsMailAccessGranted && account.Preferences.IsJumpListEnabled))
            {
                var folders = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
                foreach (var folder in folders.Where(folder => folder.IsMoveTarget && folder.IsJumpListEnabled))
                {
                    var accountName = string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
                    var item = JumpListItem.CreateWithArguments(
                        CreateMailFolderJumpListArguments(account.Id, folder.Id),
                        $"{folder.FolderName} - {accountName}");
                    item.GroupName = Translator.JumpList_QuickFoldersGroup;
                    jumpList.Items.Add(item);
                }
            }

            await jumpList.SaveAsync();
        }
        catch
        {
            // JumpList can be unavailable during package update or shell restart. The next
            // synchronization/settings change will rebuild it from the database.
        }
    }

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount)
    {
        if (newlyDownloadedCount > 0)
        {
            Interlocked.Add(ref calendarBadgeCount, newlyDownloadedCount);
            UpdateUnifiedBadge();
        }

        return Task.CompletedTask;
    }

    public Task ClearCalendarTaskbarBadgeAsync()
    {
        Interlocked.Exchange(ref calendarBadgeCount, 0);
        UpdateUnifiedBadge();
        return Task.CompletedTask;
    }

    public void RemoveNotification(Guid mailUniqueId)
    {
        try
        {
            ToastNotificationManager.History.Remove(mailUniqueId.ToString(), string.Empty, CompanionAppIds.MailAumid);
        }
        catch
        {
            // A toast may already have expired or been removed by the user.
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true)
        {
            return;
        }

        var launch = BuildArguments(
            (Constants.ToastModeKey, Constants.ToastModeMail),
            (Constants.ToastMailAccountIdKey, account.Id.ToString()));
        ShowToast(
            CompanionAppIds.MailAumid,
            Translator.Exception_AccountNeedsAttention_Title,
            string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name),
            launch);
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
        // WebView availability belongs to the UWP UI and is not probed by the companion.
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem is null)
        {
            return Task.CompletedTask;
        }

        var launch = BuildArguments(
            (Constants.ToastModeKey, Constants.ToastModeCalendar),
            (Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction),
            (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString()));
        var actions = new List<ToastAction>();
        var inputs = new List<ToastSelectionInput>();
        var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds,
            preferencesService.DefaultReminderDurationInSeconds);

        if (allowedSnoozeMinutes.Count > 0)
        {
            var preferred = preferencesService.DefaultSnoozeDurationInMinutes;
            var selected = allowedSnoozeMinutes.Contains(preferred)
                ? preferred
                : allowedSnoozeMinutes[0];
            inputs.Add(new ToastSelectionInput(
                Constants.ToastCalendarSnoozeDurationInputId,
                selected.ToString(),
                allowedSnoozeMinutes
                    .Select(minutes => new ToastSelection(
                        minutes.ToString(),
                        string.Format(Translator.CalendarReminder_SnoozeMinutesOption, minutes)))
                    .ToArray()));
            actions.Add(new ToastAction(
                Translator.CalendarReminder_SnoozeAction,
                BuildArguments(
                    (Constants.ToastModeKey, Constants.ToastModeCalendar),
                    (Constants.ToastCalendarActionKey, Constants.ToastCalendarSnoozeAction),
                    (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())),
                true));
        }

        actions.Add(new ToastAction(Translator.Buttons_Open, launch, false));
        if (Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out _))
        {
            actions.Add(new ToastAction(
                Translator.CalendarEventDetails_JoinOnline,
                BuildArguments(
                    (Constants.ToastModeKey, Constants.ToastModeCalendar),
                    (Constants.ToastCalendarActionKey, Constants.ToastCalendarJoinOnlineAction),
                    (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())),
                true));
        }

        actions.Add(new ToastAction(
            Translator.Buttons_Dismiss,
            BuildArguments((Constants.ToastDismissActionKey, bool.TrueString)),
            true));
        ShowToast(
            CompanionAppIds.MailAumid,
            calendarItem.Title,
            $"{calendarItem.GetLocalStartDate():g} {calendarItem.Location}".Trim(),
            launch,
            $"cal-{calendarItem.Id:N}-{reminderDurationInSeconds}",
            isReminder: true,
            actions: actions,
            inputs: inputs);
        return Task.CompletedTask;
    }

    private static void ShowToast(
        string appUserModelId,
        string title,
        string body,
        string launch,
        string? tag = null,
        bool isReminder = false,
        IReadOnlyList<ToastAction>? actions = null,
        IReadOnlyList<ToastSelectionInput>? inputs = null)
    {
        var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
        var textNodes = xml.GetElementsByTagName("text");
        textNodes[0].AppendChild(xml.CreateTextNode(title ?? string.Empty));
        textNodes[1].AppendChild(xml.CreateTextNode(body ?? string.Empty));

        if (xml.DocumentElement is XmlElement root)
        {
            root.SetAttribute("launch", launch);
            if (isReminder)
            {
                root.SetAttribute("scenario", "reminder");
            }
        }

        if (actions is { Count: > 0 } && xml.DocumentElement is XmlElement toastRoot)
        {
            var actionsElement = xml.CreateElement("actions");
            if (inputs is { Count: > 0 })
            {
                foreach (var toastInput in inputs)
                {
                    var inputElement = xml.CreateElement("input");
                    inputElement.SetAttribute("id", toastInput.Id);
                    inputElement.SetAttribute("type", "selection");
                    inputElement.SetAttribute("defaultInput", toastInput.DefaultSelection);
                    foreach (var selection in toastInput.Selections)
                    {
                        var selectionElement = xml.CreateElement("selection");
                        selectionElement.SetAttribute("id", selection.Id);
                        selectionElement.SetAttribute("content", selection.Content);
                        inputElement.AppendChild(selectionElement);
                    }

                    actionsElement.AppendChild(inputElement);
                }
            }

            foreach (var toastAction in actions)
            {
                var actionElement = xml.CreateElement("action");
                actionElement.SetAttribute("content", toastAction.Content);
                actionElement.SetAttribute("arguments", toastAction.Arguments);
                actionElement.SetAttribute("activationType", toastAction.IsBackground ? "background" : "foreground");
                actionsElement.AppendChild(actionElement);
            }

            toastRoot.AppendChild(actionsElement);
        }

        var toast = new ToastNotification(xml);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            toast.Tag = tag.Length <= 64 ? tag : tag[..64];
        }

        ToastNotificationManager.CreateToastNotifier(appUserModelId).Show(toast);
    }

    private static void UpdateBadge(string appUserModelId, int count)
    {
        var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication(appUserModelId);
        if (count <= 0)
        {
            updater.Clear();
            return;
        }

        var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        if (xml.SelectSingleNode("/badge") is XmlElement badge)
        {
            badge.SetAttribute("value", count.ToString());
            updater.Update(new BadgeNotification(xml));
        }
    }

    private static void UpdateUnifiedBadge() =>
        UpdateBadge(
            CompanionAppIds.MailAumid,
            Math.Max(0, Volatile.Read(ref mailBadgeCount)) + Math.Max(0, Volatile.Read(ref calendarBadgeCount)));

    private static string BuildArguments(params (string Key, string Value)[] values) =>
        string.Join("&", values.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));

    private static string CreateMailFolderJumpListArguments(Guid accountId, Guid folderId) =>
        $"{CompanionAppIds.MailLaunchArgument};" + string.Join(';',
        [
            $"{WebUtility.UrlEncode(Constants.JumpListActionKey)}={WebUtility.UrlEncode(Constants.JumpListOpenMailFolderAction)}",
            $"{WebUtility.UrlEncode(Constants.JumpListAccountIdKey)}={WebUtility.UrlEncode(accountId.ToString())}",
            $"{WebUtility.UrlEncode(Constants.JumpListFolderIdKey)}={WebUtility.UrlEncode(folderId.ToString())}",
        ]);

    private static bool TryGetJumpListFolderId(string arguments, out Guid folderId)
    {
        folderId = Guid.Empty;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in (arguments ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[WebUtility.UrlDecode(segment[..separator])] = WebUtility.UrlDecode(segment[(separator + 1)..]);
        }

        return values.TryGetValue(Constants.JumpListActionKey, out var action) &&
               string.Equals(action, Constants.JumpListOpenMailFolderAction, StringComparison.Ordinal) &&
               values.TryGetValue(Constants.JumpListFolderIdKey, out var folderIdText) &&
               Guid.TryParse(folderIdText, out folderId);
    }

    private IReadOnlyList<ToastAction> GetMailActions(MailCopy mail)
    {
        var first = preferencesService.FirstMailNotificationAction;
        var second = preferencesService.SecondMailNotificationAction == first
            ? MailOperation.SoftDelete
            : preferencesService.SecondMailNotificationAction;
        return [CreateMailAction(first, mail.UniqueId), CreateMailAction(second, mail.UniqueId)];
    }

    private static ToastAction CreateMailAction(MailOperation operation, Guid mailId)
    {
        var arguments = BuildArguments(
            (Constants.ToastModeKey, Constants.ToastModeMail),
            (Constants.ToastMailUniqueIdKey, mailId.ToString()),
            (Constants.ToastActionKey, operation.ToString()));
        var background = operation is MailOperation.MarkAsRead or MailOperation.MarkAsUnread or
            MailOperation.SoftDelete or MailOperation.MoveToJunk or MailOperation.Archive;
        return new ToastAction(operation.ToString(), arguments, background);
    }

    private sealed record ToastAction(string Content, string Arguments, bool IsBackground);
    private sealed record ToastSelectionInput(string Id, string DefaultSelection, IReadOnlyList<ToastSelection> Selections);
    private sealed record ToastSelection(string Id, string Content);
}
