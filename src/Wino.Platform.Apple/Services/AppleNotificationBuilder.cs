using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using UserNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Badges;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Notifications;
using Wino.Messaging.UI;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// A quick folder (the Windows Jump List equivalent): a Dock menu item on macOS, a Home Screen quick action on iOS.
/// </summary>
public sealed record AppleQuickFolderEntry(Guid AccountId, Guid FolderId, string Title);

/// <summary>
/// Apple port of the Windows NotificationBuilder: UNUserNotificationCenter notifications with the
/// same policy gate, privacy levels and actions, the mail-only app icon badge and the quick folder model.
/// The head supplies the picture conversion (<see cref="INotificationImageConverter"/>) and the badge
/// (<see cref="IApplicationBadgePresenter"/>). Responses are handled by the app head.
/// </summary>
public sealed class AppleNotificationBuilder : INotificationBuilder
{
    private readonly IDispatcher _dispatcher;
    private readonly IPreferencesService _preferences;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly IUnreadBadgeService _unreadBadgeService;
    private readonly IMailService _mailService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IPictureStorageService _pictureStorageService;
    private readonly INotificationPolicyService _policy;
    private readonly AppleNotificationSounds _sounds;
    private readonly INotificationImageConverter _imageConverter;
    private readonly IApplicationBadgePresenter _badgePresenter;
    private readonly SemaphoreSlim _badgeLock = new(1, 1);
    private readonly object _categoryLock = new();
    private Task<bool>? _authorization;
    private string? _registeredCategoryKey;

