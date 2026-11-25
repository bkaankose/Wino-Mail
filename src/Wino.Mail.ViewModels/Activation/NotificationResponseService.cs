#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Notifications;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Requests;
using Wino.Services.Extensions;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Mail.ViewModels.Activation;

/// <summary>
/// The notification response rules shared by every head (the Windows toast activation, the Apple
/// UNUserNotificationCenter delegate, the Android notification intents). UI work goes through
/// <see cref="INotificationResponseNavigator"/>, and a mail action asks and reports through the same
/// <see cref="WinoRequestCoordinator"/> a page uses, so it lives in the ViewModel layer.
/// </summary>
public sealed partial class NotificationResponseService(
    ICalendarService calendarService,
    IMailService mailService,
    IFolderService folderService,
    IAccountService accountService,
    IMimeFileService mimeFileService,
    IWinoRequestDelegator requestDelegator,
    IWinoRequestProcessor requestProcessor,
    ISynchronizationManager synchronizationManager,
    WinoRequestCoordinator requestCoordinator,
    INotificationBuilder notificationBuilder,
    IPreferencesService preferencesService,
    IExternalLauncher externalLauncher,
    INotificationResponseNavigator navigator,
    IAppLockService appLock,
    ITaskService taskService,
    IApplicationRuntime? runtime = null) : INotificationResponseService
{
    private static readonly TimeSpan RelayLookupSyncBudget = TimeSpan.FromSeconds(20);
    private readonly ILogger _logger = Log.ForContext<NotificationResponseService>();

    public async Task HandleAsync(NotificationResponse response)
    {
        if (response.IsDismiss) return;

        var values = response.Values;

        if (values.TryGetValue(Constants.ToastCalendarItemIdKey, out var calendarItemValue) && Guid.TryParse(calendarItemValue, out var calendarItemId))
        {
            if (response.ActionIdentifier == NotificationActionIdentifiers.CalendarJoin) await JoinOnlineAsync(calendarItemId);
            else if (response.ActionIdentifier == NotificationActionIdentifiers.CalendarSnooze)
            {
                if (!IsActionRefusedByAppLock("calendar snooze")) await SnoozeAsync(calendarItemId, values);
            }
            else if (response.IsOpen) await OpenCalendarItemAsync(calendarItemId);

            return;
        }

        if (values.TryGetValue(Constants.ToastTaskIdKey, out var taskValue) && Guid.TryParse(taskValue, out var taskId))
        {
            await HandleTaskResponseAsync(response, taskId);
            return;
        }

        if (values.TryGetValue(Constants.ToastMailUniqueIdKey, out var mailValue) && Guid.TryParse(mailValue, out var mailUniqueId))
        {
            if (response.IsOpen)
            {
                await OpenMailAsync(mailUniqueId);
                return;
            }

            if (!NotificationActionIdentifiers.TryParseMailAction(response.ActionIdentifier, out var action)) return;

            var replyText = response.UserText?.Trim();
            var inlineReply = action == MailOperation.Reply && !string.IsNullOrEmpty(replyText) ? replyText : null;

            // Compose actions open the shell, which asks for the unlock first, so only actions that run without it are checked.
            if (inlineReply is null && action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward)
            {
                await ComposeAsync(action, mailUniqueId);
                return;
            }

            if (IsActionRefusedByAppLock($"notification action {action}")) return;

            // The user kept action buttons on notifications, so this one action goes through while the window is locked.
            using var actionScope = appLock.AllowActionsWhileLocked();

            if (inlineReply is not null)
                await ReplyInlineAsync(mailUniqueId, inlineReply);
            else if (response.RunsWithoutWindow)
                await ExecuteMailActionWithoutWindowAsync(action, mailUniqueId);
            else
                await ExecuteMailActionAsync(action, mailUniqueId);

            return;
        }

        // A relay alert: the extension's enriched notification names the provider message, a plain one only the account.
        if (response.IsOpen && values.TryGetValue(Constants.ToastRelayClientAccountIdKey, out var cidValue) && Guid.TryParse(cidValue, out var relayAccountId))
        {
            values.TryGetValue(Constants.ToastRelayMailIdKey, out var relayMailId);
            values.TryGetValue(Constants.ToastRelayInternetMessageIdKey, out var relayMessageId);
            await OpenRelayMailAsync(relayAccountId, relayMailId, relayMessageId);
            return;
        }

        if (response.IsOpen)
        {
            var mode = ResolveMode(values);
            await navigator.OpenModeAsync(mode, DefaultModeParameter(mode));
        }
    }

    /// <summary>
    /// Opens the message a relay alert announced. The app may have been terminated, so the message is looked up by its
    /// provider id, synchronized once when it is not stored yet, and mail opens plainly when it still cannot be found.
    /// </summary>
    private async Task OpenRelayMailAsync(Guid accountId, string? providerMailId, string? internetMessageId = null)
    {
        MailCopy? mailItem = await FindRelayMailAsync(accountId, providerMailId, internetMessageId);
        if (mailItem is null && runtime is not null)
        {
            try
            {
                using var budget = new System.Threading.CancellationTokenSource(RelayLookupSyncBudget);
                await runtime.SynchronizeAccountInboxAsync(accountId, budget.Token);
            }
            catch (Exception error)
            {
                _logger.Information("Inbox sync before opening a relay notification did not finish: {Error}", error.Message);
            }

            mailItem = await FindRelayMailAsync(accountId, providerMailId, internetMessageId);
        }

        if (mailItem?.AssignedFolder is { } folder)
        {
            await navigator.OpenMailAsync(accountId, folder.Id, mailItem);
            return;
        }

        _logger.Information("Relay notification mail {MailId} of account {AccountId} was not found; opening mail.", providerMailId, accountId);
        await navigator.OpenModeAsync(WinoApplicationMode.Mail);
    }

    private async Task<MailCopy?> FindRelayMailAsync(Guid accountId, string? providerMailId, string? internetMessageId)
    {
        try
        {
            MailCopy? item = string.IsNullOrEmpty(providerMailId) ? null : await mailService.GetSingleMailItemAsync(providerMailId);
            if (item is null && !string.IsNullOrEmpty(internetMessageId))
                item = await mailService.GetMailCopyByMessageIdAsync(accountId, internetMessageId);
            return item is not null && (item.AssignedFolder is null || item.AssignedFolder.MailAccountId == accountId) ? item : null;
        }
        catch (Exception error)
        {
            _logger.Debug(error, "Relay mail lookup failed for {MailId}.", providerMailId);
            return null;
        }
    }

    // Only notifications created before the read-only option was turned on still carry action buttons.
    public bool IsActionRefusedByAppLock(string action)
    {
        if (!appLock.IsLocked || !appLock.AreNotificationsReadOnly) return false;

        _logger.Information("Ignoring {Action} while the app is locked.", action);
        return true;
    }

    /// <summary>The mode a generic notification opens (<see cref="Constants.ToastModeKey"/>); mail when absent or unknown.</summary>
    public static WinoApplicationMode ResolveMode(IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue(Constants.ToastModeKey, out var modeValue);

        return modeValue switch
        {
            Constants.ToastModeCalendar => WinoApplicationMode.Calendar,
            Constants.ToastModePeople => WinoApplicationMode.Contacts,
            Constants.ToastModeTasks => WinoApplicationMode.Tasks,
            _ => WinoApplicationMode.Mail
        };
    }

    private static object? DefaultModeParameter(WinoApplicationMode mode)
        => mode == WinoApplicationMode.Calendar ? new CalendarPageNavigationArgs { RequestDefaultNavigation = true } : null;

    /// <summary>
    /// The snooze length: the one the notification offered (<see cref="Constants.ToastCalendarSnoozeDurationInputId"/>);
    /// older notifications without it use the "Default snooze duration" setting, then <see cref="NotificationActionIdentifiers.FallbackSnoozeMinutes"/>.
    /// </summary>
    public static int ResolveSnoozeMinutes(IReadOnlyDictionary<string, string> values, int preferredMinutes)
    {
        if (values.TryGetValue(Constants.ToastCalendarSnoozeDurationInputId, out var offered) &&
            int.TryParse(offered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offeredMinutes) &&
            offeredMinutes > 0)
        {
            return offeredMinutes;
        }

        return preferredMinutes > 0 ? preferredMinutes : NotificationActionIdentifiers.FallbackSnoozeMinutes;
    }

    #region Calendar

    public async Task OpenCalendarItemAsync(Guid calendarItemId)
    {
        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem is null)
        {
            _logger.Information("Calendar notification item {Id} was not found; opening the calendar.", calendarItemId);
            await navigator.OpenModeAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs { RequestDefaultNavigation = true });
            return;
        }

        await navigator.OpenModeAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs
        {
            NavigationDate = calendarItem.LocalStartDate,
            PendingTarget = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single)
        });
    }

    private Task SnoozeAsync(Guid calendarItemId, IReadOnlyDictionary<string, string> values)
    {
        var minutes = ResolveSnoozeMinutes(values, preferencesService.DefaultSnoozeDurationInMinutes);
        return calendarService.SnoozeCalendarItemAsync(calendarItemId, DateTime.Now.AddMinutes(minutes));
    }

    public async Task JoinOnlineAsync(Guid calendarItemId)
    {
        if (IsActionRefusedByAppLock("calendar join")) return;

        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem is null || !CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out var joinUri)) return;

        await externalLauncher.LaunchUriAsync(joinUri);
    }

    #endregion

    #region Mail

    public async Task OpenMailAsync(Guid mailUniqueId)
    {
        MailAccount? account = await mailService.GetMailAccountByUniqueIdAsync(mailUniqueId);
        MailCopy? mailItem = account is null ? null : await mailService.GetSingleMailItemAsync(mailUniqueId);
        var folder = mailItem?.AssignedFolder;

        if (account is null || mailItem is null || folder is null)
        {
            _logger.Information("Notification mail {Id} was not found; opening mail.", mailUniqueId);
            await navigator.OpenModeAsync(WinoApplicationMode.Mail);
            return;
        }

        await navigator.OpenMailAsync(account.Id, folder.Id, mailItem);
    }

    /// <summary>A window is open, so the delegator queues the request with undo and synchronization runs in the background.</summary>
    private async Task ExecuteMailActionAsync(MailOperation action, Guid mailUniqueId)
    {
        MailCopy? mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null) return;

        await requestCoordinator.ExecuteMailOperationAsync(new MailOperationPreperationRequest(action, mailItem));
        await notificationBuilder.UpdateTaskbarIconBadgeAsync();
    }

    /// <summary>
    /// No window can show the undo bar, and a request held for undo would be lost when the process ends, so the
    /// requests are queued without undo and each account runs them on the server before this returns.
    /// </summary>
    private async Task ExecuteMailActionWithoutWindowAsync(MailOperation action, Guid mailUniqueId)
    {
        MailCopy? mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null)
        {
            _logger.Information("Notification mail {Id} for action {Action} was not found.", mailUniqueId, action);
            return;
        }

        var answered = await requestCoordinator.AnswerMailOperationAsync(new MailOperationPreperationRequest(action, mailItem));
        var requests = answered is null ? null : await requestProcessor.PrepareRequestsAsync(answered);
        if (requests is null || requests.Count == 0) return;

        foreach (var accountRequests in requests.GroupBy(request => request.Item.AssignedAccount.Id))
        {
            var accountId = accountRequests.Key;
            foreach (var request in accountRequests)
                await synchronizationManager.QueueRequestAsync(request, accountId, triggerSynchronization: false, allowUndo: false);

            var result = await synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            });

            _logger.Information("Notification action {Action} ran for account {AccountId}: {State}.", action, accountId, result.CompletedState);
        }

        await notificationBuilder.UpdateTaskbarIconBadgeAsync();
    }

    private sealed record ComposeSource(MailAccount Account, MailCopy MailItem, MimeKit.MimeMessage Mime);

    private async Task<ComposeSource?> LoadComposeSourceAsync(Guid mailUniqueId)
    {
        MailCopy? mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null) return null;

        MailAccount? account = await mailService.GetMailAccountByUniqueIdAsync(mailUniqueId) ?? mailItem.AssignedAccount;
        if (account is null) return null;

        if (await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft) is null)
        {
            _logger.Information("Notification compose: the draft folder of account {Id} is missing.", account.Id);
            return null;
        }

        if (!await EnsureMimeAsync(account.Id, mailItem)) return null;

        try
        {
            var mime = await mimeFileService.GetMimeMessageInformationAsync(mailItem.FileId, account.Id);
            return mime?.MimeMessage is null ? null : new ComposeSource(account, mailItem, mime.MimeMessage);
        }
        catch (IOException error)
        {
            _logger.Warning(error, "Notification compose: the message of mail {Id} could not be read.", mailUniqueId);
            return null;
        }
    }

    /// <summary>
    /// Synchronization stores only the headers, and the reader downloads the message when it opens. A notification
    /// can reply to a mail nobody opened, so the message is downloaded here the same way.
    /// </summary>
    private async Task<bool> EnsureMimeAsync(Guid accountId, MailCopy mailItem)
    {
        if (await mimeFileService.IsMimeExistAsync(accountId, mailItem.FileId)) return true;

        try
        {
            await synchronizationManager.DownloadMimeMessageAsync(mailItem, accountId);
        }
        catch (Exception error)
        {
            _logger.Warning(error, "Notification compose: the message of mail {Id} could not be downloaded.", mailItem.UniqueId);
            return false;
        }

        if (await mimeFileService.IsMimeExistAsync(accountId, mailItem.FileId)) return true;

        _logger.Information("Notification compose: the message of mail {Id} is not available.", mailItem.UniqueId);
        return false;
    }

    private async Task<(MailCopy Draft, string Base64)> CreateDraftAsync(ComposeSource source, MailOperation action, string? initialText = null)
    {
        var options = new DraftCreationOptions
        {
            Reason = action switch
            {
                MailOperation.Reply => DraftCreationReason.Reply,
                MailOperation.ReplyAll => DraftCreationReason.ReplyAll,
                MailOperation.Forward => DraftCreationReason.Forward,
                _ => DraftCreationReason.Empty
            },
            InitialBodyText = initialText ?? string.Empty,
            ReferencedMessage = new ReferencedMessage { MimeMessage = source.Mime, MailCopy = source.MailItem }
        };

        var (draft, base64) = await mailService.CreateDraftAsync(source.Account.Id, options);
        await requestDelegator.ExecuteAsync(new DraftPreparationRequest(source.Account, draft, base64, options.Reason, source.MailItem));

        return (draft, base64);
    }

    private async Task ComposeAsync(MailOperation action, Guid mailUniqueId)
    {
        var source = await LoadComposeSourceAsync(mailUniqueId);
        if (source is null)
        {
            await OpenMailAsync(mailUniqueId);
            return;
        }

        if (!await navigator.ShowDraftsAsync(source.Account.Id)) return;

        MailCopy draft;
        try
        {
            (draft, _) = await CreateDraftAsync(source, action);
        }
        catch (AppLockedException error)
        {
            // The window was still locked when the shell finished opening; the delegator already deleted the local draft.
            requestCoordinator.ShowMailOperationError(error);
            return;
        }

        await navigator.OpenComposerAsync(draft);
    }

    /// <summary>
    /// Inline reply from the notification: the reply draft gets the typed text and is sent.
    /// Without a sending alias or a Sent folder, or on failure, the draft opens in the composer.
    /// </summary>
    private async Task ReplyInlineAsync(Guid mailUniqueId, string text)
    {
        var source = await LoadComposeSourceAsync(mailUniqueId);
        if (source is null) return;

        MailCopy? draft = null;
        try
        {
            (draft, var base64) = await CreateDraftAsync(source, MailOperation.Reply, text);

            var alias = await ResolveSendingAliasAsync(source.Account.Id, base64);
            MailItemFolder? sentFolder = await folderService.GetSpecialFolderByAccountIdAsync(source.Account.Id, SpecialFolderType.Sent);

            if (alias is not null && sentFolder is not null && draft.AssignedFolder is not null)
            {
                await requestDelegator.ExecuteAsync(
                    new SendDraftPreparationRequest(draft, alias, sentFolder, draft.AssignedFolder, source.Account.Preferences, base64));
                return;
            }

            _logger.Information("Inline reply could not be sent directly; opening the composer.");
        }
        catch (Exception error)
        {
            _logger.Error(error, "Inline reply failed; opening the composer.");
        }

        if (!await navigator.ShowDraftsAsync(source.Account.Id)) return;

        if (draft is null) (draft, _) = await CreateDraftAsync(source, MailOperation.Reply, text);
        await navigator.OpenComposerAsync(draft);
    }

    /// <summary>The alias in the draft's From header (MailService picks it from the original message), else the primary one.</summary>
    internal async Task<MailAccountAlias?> ResolveSendingAliasAsync(Guid accountId, string base64Mime)
    {
        var from = base64Mime.GetMimeMessageFromBase64().From.Mailboxes.FirstOrDefault()?.Address;
        if (!string.IsNullOrWhiteSpace(from))
        {
            List<MailAccountAlias>? aliases = await accountService.GetAccountAliasesAsync(accountId);
            var match = aliases?.FirstOrDefault(alias => string.Equals(alias.AliasAddress, from, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return await accountService.GetPrimaryAccountAliasAsync(accountId);
    }

    #endregion
}
