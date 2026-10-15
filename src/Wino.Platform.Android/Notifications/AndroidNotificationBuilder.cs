using Android.App;
using Android.Content;
using Android.Graphics;
using AndroidX.Core.App;
using RemoteInput = AndroidX.Core.App.RemoteInput;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Notifications;
using Wino.Messaging.UI;

namespace Wino.Platform.Android.Notifications;

/// <summary>
/// Android port of the notification builder (Windows NotificationBuilder, AppleNotificationBuilder): the shared policy
/// gate (<see cref="INotificationPolicyService"/>: snooze, quiet hours, per-type and per-account switches), the shared
/// content rules (<see cref="NotificationContentRules"/>: scope, privacy level, summary threshold, configured actions,
/// reminder text) over NotificationCompat. Mail goes to the account's channel and is grouped per account; actions go
/// to the head's receiver, taps to the head's activity, both carrying the shared payload keys. The launcher badge is
/// the notification count: mail notifications carry the unread total as their number.
/// </summary>
public sealed class AndroidNotificationBuilder : INotificationBuilder
{
    private const int MailId = 1;
    private const int MailSummaryId = 2;
    private const int GroupSummaryId = 3;
    private const int CalendarId = 4;
    private const int GenericId = 5;

    private readonly Context _context;
    private readonly AndroidNotificationComponents _components;
    private readonly IPreferencesService _preferences;
    private readonly IAccountService _accountService;
    private readonly IUnreadBadgeService _unreadBadgeService;
    private readonly IMailService _mailService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IPictureStorageService _pictureStorageService;
    private readonly INotificationPolicyService _policy;
    private readonly AndroidReminderState _reminderState;
    private int _badgeCount;

