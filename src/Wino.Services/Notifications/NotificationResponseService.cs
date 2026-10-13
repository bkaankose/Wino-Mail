#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Services.Notifications;

/// <summary>
/// The notification response rules shared by every head (the Windows AppNotificationHandler and
/// App.xaml.cs toast handlers, the macOS UNUserNotificationCenter delegate). UI work goes through
/// <see cref="INotificationResponseNavigator"/>.
/// </summary>
public sealed class NotificationResponseService(
    ICalendarService calendarService,
    IMailService mailService,
    IFolderService folderService,
    IAccountService accountService,
    IMimeFileService mimeFileService,
    IWinoRequestDelegator requestDelegator,
    INotificationBuilder notificationBuilder,
    IPreferencesService preferencesService,
    IExternalLauncher externalLauncher,
    INotificationResponseNavigator navigator) : INotificationResponseService
{
    private readonly ILogger _logger = Log.ForContext<NotificationResponseService>();

    public async Task HandleAsync(NotificationResponse response)
    {
        if (response.IsDismiss) return;

        var values = response.Values;

        if (values.TryGetValue(Constants.ToastCalendarItemIdKey, out var calendarItemValue) && Guid.TryParse(calendarItemValue, out var calendarItemId))
        {
            if (response.ActionIdentifier == NotificationActionIdentifiers.CalendarJoin) await JoinOnlineAsync(calendarItemId);
            else if (response.ActionIdentifier == NotificationActionIdentifiers.CalendarSnooze) await SnoozeAsync(calendarItemId, values);
            else if (response.IsOpen) await OpenCalendarItemAsync(calendarItemId);

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
            if (action == MailOperation.Reply && !string.IsNullOrEmpty(replyText))
                await ReplyInlineAsync(mailUniqueId, replyText);
            else if (action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward)
                await ComposeAsync(action, mailUniqueId);
            else
                await ExecuteMailActionAsync(action, mailUniqueId);

            return;
        }

        if (response.IsOpen)
        {
            var mode = ResolveMode(values);
            await navigator.OpenModeAsync(mode, DefaultModeParameter(mode));
        }
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

    /// <summary>App.HandleCalendarToastNavigationAsync.</summary>
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

    /// <summary>App.HandleCalendarToastSnoozeAsync.</summary>
    private Task SnoozeAsync(Guid calendarItemId, IReadOnlyDictionary<string, string> values)
    {
        var minutes = ResolveSnoozeMinutes(values, preferencesService.DefaultSnoozeDurationInMinutes);
        return calendarService.SnoozeCalendarItemAsync(calendarItemId, DateTime.Now.AddMinutes(minutes));
    }

    /// <summary>App.HandleCalendarToastJoinOnlineAsync.</summary>
    public async Task JoinOnlineAsync(Guid calendarItemId)
    {
        var calendarItem = await calendarService.GetCalendarItemAsync(calendarItemId);
        if (calendarItem is null || !CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out var joinUri)) return;

        await externalLauncher.LaunchUriAsync(joinUri);
    }

    #endregion

    #region Mail

    /// <summary>App.HandleToastNavigationAsync.</summary>
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

    /// <summary>App.HandleToastActionAsync (the app is running here, so the delegator queues the request).</summary>
    private async Task ExecuteMailActionAsync(MailOperation action, Guid mailUniqueId)
    {
        MailCopy? mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null) return;

        await requestDelegator.ExecuteAsync(new MailOperationPreperationRequest(action, mailItem));
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

        var mime = await mimeFileService.GetMimeMessageInformationAsync(mailItem.FileId, account.Id);
        return mime?.MimeMessage is null ? null : new ComposeSource(account, mailItem, mime.MimeMessage);
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

    /// <summary>App.HandleToastComposeActionAsync.</summary>
    private async Task ComposeAsync(MailOperation action, Guid mailUniqueId)
    {
        var source = await LoadComposeSourceAsync(mailUniqueId);
        if (source is null)
        {
            await OpenMailAsync(mailUniqueId);
            return;
        }

        if (!await navigator.ShowDraftsAsync(source.Account.Id)) return;

        var (draft, _) = await CreateDraftAsync(source, action);
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
