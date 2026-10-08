using AppKit;
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

namespace Wino.Platform.MacOS.Services;

/// <summary>A quick folder shown in the Dock menu (the Windows Jump List equivalent).</summary>
public sealed record MacDockFolderEntry(Guid AccountId, Guid FolderId, string Title);

/// <summary>
/// macOS port of the Windows NotificationBuilder: UNUserNotificationCenter notifications with the
/// same policy gate, privacy levels and actions, the mail-only Dock badge and the Dock menu model.
/// Responses are handled by the app head (MacNotificationResponseHandler).
/// </summary>
public sealed class MacNotificationBuilder : INotificationBuilder
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
    private readonly SemaphoreSlim _badgeLock = new(1, 1);
    private readonly object _categoryLock = new();
    private Task<bool>? _authorization;
    private string? _registeredCategoryKey;

    public MacNotificationBuilder(IDispatcher dispatcher,
                                  IPreferencesService preferences,
                                  IAccountService accountService,
                                  IFolderService folderService,
                                  IUnreadBadgeService unreadBadgeService,
                                  IMailService mailService,
                                  IThumbnailService thumbnailService,
                                  IPictureStorageService pictureStorageService,
                                  INotificationPolicyService policy)
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

        WeakReferenceMessenger.Default.Register<MacNotificationBuilder, MailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications([msg.UniqueId]));
        WeakReferenceMessenger.Default.Register<MacNotificationBuilder, BulkMailReadStatusChanged>(this, (r, msg) => r.RemoveNotifications(msg.UniqueIds));
        WeakReferenceMessenger.Default.Register<MacNotificationBuilder, BulkMailUpdatedMessage>(this, (r, msg) =>
        {
            if (msg.Source == EntityUpdateSource.Server &&
                (msg.ChangedProperties & (MailCopyChangeFlags.IsRead | MailCopyChangeFlags.FolderId)) != 0)
            {
                _ = r.UpdateTaskbarIconBadgeAsync();
            }
        });
    }

    /// <summary>Snapshot behind the Dock badge; launch routing reads it so the count and destination agree.</summary>
    public static UnreadBadgeSnapshot LastSnapshot { get; private set; } = UnreadBadgeSnapshot.Empty;

    /// <summary>Quick folders for the Dock menu, rebuilt by <see cref="UpdateJumpListOptionsAsync"/>.</summary>
    public IReadOnlyList<MacDockFolderEntry> DockFolders { get; private set; } = [];

    /// <summary>The current Dock badge text, for diagnostics.</summary>
    public string BadgeText { get; private set; } = string.Empty;

    #region Authorization and categories

    /// <summary>
    /// Registers the notification categories for the current preferences and language. Called before
    /// every request and at launch; it only talks to the notification center when something changed.
    /// </summary>
    public void RegisterCategories()
    {
        var (first, second) = MacNotificationCategories.ResolveMailActions(_preferences.FirstMailNotificationAction, _preferences.SecondMailNotificationAction);
        var key = $"{first}|{second}|{Translator.Buttons_Dismiss}|{Translator.Buttons_Open}|{Translator.MailOperation_Reply}";
        lock (_categoryLock)
        {
            if (key == _registeredCategoryKey) return;
            _registeredCategoryKey = key;
        }
        UNUserNotificationCenter.Current.SetNotificationCategories(MacNotificationCategories.Create(first, second));
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
        string? ImagePath = null);

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
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            using var content = new UNMutableNotificationContent
            {
                Title = request.Title,
                Body = request.Body ?? string.Empty,
                CategoryIdentifier = request.Category,
                UserInfo = MacNotificationCategories.ToUserInfo(request.UserInfo),
                Sound = UNNotificationSound.Default
            };
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
                if (error != null) completion.TrySetException(new InvalidOperationException("macOS could not deliver the notification: " + error.LocalizedDescription));
                else completion.TrySetResult();
            });
        }).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Converts a sender picture to a PNG in the temporary folder for a notification attachment.</summary>
    private static string? CreateAttachmentCopy(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            using var image = new NSImage(sourcePath);
            using var tiff = image.AsTiff();
            if (tiff is null) return null;
            using var bitmap = new NSBitmapImageRep(tiff);
            using var png = bitmap.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            if (png is null) return null;
            var folder = Path.Combine(Path.GetTempPath(), "wino-notification-images");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            if (!png.Save(path, true)) return null;
            return path;
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
                if (settings.IsEnabled && IsWithinNotificationScope(mailItem, settings.Scope))
                    notifiable.Add((mailItem, account?.Preferences));
            }

            if (notifiable.Count == 0) return;

            if (notifiable.Count > 3)
            {
                await ShowAsync(new NotificationRequest(
                    Identifier: "mail-summary-" + Guid.NewGuid().ToString("N"),
                    Title: Translator.Notifications_MultipleNotificationsTitle,
                    Subtitle: null,
                    Body: string.Format(Translator.Notifications_MultipleNotificationsMessage, notifiable.Count),
                    Category: MacNotificationCategories.MailSummary,
                    ThreadIdentifier: null,
                    UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeMail }),
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
        if (settings.Content != MailNotificationContent.Nothing)
        {
            imagePath = mailItem.SenderContact?.ContactPictureFileId is { } fileId
                ? _pictureStorageService.GetPicturePath(PictureKind.Contact, fileId)
                : null;
            if (imagePath is null && !string.IsNullOrWhiteSpace(mailItem.FromAddress))
                imagePath = (await _thumbnailService.GetThumbnailAsync(mailItem.FromAddress, awaitLoad: true).ConfigureAwait(false))?.FilePath;
        }

        // Windows AddMailNotificationText: as much as the account's privacy level allows.
        string title;
        string? subtitle = null, body = null;
        if (settings.Content == MailNotificationContent.Nothing)
        {
            title = Translator.Notifications_MultipleNotificationsTitle;
        }
        else
        {
            title = string.IsNullOrWhiteSpace(mailItem.FromName) ? mailItem.FromAddress ?? string.Empty : mailItem.FromName;
            if (settings.Content != MailNotificationContent.SenderOnly)
            {
                subtitle = mailItem.Subject;
                if (settings.Content == MailNotificationContent.SenderSubjectPreview) body = mailItem.PreviewText;
            }
        }

        var accountId = mailItem.AssignedFolder?.MailAccountId;
        await ShowAsync(new NotificationRequest(
            Identifier: mailItem.UniqueId.ToString(),
            Title: title,
            Subtitle: subtitle,
            Body: body,
            Category: MacNotificationCategories.Mail,
            ThreadIdentifier: accountId?.ToString(),
            UserInfo: new Dictionary<string, string>
            {
                [Constants.ToastMailUniqueIdKey] = mailItem.UniqueId.ToString(),
                [Constants.ToastActionKey] = MailOperation.Navigate.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeMail
            },
            ImagePath: imagePath),
            NotificationKind.Mail, accountPreferences).ConfigureAwait(false);
    }

    /// <summary>Windows IsWithinNotificationScope.</summary>
    private static bool IsWithinNotificationScope(MailCopy mailItem, MailNotificationScope scope)
    {
        if (scope == MailNotificationScope.AllFolders) return true;
        var isInbox = mailItem.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox;
        return scope switch
        {
            MailNotificationScope.InboxOnly => isInbox,
            MailNotificationScope.FocusedInboxOnly => isInbox && mailItem.IsFocused,
            MailNotificationScope.InboxAndCustomFolders => isInbox || mailItem.AssignedFolder?.SpecialFolderType == SpecialFolderType.Other,
            _ => true
        };
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
            Category: MacNotificationCategories.Generic,
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
            Category: MacNotificationCategories.Generic,
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

        var localStart = calendarItem.GetLocalStartDate();
        var body = $"{GetCalendarReminderContext(localStart, DateTime.Now)} - {localStart:g}";
        if (!string.IsNullOrWhiteSpace(calendarItem.Location)) body += "\n" + calendarItem.Location;

        var canSnooze = CalendarReminderSnoozeOptions
            .GetAllowedSnoozeMinutes(reminderDurationInSeconds, _preferences.DefaultReminderDurationInSeconds)
            .Contains(MacNotificationCategories.CalendarSnoozeMinutes);
        var canJoin = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _);

        await ShowAsync(new NotificationRequest(
            Identifier: $"calendar-reminder-{calendarItem.Id:N}-{reminderDurationInSeconds}",
            Title: calendarItem.Title ?? string.Empty,
            Subtitle: null,
            Body: body,
            Category: MacNotificationCategories.CalendarCategory(canJoin, canSnooze),
            ThreadIdentifier: "calendar",
            UserInfo: new Dictionary<string, string>
            {
                [Constants.ToastCalendarActionKey] = Constants.ToastCalendarNavigateAction,
                [Constants.ToastCalendarItemIdKey] = calendarItem.Id.ToString(),
                [Constants.ToastModeKey] = Constants.ToastModeCalendar
            }),
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
            Category: MacNotificationCategories.Generic,
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
            Category: MacNotificationCategories.Generic,
            ThreadIdentifier: "tasks",
            UserInfo: new Dictionary<string, string> { [Constants.ToastModeKey] = Constants.ToastModeTasks }),
            NotificationKind.TaskReminder);
    }

    /// <summary>Windows GetCalendarReminderContext.</summary>
    private static string GetCalendarReminderContext(DateTime localStart, DateTime nowLocal)
    {
        var delta = localStart - nowLocal;
        var absDelta = delta.Duration();
        if (absDelta < TimeSpan.FromMinutes(1))
            return delta.TotalSeconds >= 0 ? Translator.CalendarReminder_StartingNow : Translator.CalendarReminder_StartedNow;
        if (delta.TotalSeconds > 0)
        {
            if (delta.TotalHours >= 1) return string.Format(Translator.CalendarReminder_StartsInHours, Math.Max(1, (int)Math.Floor(delta.TotalHours)));
            return string.Format(Translator.CalendarReminder_StartsInMinutes, Math.Max(1, (int)Math.Floor(delta.TotalMinutes)));
        }
        if (absDelta.TotalHours >= 1) return string.Format(Translator.CalendarReminder_StartedHoursAgo, Math.Max(1, (int)Math.Floor(absDelta.TotalHours)));
        return string.Format(Translator.CalendarReminder_StartedMinutesAgo, Math.Max(1, (int)Math.Floor(absDelta.TotalMinutes)));
    }

    #endregion

    #region Dock

    /// <summary>The Dock shows the mail unread total only (the Windows mail taskbar badge).</summary>
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
            await _dispatcher.ExecuteOnUIThread(() => NSApplication.SharedApplication.DockTile.BadgeLabel = text).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Error while updating the Dock badge.");
        }
        finally
        {
            _badgeLock.Release();
        }
    }

    // Calendar counts stay off the Dock (mail unread only); there is no separate calendar tile.
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;

    /// <summary>Windows UpdateJumpListOptionsAsync: folders the user pinned to the Jump List become Dock menu items.</summary>
    public async Task UpdateJumpListOptionsAsync()
    {
        try
        {
            var entries = new List<MacDockFolderEntry>();
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            foreach (var account in accounts.Where(account => account.IsMailAccessGranted && account.Preferences?.IsJumpListEnabled == true))
            {
                var accountName = string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name;
                var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
                foreach (var folder in folders.Where(folder => folder.IsMoveTarget && folder.IsJumpListEnabled))
                    entries.Add(new MacDockFolderEntry(account.Id, folder.Id, $"{folder.FolderName} - {accountName}"));
            }
            DockFolders = entries;
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Error while updating the Dock menu.");
        }
    }

    #endregion
}