    public AppleNotificationBuilder(IDispatcher dispatcher,
                                  IPreferencesService preferences,
                                  IAccountService accountService,
                                  IFolderService folderService,
                                  IUnreadBadgeService unreadBadgeService,
                                  IMailService mailService,
                                  IThumbnailService thumbnailService,
                                  IPictureStorageService pictureStorageService,
                                  INotificationPolicyService policy,
                                  AppleNotificationSounds sounds,
                                  INotificationImageConverter imageConverter,
                                  IApplicationBadgePresenter badgePresenter)
    {
        _dispatcher = dispatcher;
        _preferences = preferences;
        _accountService = accountService;
        _folderService = folderService;
        _unreadBadgeService = unreadBadgeService;
        _mailService = mailService;
        _thumbnailService = thumbnailService;
        _pictureStorageService = pictureStorageService;
        _policy = policy;
        _sounds = sounds;
        _imageConverter = imageConverter;
        _badgePresenter = badgePresenter;
        _sounds.PrepareConfiguredSounds();

        WeakReferenceMessenger.Default.Register<AppleNotificationBuilder, MailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications([msg.UniqueId]));
        WeakReferenceMessenger.Default.Register<AppleNotificationBuilder, BulkMailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications(msg.UniqueIds));
        WeakReferenceMessenger.Default.Register<AppleNotificationBuilder, BulkMailUpdatedMessage>(this, (r, msg) =>
        {
            if (msg.Source == EntityUpdateSource.Server &&
                (msg.ChangedProperties & (MailCopyChangeFlags.IsRead | MailCopyChangeFlags.FolderId)) != 0)
            {
                _ = r.UpdateTaskbarIconBadgeAsync();
            }
        });
    }

    /// <summary>Snapshot behind the app icon badge; launch routing reads it so the count and destination agree.</summary>
    public static UnreadBadgeSnapshot LastSnapshot { get; private set; } = UnreadBadgeSnapshot.Empty;

    /// <summary>Quick folders (Dock menu, Home Screen quick actions), rebuilt by <see cref="UpdateJumpListOptionsAsync"/>.</summary>
    public IReadOnlyList<AppleQuickFolderEntry> QuickFolders { get; private set; } = [];

    /// <summary>The current badge text, for diagnostics.</summary>
    public string BadgeText { get; private set; } = string.Empty;

    #region Authorization and categories

    /// <summary>
    /// Registers the notification categories for the current preferences and language. Called before
    /// every request and at launch; it only talks to the notification center when something changed.
    /// </summary>
    public void RegisterCategories()
    {
        var (first, second) = AppleNotificationCategories.ResolveMailActions(_preferences.FirstMailNotificationAction, _preferences.SecondMailNotificationAction);
        var key = $"{first}|{second}|{Translator.Buttons_Dismiss}|{Translator.Buttons_Open}|{Translator.MailOperation_Reply}";
        lock (_categoryLock)
        {
            if (key == _registeredCategoryKey) return;
            _registeredCategoryKey = key;
        }
        UNUserNotificationCenter.Current.SetNotificationCategories(AppleNotificationCategories.Create(first, second));
    }

    private async Task<bool> AuthorizeAsync()
    {
        RegisterCategories();
        var granted = await (_authorization ??= RequestAuthorizationAsync()).ConfigureAwait(false);
        // Asking again after a refusal does not prompt; it picks up a later change in System Settings.
        if (!granted) _authorization = null;
        return granted;
    }

    private static Task<bool> RequestAuthorizationAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound,
            (granted, error) =>
            {
                if (error != null) Serilog.Log.Warning("Notification authorization failed: {Error}", error.LocalizedDescription);
                completion.TrySetResult(granted);
            });
        return completion.Task;
    }

    /// <summary>Authorization and category registration, for the debug bridge.</summary>
    public async Task<string> DescribeAsync()
    {
        var granted = await AuthorizeAsync().ConfigureAwait(false);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        UNUserNotificationCenter.Current.GetNotificationCategories(categories =>
        {
            var names = categories?.ToArray().Select(category =>
                $"{category.Identifier}[{string.Join(",", category.Actions.Select(action => action.Identifier))}]") ?? Enumerable.Empty<string>();
            completion.TrySetResult(string.Join(" ", names));
        });
        return $"authorized={granted} categories={await completion.Task.ConfigureAwait(false)}";
    }

    #endregion

    #region Delivery

    private sealed record NotificationRequest(
        string Identifier,
        string Title,
        string? Subtitle,
        string? Body,
        string Category,
        string? ThreadIdentifier,
        IReadOnlyDictionary<string, string> UserInfo,
        string? ImagePath = null,
        string Sound = AppleNotificationSounds.DefaultSound);

    /// <summary>
    /// The single gate every notification passes through, like Windows ShowNotificationAsync:
    /// snooze, quiet hours, per-type switches and per-account overrides; suppressed ones are dropped.
    /// </summary>
    private async Task ShowAsync(NotificationRequest request, NotificationKind kind, MailAccountPreferences? accountPreferences = null)
    {
        if (!_policy.Evaluate(kind, accountPreferences, DateTimeOffset.Now).ShouldDeliver) return;
        if (!await AuthorizeAsync().ConfigureAwait(false))
        {
            Serilog.Log.Debug("Notification {Identifier} dropped: notifications are not authorized.", request.Identifier);
            return;
        }

        var attachmentPath = request.ImagePath is { } image ? CreateAttachmentCopy(image) : null;
        var sound = _sounds.CreateNotificationSound(request.Sound);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            using var content = new UNMutableNotificationContent
            {
                Title = request.Title,
                Body = request.Body ?? string.Empty,
                CategoryIdentifier = request.Category,
                UserInfo = AppleNotificationCategories.ToUserInfo(request.UserInfo)
            };
            if (sound is not null) content.Sound = sound;
            if (!string.IsNullOrWhiteSpace(request.Subtitle)) content.Subtitle = request.Subtitle;
            if (!string.IsNullOrWhiteSpace(request.ThreadIdentifier)) content.ThreadIdentifier = request.ThreadIdentifier;
            if (attachmentPath is not null)
            {
                // The notification center moves the file into its own store.
                var attachment = UNNotificationAttachment.FromIdentifier("avatar", NSUrl.FromFilename(attachmentPath), new UNNotificationAttachmentOptions(), out var attachmentError);
                if (attachment is not null) content.Attachments = [attachment];
                else Serilog.Log.Debug("Notification image was not attached: {Error}", attachmentError?.LocalizedDescription);
            }
            using var notificationRequest = UNNotificationRequest.FromIdentifier(request.Identifier, content, null);
            UNUserNotificationCenter.Current.AddNotificationRequest(notificationRequest, error =>
            {
                if (error != null) completion.TrySetException(new InvalidOperationException("The notification center could not deliver the notification: " + error.LocalizedDescription));
                else completion.TrySetResult();
            });
        }).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Converts a sender picture to a PNG in the temporary folder for a notification attachment.</summary>
    private string? CreateAttachmentCopy(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var folder = Path.Combine(Path.GetTempPath(), "wino-notification-images");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            return _imageConverter.TryWritePng(sourcePath, path) ? path : null;
        }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "Could not prepare a notification image.");
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
            var notifiable = new List<(MailCopy MailItem, MailAccountPreferences? Preferences)>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var item in newMailItems)
            {
                var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false);
                if (mailItem?.AssignedFolder is null) continue;
                var account = accounts.FirstOrDefault(account => account.Id == mailItem.AssignedFolder.MailAccountId);
                var settings = NotificationSettingsResolver.ResolveMail(_preferences, account?.Preferences);
                if (settings.IsEnabled && NotificationContentRules.IsWithinNotificationScope(mailItem, settings.Scope))
                    notifiable.Add((mailItem, account?.Preferences));
            }

            if (notifiable.Count == 0) return;

            if (notifiable.Count > NotificationContentRules.MaxIndividualMailNotifications)
            {
                var summary = NotificationContentRules.ComposeSummaryText(notifiable.Count);
                await ShowAsync(new NotificationRequest(
                    Identifier: "mail-summary-" + Guid.NewGuid().ToString("N"),
                    Title: summary.Title,
                    Subtitle: summary.Subtitle,
                    Body: summary.Body,
                    Category: AppleNotificationCategories.MailSummary,
                    ThreadIdentifier: null,
                    UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail },
                    Sound: _sounds.Resolve(AppleNotificationSoundKind.Mail)),
                    NotificationKind.Mail).ConfigureAwait(false);
            }
            else
            {
                foreach (var (mailItem, accountPreferences) in notifiable)
                    await CreateSingleNotificationAsync(mailItem, accountPreferences).ConfigureAwait(false);
            }

            await UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
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
            foreach (var mailItem in mailItems) await CreateSingleNotificationAsync(mailItem).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to create test notifications.");
        }
    }

    private async Task CreateSingleNotificationAsync(MailCopy mailItem, MailAccountPreferences? accountPreferences = null)
    {
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

        var (title, subtitle, body) = NotificationContentRules.ComposeMailText(mailItem, settings.Content);

        var accountId = mailItem.AssignedFolder?.MailAccountId;
        await ShowAsync(new NotificationRequest(
            Identifier: mailItem.UniqueId.ToString(),
            Title: title,
            Subtitle: subtitle,
            Body: body,
            Category: AppleNotificationCategories.Mail,
            ThreadIdentifier: accountId?.ToString(),
            UserInfo: new Dictionary<string, string>
            {
                [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
                [Constants.ToastActionKey] = MailOperation.Navigate.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail
            },
            ImagePath: imagePath,
            Sound: _sounds.ResolveMail(accountPreferences)),
            NotificationKind.Mail, accountPreferences).ConfigureAwait(false);
    }

    public void RemoveNotification(Guid mailUniqueId) => RemoveNotifications([mailUniqueId]);

    private void RemoveNotifications(IEnumerable<Guid>? mailUniqueIds)
    {
        var identifiers = mailUniqueIds?.Where(id => id != Guid.Empty).Distinct().Select(id => id.ToString()).ToArray();
        if (identifiers is null || identifiers.Length == 0) return;
        try
        {
            UNUserNotificationCenter.Current.RemoveDeliveredNotifications(identifiers);
            UNUserNotificationCenter.Current.RemovePendingNotificationRequests(identifiers);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Failed to remove mail notifications.");
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true) return;
        Observe(ShowAsync(new NotificationRequest(
            Identifier: $"account-attention-{account.Id:N}",
            Title: Translator.Exception_AccountNeedsAttention_Title,
            Subtitle: null,
            Body: string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name),
            Category: AppleNotificationCategories.Generic,
            ThreadIdentifier: account.Id.ToString(),
            UserInfo: new Dictionary<string, string>
            {
                [Constants.ToastMailAccountIdKey] = account.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail
            }),
            NotificationKind.Other), "account attention");
    }

    public void CreateWebView2RuntimeMissingNotification()
        => Observe(ShowAsync(new NotificationRequest(
            Identifier: "reader-runtime-missing",
            Title: Translator.Exception_WebView2RuntimeMissing_Title,
            Subtitle: null,
            Body: Translator.Exception_WebView2RuntimeMissing_Message,
            Category: AppleNotificationCategories.Generic,
            ThreadIdentifier: null,
            UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail }),
            NotificationKind.Other), "reader runtime");

    #endregion

    #region Calendar, people and tasks

    public async Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
    {
        if (calendarItem == null) return;

        MailAccountPreferences? accountPreferences = null;
        if (calendarItem.AssignedCalendar?.AccountId is { } accountId)
            accountPreferences = (await _accountService.GetAccountAsync(accountId).ConfigureAwait(false))?.Preferences;

        var body = NotificationContentRules.ComposeCalendarReminderBody(calendarItem, DateTime.Now);

        // One Snooze action whose length follows the "Default snooze duration" setting (Windows' default pick).
        var snoozeMinutes = NotificationContentRules.ResolveReminderSnoozeMinutes(
            reminderDurationInSeconds, _preferences.DefaultReminderDurationInSeconds, _preferences.DefaultSnoozeDurationInMinutes);
        var canJoin = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _);
        var userInfo = new Dictionary<string, string>
        {
            [Constants.ToastCalendarActionKey] = Constants.ToastCalendarNavigateAction,
            [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
            [Constants.ToastModeKey] = Constants.ToastModeCalendar
        };
        if (snoozeMinutes is { } minutes) userInfo[Constants.ToastCalendarSnoozeDurationInputId] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await ShowAsync(new NotificationRequest(
            Identifier: $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}",
            Title: calendarItem.Title ?? string.Empty,
            Subtitle: null,
            Body: body,
            Category: AppleNotificationCategories.CalendarCategory(canJoin, snoozeMinutes),
            ThreadIdentifier: "calendar",
            UserInfo: userInfo,
            Sound: _sounds.Resolve(AppleNotificationSoundKind.Calendar)),
            NotificationKind.CalendarReminder, accountPreferences).ConfigureAwait(false);
    }

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem)
        => CreateCalendarReminderNotificationAsync(calendarItem,
            Math.Max(_preferences.DefaultReminderDurationInSeconds, (long)TimeSpan.FromMinutes(30).TotalSeconds));

    public Task CreateTestPeopleNotificationAsync(AccountContact contact)
    {
        if (contact == null) return Task.CompletedTask;
        var secondary = contact.PrimaryEmailAddress ?? contact.PrimaryPhoneNumber;
        return ShowAsync(new NotificationRequest(
            Identifier: $"people-test-{contact.Id:N}",
            Title: string.IsNullOrWhiteSpace(contact.DisplayValue) ? Translator.Buttons_TestNotification : contact.DisplayValue,
            Subtitle: null,
            Body: string.IsNullOrWhiteSpace(secondary) ? Translator.Buttons_TestNotification : secondary,
            Category: AppleNotificationCategories.Generic,
            ThreadIdentifier: "people",
            UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModePeople },
            ImagePath: contact.ContactPictureFileId is { } pictureId ? _pictureStorageService.GetPicturePath(PictureKind.Contact, pictureId) : null),
            NotificationKind.Other);
    }

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)
    {
        if (task == null) return Task.CompletedTask;
        return ShowAsync(new NotificationRequest(
            Identifier: $"task-test-{task.Id:N}",
            Title: string.IsNullOrWhiteSpace(task.Title) ? Translator.Buttons_TestNotification : task.Title,
            Subtitle: null,
            Body: task.DueDate is { } dueDate ? dueDate.ToString("D") : Translator.Buttons_TestNotification,
            Category: AppleNotificationCategories.Generic,
            ThreadIdentifier: "tasks",
            UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeTasks },
            Sound: _sounds.Resolve(AppleNotificationSoundKind.Task)),
            NotificationKind.TaskReminder);
    }

    public Task CreateLiveNotificationsTestNotificationAsync()
        => ShowAsync(new NotificationRequest(
            Identifier: "live-notifications-test",
            Title: Translator.NotificationSettings_PushRelay_Title,
            Subtitle: null,
            Body: Translator.NotificationSettings_PushRelay_TestReceived,
            Category: AppleNotificationCategories.Generic,
            ThreadIdentifier: "live-notifications",
            UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail },
            Sound: _sounds.Resolve(AppleNotificationSoundKind.Mail)),
            NotificationKind.Other);

    #endregion

    #region Badge and quick folders

    /// <summary>The app icon shows the mail unread total only (the Windows mail taskbar badge).</summary>
    public async Task UpdateTaskbarIconBadgeAsync()
    {
        await _badgeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = await _unreadBadgeService.GetSnapshotAsync().ConfigureAwait(false);
            LastSnapshot = snapshot;
            var count = snapshot.TaskbarUnreadCount;
            var text = count > 0 ? count.ToString() : string.Empty;
            BadgeText = text;
            await _dispatcher.ExecuteOnUIThread(() => _badgePresenter.ShowBadge(Math.Max(0, count))).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Error while updating the app icon badge.");
        }
        finally
        {
            _badgeLock.Release();
        }
    }

    // Calendar counts stay off the app icon (mail unread only); there is no separate calendar tile.
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;

    /// <summary>Windows UpdateJumpListOptionsAsync: folders the user pinned to the Jump List become quick folders.</summary>
    public async Task UpdateJumpListOptionsAsync()
    {
        try
        {
            var entries = new List<AppleQuickFolderEntry>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            foreach (var account in accounts.Where(account => account.IsMailAccessGranted && account.Preferences?.IsJumpListEnabled == true))
            {
                var accountName = string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
                var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
                foreach (var folder in folders.Where(folder => folder.IsMoveTarget && folder.IsJumpListEnabled))
                    entries.Add(new AppleQuickFolderEntry(account.Id, folder.Id, $"{folder.FolderName} - {accountName}"));
            }
            QuickFolders = entries;
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Error while updating the quick folders.");
        }
    }

    #endregion
}
