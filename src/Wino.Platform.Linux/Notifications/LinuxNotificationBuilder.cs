using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Notifications;
using Wino.Services.Interfaces;

namespace Wino.Platform.Linux.Notifications;

/// <summary>One notification as it is handed to GLib; kept in Debug builds so checks can read and answer it.</summary>
public sealed record LinuxNotificationRequest(
    string Id,
    string Title,
    string Body,
    string? IconPath,
    Gio.NotificationPriority Priority,
    string Category,
    LinuxNotificationButton[] Buttons,
    IReadOnlyDictionary<string, string> Values);

public sealed record LinuxNotificationButton(string Label, string ActionIdentifier);

/// <summary>
/// Desktop notifications through <c>GNotification</c> (Apple AppleNotificationBuilder and Windows NotificationBuilder
/// port): the shared policy gate, mail content levels, a summary above the individual maximum, hidden-content
/// accounts, the two configured mail actions, calendar reminders with Join and Snooze, attention and test
/// notifications. Every button and the body target <see cref="LinuxNotificationActions.ActionName"/>; the head turns
/// the click into a <see cref="NotificationResponse"/>. GLib delivers over org.gtk.Notifications, org.freedesktop.Notifications
/// or the portal; the shell lists them under the app only when the installed <c>.desktop</c> file is named after the
/// application id. A GNotification reports no dismissal, so Dismiss exists only as a button. No badge, no jump list.
/// </summary>
public sealed class LinuxNotificationBuilder : INotificationBuilder
{
    private const string MailCategory = "wino.mail";
    private const string MailSummaryCategory = "wino.mail.summary";
    private const string MailHiddenCategory = "wino.mail.hidden";
    private const string CalendarCategory = "wino.calendar";
    private const string GenericCategory = "wino.generic";

    private readonly IDispatcher _dispatcher;
    private readonly IPreferencesService _preferences;
    private readonly IAccountService _accountService;
    private readonly IMailService _mailService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IPictureStorageService _pictureStorageService;
    private readonly INotificationPolicyService _policy;
    private readonly IAppLockService _appLock;
    private readonly LinuxNotificationSounds _sounds;

    public LinuxNotificationBuilder(IDispatcher dispatcher, IPreferencesService preferences, IAccountService accountService, IMailService mailService,
        IThumbnailService thumbnailService, IPictureStorageService pictureStorageService, INotificationPolicyService policy, IAppLockService appLock,
        LinuxNotificationSounds sounds)
    {
        _dispatcher = dispatcher;
        _preferences = preferences;
        _accountService = accountService;
        _mailService = mailService;
        _thumbnailService = thumbnailService;
        _pictureStorageService = pictureStorageService;
        _policy = policy;
        _appLock = appLock;
        _sounds = sounds;
        WeakReferenceMessenger.Default.Register<LinuxNotificationBuilder, MailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications([msg.UniqueId]));
        WeakReferenceMessenger.Default.Register<LinuxNotificationBuilder, BulkMailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications(msg.UniqueIds));
    }

#if DEBUG
    private readonly List<LinuxNotificationRequest> _sent = [];

    /// <summary>Every notification sent in this run, oldest first (main thread).</summary>
    public IReadOnlyList<LinuxNotificationRequest> Sent => _sent;

    public List<string> Withdrawn { get; } = [];