    public AndroidNotificationBuilder(Context context,
                                      AndroidNotificationComponents components,
                                      IPreferencesService preferences,
                                      IAccountService accountService,
                                      IUnreadBadgeService unreadBadgeService,
                                      IMailService mailService,
                                      IThumbnailService thumbnailService,
                                      IPictureStorageService pictureStorageService,
                                      INotificationPolicyService policy,
                                      AndroidReminderState reminderState)
    {
        _context = context;
        _components = components;
        _preferences = preferences;
        _accountService = accountService;
        _unreadBadgeService = unreadBadgeService;
        _mailService = mailService;
        _thumbnailService = thumbnailService;
        _pictureStorageService = pictureStorageService;
        _policy = policy;
        _reminderState = reminderState;

        WeakReferenceMessenger.Default.Register<AndroidNotificationBuilder, MailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications([msg.UniqueId]));
        WeakReferenceMessenger.Default.Register<AndroidNotificationBuilder, BulkMailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications(msg.UniqueIds));
        WeakReferenceMessenger.Default.Register<AndroidNotificationBuilder, BulkMailUpdatedMessage>(this, (r, msg) =>
        {
            if (msg.Source == EntityUpdateSource.Server &&
                (msg.ChangedProperties & (MailCopyChangeFlags.IsRead | MailCopyChangeFlags.FolderId)) != 0)
            {
                _ = r.UpdateTaskbarIconBadgeAsync();
            }
        });
    }

    /// <summary>The unread total the launcher badge shows, for diagnostics.</summary>
    public int BadgeCount => _badgeCount;

    /// <summary>Creates the channels for the current accounts (called when services are ready and after account changes).</summary>
    public async Task EnsureChannelsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        AndroidNotificationChannels.Ensure(_context, accounts);
    }

    private NotificationManagerCompat Manager => NotificationManagerCompat.From(_context);

    #region Delivery

    private sealed record Request(
        string Tag,
        int Id,
        string Channel,
        string Category,
        string Title,
        string? Subtitle,
        string? Body,
        IReadOnlyDictionary<string, string> Values,
        int SmallIcon,
        string? Group = null,
        string? ImagePath = null,
        string? AndroidCategory = null,
        IReadOnlyList<Action>? Actions = null,
        string? SubText = null);

    private sealed record Action(string Identifier, string Title, ActionTarget Target);

    private enum ActionTarget { Background, Reply, Activity, Dismiss }

    /// <summary>The single gate every notification passes through (Windows ShowNotificationAsync).</summary>
    private bool ShouldDeliver(NotificationKind kind, MailAccountPreferences? accountPreferences, string what)
    {
        if (!_policy.Evaluate(kind, accountPreferences, DateTimeOffset.Now).ShouldDeliver) return false;
        if (!Manager.AreNotificationsEnabled())
        {
            Serilog.Log.Debug("Notification {What} dropped: notifications are not allowed.", what);
            return false;
        }
        return true;
    }

    private void Post(Request request)
    {
        var identity = new AndroidNotificationIntents.NotificationIdentity(request.Tag, request.Id);
        var builder = new NotificationCompat.Builder(_context, request.Channel)
            .SetSmallIcon(request.SmallIcon)
            .SetContentTitle(request.Title)
            .SetAutoCancel(true)
            // Below API 26 the priority stands in for the channel importance (mail per account default, the rest high).
            .SetPriority(AndroidNotificationChannels.IsMail(request.Channel)
                ? NotificationCompat.PriorityDefault : NotificationCompat.PriorityHigh)
            .SetShowWhen(true)
            .SetContentIntent(AndroidNotificationIntents.CreateContentIntent(_context, _components, identity, request.Category, request.Values));

        var text = string.IsNullOrWhiteSpace(request.Subtitle) ? request.Body : request.Subtitle;
        if (!string.IsNullOrWhiteSpace(text)) builder.SetContentText(text);
        var expanded = string.Join("\n", new[] { request.Subtitle, request.Body }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(expanded)) builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(expanded));
        // The header line names the account: "Wino Mail · address · time" (canvas Notification board).
        if (!string.IsNullOrWhiteSpace(request.SubText)) builder.SetSubText(request.SubText);
        if (request.AndroidCategory is { } category) builder.SetCategory(category);
        if (request.Group is { } group) builder.SetGroup(group);
        if (request.ImagePath is { } imagePath && LoadPicture(imagePath) is { } picture) builder.SetLargeIcon(picture);
        if (request.Channel.StartsWith(AndroidNotificationChannels.MailPrefix, StringComparison.Ordinal) && _badgeCount > 0)
            builder.SetNumber(_badgeCount);

        foreach (var action in request.Actions ?? [])
            builder.AddAction(CreateAction(identity, request, action));

        Manager.Notify(request.Tag, request.Id, builder.Build());
    }

    private NotificationCompat.Action CreateAction(AndroidNotificationIntents.NotificationIdentity identity, Request request, Action action)
    {
        switch (action.Target)
        {
            case ActionTarget.Reply:
            {
                var intent = AndroidNotificationIntents.CreateBroadcastIntent(_context, _components, identity, request.Category, request.Values, action.Identifier, acceptsText: true);
                var input = new RemoteInput.Builder(AndroidNotificationIntents.ReplyInputKey).SetLabel(Translator.MailNotification_ReplyPlaceholder).Build();
                return new NotificationCompat.Action.Builder(0, action.Title, intent).AddRemoteInput(input).SetAllowGeneratedReplies(false).Build();
            }
            case ActionTarget.Activity:
                return new NotificationCompat.Action.Builder(0, action.Title,
                    AndroidNotificationIntents.CreateActivityIntent(_context, _components, identity, request.Category, request.Values, action.Identifier)).Build();
            case ActionTarget.Dismiss:
                return new NotificationCompat.Action.Builder(0, action.Title,
                    AndroidNotificationIntents.CreateBroadcastIntent(_context, _components, identity, request.Category, request.Values, action.Identifier,
                        kind: NotificationResponseKind.Dismiss)).Build();
            default:
                return new NotificationCompat.Action.Builder(0, action.Title,
                    AndroidNotificationIntents.CreateBroadcastIntent(_context, _components, identity, request.Category, request.Values, action.Identifier)).Build();
        }
    }

    private static Bitmap? LoadPicture(string path)
    {
        try { return File.Exists(path) ? BitmapFactory.DecodeFile(path) : null; }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "Could not load a notification picture.");
            return null;
        }
    }

    private static void Observe(Task task, string what)
        => _ = task.ContinueWith(t => Serilog.Log.Error(t.Exception, "Failed to show the {What} notification.", what),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    #endregion

    #region Mail

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> newMailItems)
    {
        try
        {
            var notifiable = new List<(MailCopy MailItem, MailAccount? Account)>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var item in newMailItems)
            {
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false);
                if (mailItem?.AssignedFolder is null) continue;
                var account = accounts.FirstOrDefault(account => account.Id == mailItem.AssignedFolder.MailAccountId);
                var settings = NotificationSettingsResolver.ResolveMail(_preferences, account?.Preferences);
                if (settings.IsEnabled && NotificationContentRules.IsWithinNotificationScope(mailItem, settings.Scope))
                    notifiable.Add((mailItem, account));
            }

            if (notifiable.Count == 0) return;
            await RefreshBadgeCountAsync().ConfigureAwait(false);

            if (notifiable.Count > NotificationContentRules.MaxIndividualMailNotifications)
            {
                if (!ShouldDeliver(NotificationKind.Mail, null, "mail summary")) return;
                var summary = NotificationContentRules.ComposeSummaryText(notifiable.Count);
                Post(new Request(
                    Tag: "mail-summary",
                    Id: MailSummaryId,
                    Channel: AndroidNotificationChannels.MailSummary,
                    Category: "wino.mail.summary",
                    Title: summary.Title,
                    Subtitle: null,
                    Body: summary.Body,
                    Values: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail },
                    SmallIcon: Resource.Drawable.wino_notification_mail,
                    AndroidCategory: NotificationCompat.CategoryEmail));
            }
            else
            {
                foreach (var (mailItem, account) in notifiable)
                    await CreateSingleNotificationAsync(mailItem, account).ConfigureAwait(false);
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
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            await RefreshBadgeCountAsync().ConfigureAwait(false);
            foreach (var item in mailItems)
            {
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false) ?? item;
                var account = accounts.FirstOrDefault(account => account.Id == mailItem.AssignedFolder?.MailAccountId);
                await CreateSingleNotificationAsync(mailItem, account).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to create test notifications.");
        }
    }

    private async Task CreateSingleNotificationAsync(MailCopy mailItem, MailAccount? account)
    {
        var accountPreferences = account?.Preferences;
        if (!ShouldDeliver(NotificationKind.Mail, accountPreferences, "mail")) return;

        var settings = NotificationSettingsResolver.ResolveMail(_preferences, accountPreferences);
        string? imagePath = null;
        if (NotificationContentRules.ShowsSenderPicture(settings.Content))
        {
            imagePath = mailItem.SenderContact?.ContactPictureFileId is { } fileId
                ? _pictureStorageService.GetPicturePath(PictureKind.Contact, fileId)
                : null;
            if (imagePath is null && !string.IsNullOrWhiteSpace(mailItem.FromAddress))
                imagePath = (await _thumbnailService.GetThumbnailAsync(mailItem.FromAddress, awaitLoad: true).ConfigureAwait(false))?.FilePath;
        }

        var text = NotificationContentRules.ComposeMailText(mailItem, settings.Content);
        string channel = AndroidNotificationChannels.MailSummary;
        string? group = null;
        if (account is not null)
        {
            AndroidNotificationChannels.EnsureAccount(_context, account);
            channel = AndroidNotificationChannels.ForAccount(account.Id);
            group = channel;
        }

        var (first, second) = NotificationContentRules.ResolveMailActions(_preferences.FirstMailNotificationAction, _preferences.SecondMailNotificationAction);
        Post(new Request(
            Tag: mailItem.UniqueId.ToString(),
            Id: MailId,
            Channel: channel,
            Category: "wino.mail",
            Title: text.Title,
            Subtitle: text.Subtitle,
            Body: text.Body,
            Values: new Dictionary<string, string>
            {
                [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
                [Constants.ToastActionKey] = MailOperation.Navigate.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail
            },
            SmallIcon: Resource.Drawable.wino_notification_mail,
            Group: group,
            ImagePath: imagePath,
            AndroidCategory: NotificationCompat.CategoryEmail,
            Actions: [MailAction(first), MailAction(second)],
            SubText: account?.Address));

        if (account is not null) PostGroupSummary(account);
    }

    private static Action MailAction(MailOperation operation)
    {
        var identifier = NotificationActionIdentifiers.MailAction(operation);
        var title = NotificationContentRules.MailActionTitle(operation);
        return operation switch
        {
            // Reply answers inline; reply all and forward open the composer.
            MailOperation.Reply => new Action(identifier, title, ActionTarget.Reply),
            MailOperation.ReplyAll or MailOperation.Forward => new Action(identifier, title, ActionTarget.Activity),
            _ => new Action(identifier, title, ActionTarget.Background)
        };
    }

    /// <summary>The per-account group summary (required for grouping on API 24+): the account and its new mail senders.</summary>
    private void PostGroupSummary(MailAccount account)
    {
        var group = AndroidNotificationChannels.ForAccount(account.Id);
        var children = ActiveInGroup(group, excludeSummary: true);
        if (children.Count == 0)
        {
            Manager.Cancel(group, GroupSummaryId);
            return;
        }

        var name = string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? string.Empty : account.Name;
        var style = new NotificationCompat.InboxStyle().SetSummaryText(name);
        foreach (var title in children) style.AddLine(title);
        var identity = new AndroidNotificationIntents.NotificationIdentity(group, GroupSummaryId);
        var values = new Dictionary<string, string>
        {
            [Constants.ToastMailAccountIdKey] = account.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeMail
        };
        var builder = new NotificationCompat.Builder(_context, group)
            .SetSmallIcon(Resource.Drawable.wino_notification_mail)
            .SetContentTitle(name)
            .SetContentText(string.Format(Translator.Notifications_MultipleNotificationsMessage, children.Count))
            .SetStyle(style)
            .SetGroup(group)
            .SetGroupSummary(true)
            .SetGroupAlertBehavior(NotificationCompat.GroupAlertChildren)
            .SetAutoCancel(true)
            .SetCategory(NotificationCompat.CategoryEmail)
            .SetContentIntent(AndroidNotificationIntents.CreateContentIntent(_context, _components, identity, "wino.mail.group", values));
        if (_badgeCount > 0) builder.SetNumber(_badgeCount);
        Manager.Notify(group, GroupSummaryId, builder.Build());
    }

    /// <summary>Titles of the posted notifications in <paramref name="group"/>.</summary>
    private List<string> ActiveInGroup(string group, bool excludeSummary)
    {
        var titles = new List<string>();
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return titles;
        foreach (var posted in manager.GetActiveNotifications() ?? [])
        {
            var notification = posted.Notification;
            if (notification is null || notification.Group != group) continue;
            if (excludeSummary && (notification.Flags & NotificationFlags.GroupSummary) != 0) continue;
            titles.Add(notification.Extras?.GetCharSequence(NotificationCompat.ExtraTitle)?.ToString() ?? string.Empty);
        }
        return titles;
    }

    public void RemoveNotification(Guid mailUniqueId) => RemoveNotifications([mailUniqueId]);

    private void RemoveNotifications(IEnumerable<Guid>? mailUniqueIds)
    {
        var identifiers = mailUniqueIds?.Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString()).ToArray();
        if (identifiers is null || identifiers.Length == 0) return;
        try
        {
            foreach (var identifier in identifiers) Manager.Cancel(identifier, MailId);
            RefreshGroupSummaries();
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to remove mail notifications.");
        }
    }

    /// <summary>Removes group summaries whose children are gone.</summary>
    private void RefreshGroupSummaries()
    {
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        foreach (var posted in manager.GetActiveNotifications() ?? [])
        {
            if (posted.Notification is not { } notification || (notification.Flags & NotificationFlags.GroupSummary) == 0 || notification.Group is not { } group) continue;
            if (ActiveInGroup(group, excludeSummary: true).Count == 0) Manager.Cancel(posted.Tag, posted.Id);
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true) return;
        if (!ShouldDeliver(NotificationKind.Other, null, "account attention")) return;
        var tag = $"account-attention-{account.Id:N}";
        // "Sign in" opens the app (the account's attention bar leads to the sign-in); "Not now" dismisses.
        Post(new Request(
            Tag: tag,
            Id: GenericId,
            Channel: AndroidNotificationChannels.Account,
            Category: "wino.generic",
            Title: Translator.Exception_AccountNeedsAttention_Title,
            Subtitle: null,
            Body: string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name),
            Values: new Dictionary<string, string>
            {
                [Constants.ToastMailAccountIdKey] = account.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail
            },
            SmallIcon: Resource.Drawable.wino_notification_mail,
            AndroidCategory: NotificationCompat.CategoryError,
            Actions:
            [
                new Action(NotificationActionIdentifiers.Open, Translator.Buttons_SignIn, ActionTarget.Activity),
                new Action(NotificationActionIdentifiers.Dismiss, Translator.Buttons_NotNow, ActionTarget.Dismiss)
            ]));
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
        if (!ShouldDeliver(NotificationKind.Other, null, "reader runtime")) return;
        Post(new Request(
            Tag: "reader-runtime-missing",
            Id: GenericId,
            Channel: AndroidNotificationChannels.Account,
            Category: "wino.generic",
            Title: Translator.Exception_WebView2RuntimeMissing_Title,
            Subtitle: null,
            Body: Translator.Exception_WebView2RuntimeMissing_Message,
            Values: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail },
            SmallIcon: Resource.Drawable.wino_notification_mail));
    }

    #endregion

    #region Calendar, people and tasks

    public async Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem == null) return;

        MailAccountPreferences? accountPreferences = null;
        if (calendarItem.AssignedCalendar?.AccountId is { } accountId)
            accountPreferences = (await _accountService.GetAccountAsync(accountId).ConfigureAwait(false))?.Preferences;
        // Recorded before the policy gate: a reminder held by quiet hours is handled, not missed.
        _reminderState.RecordShown(DateTime.Now);
        if (!ShouldDeliver(NotificationKind.CalendarReminder, accountPreferences, "calendar reminder")) return;

        var snoozeMinutes = NotificationContentRules.ResolveReminderSnoozeMinutes(
            reminderDurationInSeconds, _preferences.DefaultReminderDurationInSeconds, _preferences.DefaultSnoozeDurationInMinutes);
        var canJoin = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _);
        var values = new Dictionary<string, string>
        {
            [Constants.ToastCalendarActionKey] = Constants.ToastCalendarNavigateAction,
            [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeCalendar
        };
        if (snoozeMinutes is { } minutes) values[Constants.ToastCalendarSnoozeDurationInputId] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var actions = new List<Action>();
        if (canJoin) actions.Add(new Action(NotificationActionIdentifiers.CalendarJoin, Translator.CalendarEventDetails_JoinOnline, ActionTarget.Activity));
        if (snoozeMinutes is { } snooze) actions.Add(new Action(NotificationActionIdentifiers.CalendarSnooze, NotificationContentRules.SnoozeTitle(snooze), ActionTarget.Background));
        actions.Add(new Action(NotificationActionIdentifiers.Dismiss, Translator.Buttons_Dismiss, ActionTarget.Dismiss));

        Post(new Request(
            Tag: $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}",
            Id: CalendarId,
            Channel: AndroidNotificationChannels.Calendar,
            Category: "wino.calendar",
            Title: calendarItem.Title ?? string.Empty,
            Subtitle: null,
            Body: NotificationContentRules.ComposeCalendarReminderBody(calendarItem, DateTime.Now),
            Values: values,
            SmallIcon: Resource.Drawable.wino_notification_calendar,
            AndroidCategory: NotificationCompat.CategoryReminder,
            Actions: actions));
    }

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem)
        => CreateCalendarReminderNotificationAsync(calendarItem,
            Math.Max(_preferences.DefaultReminderDurationInSeconds, (long)TimeSpan.FromMinutes(30).TotalSeconds));

    public Task CreateTestPeopleNotificationAsync(AccountContact contact)
    {
        if (contact == null || !ShouldDeliver(NotificationKind.Other, null, "people test")) return Task.CompletedTask;
        var secondary = contact.PrimaryEmailAddress ?? contact.PrimaryPhoneNumber;
        Post(new Request(
            Tag: $"people-test-{contact.Id:N}",
            Id: GenericId,
            Channel: AndroidNotificationChannels.Account,
            Category: "wino.generic",
            Title: string.IsNullOrWhiteSpace(contact.DisplayValue) ? Translator.Buttons_TestNotification : contact.DisplayValue,
            Subtitle: null,
            Body: string.IsNullOrWhiteSpace(secondary) ? Translator.Buttons_TestNotification : secondary,
            Values: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModePeople },
            SmallIcon: Resource.Drawable.wino_notification_mail,
            ImagePath: contact.ContactPictureFileId is { } pictureId ? _pictureStorageService.GetPicturePath(PictureKind.Contact, pictureId) : null));
        return Task.CompletedTask;
    }

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)
    {
        if (task == null || !ShouldDeliver(NotificationKind.TaskReminder, null, "task test")) return Task.CompletedTask;
        Post(new Request(
            Tag: $"task-test-{task.Id:N}",
            Id: GenericId,
            Channel: AndroidNotificationChannels.Account,
            Category: "wino.generic",
            Title: string.IsNullOrWhiteSpace(task.Title) ? Translator.Buttons_TestNotification : task.Title,
            Subtitle: null,
            Body: task.DueDate is { } dueDate ? dueDate.ToString("D") : Translator.Buttons_TestNotification,
            Values: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeTasks },
            SmallIcon: Resource.Drawable.wino_notification_mail));
        return Task.CompletedTask;
    }

    public Task CreateLiveNotificationsTestNotificationAsync()
    {
        if (!ShouldDeliver(NotificationKind.Other, null, "live notifications test")) return Task.CompletedTask;
        Post(new Request(
            Tag: "live-notifications-test",
            Id: GenericId,
            Channel: AndroidNotificationChannels.Account,
            Category: "wino.generic",
            Title: Translator.NotificationSettings_PushRelay_Title,
            Subtitle: null,
            Body: Translator.NotificationSettings_PushRelay_TestReceived,
            Values: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail },
            SmallIcon: Resource.Drawable.wino_notification_mail));
        return Task.CompletedTask;
    }

    #endregion

    #region Badge

    /// <summary>
    /// Android has no badge API: launchers show the notification count (or a dot). The mail unread total (the Windows
    /// mail taskbar badge) is kept and set as the number of every mail notification posted after this.
    /// </summary>
    public async Task UpdateTaskbarIconBadgeAsync()
    {
        try { await RefreshBadgeCountAsync().ConfigureAwait(false); }
        catch (Exception error) { Serilog.Log.Error(error, "Error while updating the badge count."); }
    }

    private async Task RefreshBadgeCountAsync()
    {
        var snapshot = await _unreadBadgeService.GetSnapshotAsync().ConfigureAwait(false);
        _badgeCount = Math.Max(0, snapshot.TaskbarUnreadCount);
    }

    // Calendar counts stay off the badge (mail unread only), and Android has no Jump List equivalent wired yet.
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;

    #endregion

    /// <summary>Removes a notification after its tap or action was handled.</summary>
    public void Cancel(AndroidNotificationIntents.NotificationIdentity identity)
    {
        if (string.IsNullOrEmpty(identity.Tag)) return;
        Manager.Cancel(identity.Tag, identity.Id);
        RefreshGroupSummaries();
    }
}