#endif

    #region Delivery

    /// <summary>The single gate every notification passes (Windows ShowNotificationAsync): snooze, quiet hours, switches, account overrides.</summary>
    private Task ShowAsync(LinuxNotificationRequest request, NotificationKind kind, MailAccountPreferences? accountPreferences = null)
    {
        if (!_policy.Evaluate(kind, accountPreferences, DateTimeOffset.Now).ShouldDeliver)
        {
            Serilog.Log.Debug("Notification {Id} suppressed by the notification policy.", request.Id);
            return Task.CompletedTask;
        }
        _sounds.PlayForNotification(Sound(kind, accountPreferences), SoundKind(kind));
        return _dispatcher.ExecuteOnUIThread(() => Send(request));
    }

    private string Sound(NotificationKind kind, MailAccountPreferences? accountPreferences) => kind switch
    {
        NotificationKind.Mail => _sounds.ResolveMail(accountPreferences),
        NotificationKind.CalendarReminder => _sounds.Resolve(NotificationSoundKind.Calendar),
        NotificationKind.TaskReminder => _sounds.Resolve(NotificationSoundKind.Task),
        _ => LinuxNotificationSounds.DefaultSound,
    };

    private static NotificationSoundKind? SoundKind(NotificationKind kind) => kind switch
    {
        NotificationKind.Mail => NotificationSoundKind.Mail,
        NotificationKind.CalendarReminder => NotificationSoundKind.Calendar,
        NotificationKind.TaskReminder => NotificationSoundKind.Task,
        _ => null,
    };

    private void Send(LinuxNotificationRequest request)
    {
        if (Gio.Application.GetDefault() is not { } application) return;
        var notification = Gio.Notification.New(request.Title);
        if (request.Body.Length > 0) notification.SetBody(request.Body);
        notification.SetPriority(request.Priority);
        notification.SetIcon(Icon(request.IconPath));
        notification.SetDefaultActionAndTarget(LinuxNotificationActions.DetailedName,
            LinuxNotificationActions.Encode(NotificationResponseKind.Default, string.Empty, request.Category, request.Values));
        foreach (var button in request.Buttons)
        {
            notification.AddButtonWithTarget(button.Label, LinuxNotificationActions.DetailedName,
                LinuxNotificationActions.Encode(NotificationResponseKind.Action, button.ActionIdentifier, request.Category, request.Values));
        }
        application.SendNotification(request.Id, notification);
#if DEBUG
        _sent.Add(request);
#endif
        Serilog.Log.Debug("Notification {Id} sent ({Category}, {Count} buttons).", request.Id, request.Category, request.Buttons.Length);
    }

    private static Gio.Icon Icon(string? path)
    {
        if (path is not null && File.Exists(path)) return Gio.FileIcon.New(Gio.FileHelper.NewForPath(path));
        return Gio.ThemedIcon.New("app.winomail.Mail");
    }

    private void Withdraw(string id) => _ = _dispatcher.ExecuteOnUIThread(() =>
    {
        Gio.Application.GetDefault()?.WithdrawNotification(id);
#if DEBUG
        Withdrawn.Add(id);
#endif
    });

    private static void Observe(Task task, string what)
        => _ = task.ContinueWith(t => Serilog.Log.Error(t.Exception, "Failed to show the {What} notification.", what),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string Join(string? first, string? second)
        => string.IsNullOrWhiteSpace(first) ? second ?? string.Empty
            : string.IsNullOrWhiteSpace(second) ? first : first + "\n" + second;

    private static LinuxNotificationButton Dismiss() => new(Translator.Buttons_Dismiss, NotificationActionIdentifiers.Dismiss);

    private static LinuxNotificationButton Open() => new(Translator.Buttons_Open, NotificationActionIdentifiers.Open);

    #endregion

    #region Mail

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        try
        {
            var notifiable = new List<(MailCopy MailItem, MailAccountPreferences? Preferences)>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            foreach (var item in newMailItems ?? [])
            {
                if (item is null) continue;
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false);
                if (mailItem?.AssignedFolder is null) continue;
                var account = accounts.FirstOrDefault(account => account.Id == mailItem.AssignedFolder.MailAccountId);
                var settings = NotificationSettingsResolver.ResolveMail(_preferences, account?.Preferences);
                if (settings.IsEnabled && NotificationContentRules.IsWithinNotificationScope(mailItem, settings.Scope))
                    notifiable.Add((mailItem, account?.Preferences));
            }
            if (notifiable.Count == 0) return;

            // Hidden-content accounts are announced by count, one notification each, and never join the summary.
            var visible = notifiable.Where(item => !NotificationSettingsResolver.HidesNotificationContent(item.Preferences)).ToList();
            var hiddenGroups = notifiable.Where(item => NotificationSettingsResolver.HidesNotificationContent(item.Preferences))
                .GroupBy(item => item.Preferences?.AccountId ?? Guid.Empty).ToList();

            if (visible.Count > NotificationContentRules.MaxIndividualMailNotifications)
            {
                var summary = NotificationContentRules.ComposeSummaryText(visible.Count);
                await ShowAsync(new LinuxNotificationRequest("mail-summary-" + Guid.NewGuid().ToString("N"), summary.Title, Join(summary.Subtitle, summary.Body),
                    null, Gio.NotificationPriority.Normal, MailSummaryCategory, [Dismiss()],
                    new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail }), NotificationKind.Mail).ConfigureAwait(false);
            }
            else
            {
                foreach (var (mailItem, accountPreferences) in visible) await CreateSingleNotificationAsync(mailItem, accountPreferences).ConfigureAwait(false);
            }

            foreach (var hiddenGroup in hiddenGroups)
            {
                var hiddenItems = hiddenGroup.ToList();
                var (firstMailItem, accountPreferences) = hiddenItems[0];
                if (hiddenItems.Count == 1)
                {
                    await CreateSingleNotificationAsync(firstMailItem, accountPreferences).ConfigureAwait(false);
                    continue;
                }
                await ShowAsync(new LinuxNotificationRequest("mail-hidden-" + Guid.NewGuid().ToString("N"), NotificationContentRules.HiddenMailTitle,
                    NotificationContentRules.ComposeHiddenMailText(hiddenItems.Count), null, Gio.NotificationPriority.Normal, MailSummaryCategory, [Dismiss()],
                    new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail }), NotificationKind.Mail, accountPreferences).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to create notifications.");
        }
    }

    public async Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems)
    {
        try
        {
            foreach (var mailItem in mailItems ?? [])
            {
                if (mailItem is not null) await CreateSingleNotificationAsync(mailItem).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to create test notifications.");
        }
    }

    private async Task CreateSingleNotificationAsync(MailCopy mailItem, MailAccountPreferences? accountPreferences = null)
    {
        var settings = NotificationSettingsResolver.ResolveMail(_preferences, accountPreferences);
        var hidesContent = NotificationSettingsResolver.HidesNotificationContent(accountPreferences);
        string? imagePath = null;
        if (!hidesContent && NotificationContentRules.ShowsSenderPicture(settings.Content))
        {
            imagePath = mailItem.SenderContact?.ContactPictureFileId is { } fileId ? _pictureStorageService.GetPicturePath(PictureKind.Contact, fileId) : null;
            if (imagePath is null && !string.IsNullOrWhiteSpace(mailItem.FromAddress))
                imagePath = (await _thumbnailService.GetThumbnailAsync(mailItem.FromAddress, awaitLoad: true).ConfigureAwait(false))?.FilePath;
        }

        var text = hidesContent ? NotificationContentRules.ComposeHiddenMail(1) : NotificationContentRules.ComposeMailText(mailItem, settings.Content);

        // Reply, Reply all and Forward open the message, so a hidden notification drops them; read-only notifications
        // (app lock) offer no action that works without unlocking.
        var (first, second) = NotificationContentRules.ResolveMailActions(_preferences.FirstMailNotificationAction, _preferences.SecondMailNotificationAction);
        MailOperation[] operations = _appLock.AreNotificationsReadOnly ? [] : [first, second];
        var buttons = operations.Where(operation => !hidesContent || !NotificationContentRules.IsReplyAction(operation))
            .Select(operation => new LinuxNotificationButton(NotificationContentRules.MailActionTitle(operation), NotificationActionIdentifiers.MailAction(operation)))
            .Append(Dismiss()).ToArray();

        await ShowAsync(new LinuxNotificationRequest(MailId(mailItem.UniqueId), text.Title, Join(text.Subtitle, text.Body), imagePath, Gio.NotificationPriority.Normal,
            hidesContent ? MailHiddenCategory : MailCategory, buttons,
            new Dictionary<string, string>
            {
                [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
                [Constants.ToastActionKey] = MailOperation.Navigate.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail,
            }), NotificationKind.Mail, accountPreferences).ConfigureAwait(false);
    }

    public static string MailId(Guid uniqueId) => "mail-" + uniqueId.ToString("N");

    public void RemoveNotification(Guid mailUniqueId) => RemoveNotifications([mailUniqueId]);

    private void RemoveNotifications(IEnumerable<Guid>? mailUniqueIds)
    {
        foreach (var id in mailUniqueIds?.Where(id => id != Guid.Empty).Distinct() ?? []) Withdraw(MailId(id));
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true) return;
        Observe(ShowAsync(new LinuxNotificationRequest($"account-attention-{account.Id:N}", Translator.Exception_AccountNeedsAttention_Title,
            string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name), null, Gio.NotificationPriority.High, GenericCategory, [Open(), Dismiss()],
            new Dictionary<string, string>
            {
                [Constants.ToastMailAccountIdKey] = account.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail,
            }), NotificationKind.Other), "account attention");
    }

    public void CreateWebView2RuntimeMissingNotification() { }

    #endregion

    #region Calendar, people and tasks

    public async Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem is null) return;
        MailAccountPreferences? accountPreferences = null;
        if (calendarItem.AssignedCalendar?.AccountId is { } accountId)
            accountPreferences = (await _accountService.GetAccountAsync(accountId).ConfigureAwait(false))?.Preferences;

        var hidesContent = NotificationSettingsResolver.HidesNotificationContent(accountPreferences);
        var title = hidesContent ? NotificationContentRules.HiddenCalendarReminderTitle : calendarItem.Title ?? string.Empty;
        var body = hidesContent
            ? NotificationContentRules.ComposeHiddenCalendarReminderText()
            : NotificationContentRules.ComposeCalendarReminderBody(calendarItem, DateTime.Now);
        var snoozeMinutes = NotificationContentRules.ResolveReminderSnoozeMinutes(
            reminderDurationInSeconds, _preferences.DefaultReminderDurationInSeconds, _preferences.DefaultSnoozeDurationInMinutes);
        // A join link would reveal the meeting, so a hidden reminder offers Open instead.
        var canJoin = !hidesContent && CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _);
        var readOnly = _appLock.AreNotificationsReadOnly;
        var values = new Dictionary<string, string>
        {
            [Constants.ToastCalendarActionKey] = Constants.ToastCalendarNavigateAction,
            [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeCalendar,
        };
        if (snoozeMinutes is { } minutes) values[Constants.ToastCalendarSnoozeDurationInputId] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var buttons = new List<LinuxNotificationButton> { Open() };
        if (canJoin) buttons.Add(new LinuxNotificationButton(Translator.CalendarEventDetails_JoinOnline, NotificationActionIdentifiers.CalendarJoin));
        if (snoozeMinutes is { } snooze && !readOnly) buttons.Add(new LinuxNotificationButton(NotificationContentRules.SnoozeTitle(snooze), NotificationActionIdentifiers.CalendarSnooze));
        buttons.Add(Dismiss());

        await ShowAsync(new LinuxNotificationRequest($"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}", title, body, null,
            Gio.NotificationPriority.High, CalendarCategory, [.. buttons], values), NotificationKind.CalendarReminder, accountPreferences).ConfigureAwait(false);
    }

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem)
        => CreateCalendarReminderNotificationAsync(calendarItem, Math.Max(_preferences.DefaultReminderDurationInSeconds, (long)TimeSpan.FromMinutes(30).TotalSeconds));

    public Task CreateTestPeopleNotificationAsync(AccountContact contact)
    {
        if (contact is null) return Task.CompletedTask;
        var secondary = contact.PrimaryEmailAddress ?? contact.PrimaryPhoneNumber;
        return ShowAsync(new LinuxNotificationRequest($"people-test-{contact.Id:N}",
            string.IsNullOrWhiteSpace(contact.DisplayValue) ? Translator.Buttons_TestNotification : contact.DisplayValue,
            string.IsNullOrWhiteSpace(secondary) ? Translator.Buttons_TestNotification : secondary,
            contact.ContactPictureFileId is { } pictureId ? _pictureStorageService.GetPicturePath(PictureKind.Contact, pictureId) : null,
            Gio.NotificationPriority.Normal, GenericCategory, [Open(), Dismiss()],
            new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModePeople }), NotificationKind.Other);
    }

    public async Task CreateTaskReminderNotificationAsync(AccountTask task)
    {
        if (task is null) return;
        var accountPreferences = (await _accountService.GetAccountAsync(task.MailAccountId).ConfigureAwait(false))?.Preferences;
        var hidesContent = NotificationSettingsResolver.HidesNotificationContent(accountPreferences);
        var title = hidesContent
            ? Translator.TaskReminder_HiddenTitle
            : string.IsNullOrWhiteSpace(task.Title) ? Translator.ToDoPage_NewTask : task.Title;
        var body = !hidesContent && task.DueDate is { } dueDate ? string.Format(Translator.ToDoPage_DueOn, dueDate.ToString("D")) : string.Empty;
        var values = new Dictionary<string, string>
        {
            [Constants.ToastModeKey] = Constants.ToastModeTasks,
            [Constants.ToastTaskIdKey] = task.Id.ToString(),
            [Constants.ToastTaskActionKey] = Constants.ToastTaskOpenAction,
        };

        // Read-only notifications offer no action that changes the task, as the calendar reminder drops Snooze.
        var buttons = new List<LinuxNotificationButton>();
        if (!_appLock.AreNotificationsReadOnly)
        {
            buttons.Add(new LinuxNotificationButton(Translator.ToDoPage_MarkCompleted, NotificationActionIdentifiers.TaskComplete));
            buttons.Add(new LinuxNotificationButton(Translator.CalendarReminder_SnoozeAction, NotificationActionIdentifiers.TaskSnooze));
        }
        buttons.Add(Open());
        buttons.Add(Dismiss());

        await ShowAsync(new LinuxNotificationRequest($"task-reminder-{task.Id:N}", title, body, null,
            Gio.NotificationPriority.High, GenericCategory, [.. buttons], values), NotificationKind.TaskReminder, accountPreferences).ConfigureAwait(false);
    }

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)
    {
        if (task is null) return Task.CompletedTask;
        return ShowAsync(new LinuxNotificationRequest($"task-test-{task.Id:N}",
            string.IsNullOrWhiteSpace(task.Title) ? Translator.Buttons_TestNotification : task.Title,
            task.DueDate is { } dueDate ? dueDate.ToString("D") : Translator.Buttons_TestNotification, null,
            Gio.NotificationPriority.Normal, GenericCategory, [Open(), Dismiss()],
            new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeTasks }), NotificationKind.TaskReminder);
    }

    public Task CreateLiveNotificationsTestNotificationAsync()
        => ShowAsync(new LinuxNotificationRequest("live-notifications-test", Translator.NotificationSettings_PushRelay_Title,
            Translator.NotificationSettings_PushRelay_TestReceived, null, Gio.NotificationPriority.Normal, GenericCategory, [Dismiss()],
            new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail }), NotificationKind.Other);

    #endregion

    public Task UpdateTaskbarIconBadgeAsync() => Task.CompletedTask;

    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;

    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;
}
