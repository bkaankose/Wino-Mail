using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UserNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Mails;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Receives notification taps and action buttons (the Windows AppNotificationHandler and the
/// App.xaml.cs toast handlers). It is the notification center delegate from launch, so a tap that
/// launched the app is kept until <see cref="Attach"/> supplies the services after the runtime starts.
/// </summary>
public sealed class MacNotificationResponseHandler : UNUserNotificationCenterDelegate
{
    /// <summary>A copied response; the native objects are not kept across the queue.</summary>
    public sealed record Response(IReadOnlyDictionary<string, string> Values, string ActionIdentifier, bool IsDefault, bool IsDismiss, string? UserText);

    private readonly List<Response> _pending = [];
    private readonly object _lock = new();
    private IServiceProvider? _services;

    public void Attach(IServiceProvider services)
    {
        Response[] pending;
        lock (_lock)
        {
            _services = services;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var response in pending) Observe(HandleAsync(response));
    }

    public void Detach()
    {
        lock (_lock) _services = null;
    }

    // Wino shows its notifications while it is in front too, like Windows toasts.
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
        => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);

    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        try
        {
            var copy = new Response(
                ReadValues(response.Notification.Request.Content.UserInfo),
                response.ActionIdentifier ?? string.Empty,
                response.IsDefaultAction,
                response.IsDismissAction,
                (response as UNTextInputNotificationResponse)?.UserText);
            bool handleNow;
            lock (_lock)
            {
                handleNow = _services is not null;
                if (!handleNow) _pending.Add(copy);
            }
            if (handleNow) Observe(HandleAsync(copy));
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Could not read a notification response.");
        }
        finally
        {
            completionHandler();
        }
    }

    private static Dictionary<string, string> ReadValues(NSDictionary? userInfo)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo is null) return values;
        foreach (var key in userInfo.Keys)
        {
            if (userInfo[key] is NSString value) values[key.ToString()] = value.ToString();
        }
        return values;
    }

    private IServiceProvider Services => _services ?? throw new InvalidOperationException("Notification responses arrived before the services started.");

    private static void Observe(Task task)
        => _ = task.ContinueWith(t => Serilog.Log.Error(t.Exception, "Notification response handling failed."), TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Windows AppNotificationHandler.TryCreateActivationRoute.</summary>
    public async Task HandleAsync(Response response)
    {
        if (response.IsDismiss || response.ActionIdentifier == MacNotificationCategories.DismissAction) return;
        var values = response.Values;
        var isOpen = response.IsDefault || response.ActionIdentifier == MacNotificationCategories.OpenAction;

        if (values.TryGetValue(Constants.ToastCalendarItemIdKey, out var calendarItemValue) && Guid.TryParse(calendarItemValue, out var calendarItemId))
        {
            if (response.ActionIdentifier == MacNotificationCategories.CalendarJoinAction) await JoinOnlineAsync(calendarItemId);
            else if (response.ActionIdentifier == MacNotificationCategories.CalendarSnoozeAction) await SnoozeAsync(calendarItemId);
            else if (isOpen) await NavigateCalendarItemAsync(calendarItemId);
            return;
        }

        if (values.TryGetValue(Constants.ToastMailUniqueIdKey, out var mailValue) && Guid.TryParse(mailValue, out var mailUniqueId))
        {
            if (isOpen) { await NavigateMailAsync(mailUniqueId); return; }
            if (!MacNotificationCategories.TryParseMailAction(response.ActionIdentifier, out var action)) return;
            if (action == MailOperation.Reply && !string.IsNullOrWhiteSpace(response.UserText))
                await ReplyInlineAsync(mailUniqueId, response.UserText!.Trim());
            else if (action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward)
                await ComposeAsync(action, mailUniqueId);
            else
                await ExecuteMailActionAsync(action, mailUniqueId);
            return;
        }

        if (isOpen) await NavigateModeAsync(values);
    }

    private AppKitNavigationService Navigation => Services.GetRequiredService<AppKitNavigationService>();

    private Task NavigateModeAsync(IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue(Constants.ToastModeKey, out var modeValue);
        var mode = modeValue switch
        {
            Constants.ToastModeCalendar => WinoApplicationMode.Calendar,
            Constants.ToastModePeople => WinoApplicationMode.Contacts,
            Constants.ToastModeTasks => WinoApplicationMode.Tasks,
            _ => WinoApplicationMode.Mail
        };
        object? parameter = mode == WinoApplicationMode.Calendar ? new CalendarPageNavigationArgs { RequestDefaultNavigation = true } : null;
        return Navigation.EnsureShellAsync(mode, parameter);
    }

    #region Calendar

    /// <summary>App.HandleCalendarToastNavigationAsync.</summary>
    internal async Task NavigateCalendarItemAsync(Guid calendarItemId)
    {
        var calendarItem = await Services.GetRequiredService<ICalendarService>().GetCalendarItemAsync(calendarItemId);
        if (calendarItem is null)
        {
            Serilog.Log.Information("Calendar notification item {Id} was not found; opening the calendar.", calendarItemId);
            await Navigation.EnsureShellAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs { RequestDefaultNavigation = true });
            return;
        }
        await Navigation.EnsureShellAsync(WinoApplicationMode.Calendar, new CalendarPageNavigationArgs
        {
            NavigationDate = calendarItem.LocalStartDate,
            PendingTarget = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single)
        });
    }

    /// <summary>App.HandleCalendarToastSnoozeAsync with the fixed macOS snooze length.</summary>
    private Task SnoozeAsync(Guid calendarItemId)
        => Services.GetRequiredService<ICalendarService>()
            .SnoozeCalendarItemAsync(calendarItemId, DateTime.Now.AddMinutes(MacNotificationCategories.CalendarSnoozeMinutes));

    /// <summary>App.HandleCalendarToastJoinOnlineAsync.</summary>
    internal async Task JoinOnlineAsync(Guid calendarItemId)
    {
        var calendarItem = await Services.GetRequiredService<ICalendarService>().GetCalendarItemAsync(calendarItemId);
        if (!CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out var joinUri)) return;
        await Services.GetRequiredService<IExternalLauncher>().LaunchUriAsync(joinUri);
    }

    #endregion

    #region Mail

    /// <summary>App.HandleToastNavigationAsync.</summary>
    internal async Task NavigateMailAsync(Guid mailUniqueId)
    {
        var mailService = Services.GetRequiredService<IMailService>();
        var account = await mailService.GetMailAccountByUniqueIdAsync(mailUniqueId);
        var mailItem = account is null ? null : await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (account is null || mailItem?.AssignedFolder is null)
        {
            Serilog.Log.Information("Notification mail {Id} was not found; opening mail.", mailUniqueId);
            await Navigation.EnsureShellAsync(WinoApplicationMode.Mail);
            return;
        }

        var navigation = Navigation;
        var mailReady = navigation.IsShellReady && navigation.Shell?.ActiveMode == WinoApplicationMode.Mail;
        // A new or switching shell consumes this after its mail menu has been rebuilt.
        if (!mailReady)
            Services.GetRequiredService<IActivationStateService>().LaunchParameter = new AccountMenuItemExtended(mailItem.AssignedFolder.Id, mailItem);

        if (!await navigation.EnsureShellAsync(WinoApplicationMode.Mail) || !mailReady) return;

        var shell = Services.GetRequiredService<MailAppShellViewModel>();
        await RunOnUIAsync(async () =>
        {
            var navigated = false;
            if (shell.MenuItems.TryGetFolderMenuItem(mailItem.AssignedFolder.Id, out IBaseFolderMenuItem folderMenuItem))
            {
                await shell.NavigateFolderAsync(folderMenuItem);
                navigated = true;
            }
            else if (shell.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
            {
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
                if (shell.MenuItems.TryGetFolderMenuItem(mailItem.AssignedFolder.Id, out folderMenuItem))
                {
                    await shell.NavigateFolderAsync(folderMenuItem);
                    navigated = true;
                }
            }
            if (navigated) WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(mailUniqueId, ScrollToItem: true));
            else Serilog.Log.Information("Notification folder for mail {Id} was not found.", mailUniqueId);
        });
    }

    /// <summary>App.HandleToastActionAsync (the app is running here, so the delegator queues the request).</summary>
    private async Task ExecuteMailActionAsync(MailOperation action, Guid mailUniqueId)
    {
        var mailItem = await Services.GetRequiredService<IMailService>().GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null) return;
        await Services.GetRequiredService<IWinoRequestDelegator>().ExecuteAsync(new MailOperationPreperationRequest(action, mailItem));
        await Services.GetRequiredService<INotificationBuilder>().UpdateTaskbarIconBadgeAsync();
    }

    private sealed record ComposeSource(MailAccount Account, MailCopy MailItem, MimeKit.MimeMessage Mime);

    private async Task<ComposeSource?> LoadComposeSourceAsync(Guid mailUniqueId)
    {
        var mailService = Services.GetRequiredService<IMailService>();
        var mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);
        if (mailItem is null) return null;
        var account = await mailService.GetMailAccountByUniqueIdAsync(mailUniqueId) ?? mailItem.AssignedAccount;
        if (account is null) return null;
        if (await Services.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft) is null)
        {
            Serilog.Log.Information("Notification compose: the draft folder of account {Id} is missing.", account.Id);
            return null;
        }
        var mime = await Services.GetRequiredService<IMimeFileService>().GetMimeMessageInformationAsync(mailItem.FileId, account.Id);
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
            InitialBodyText = initialText,
            ReferencedMessage = new ReferencedMessage { MimeMessage = source.Mime, MailCopy = source.MailItem }
        };
        var (draft, base64) = await Services.GetRequiredService<IMailService>().CreateDraftAsync(source.Account.Id, options);
        await Services.GetRequiredService<IWinoRequestDelegator>().ExecuteAsync(
            new DraftPreparationRequest(source.Account, draft, base64, options.Reason, source.MailItem));
        return (draft, base64);
    }

    /// <summary>App.HandleToastComposeActionAsync.</summary>
    private async Task ComposeAsync(MailOperation action, Guid mailUniqueId)
    {
        var source = await LoadComposeSourceAsync(mailUniqueId);
        if (source is null)
        {
            await NavigateMailAsync(mailUniqueId);
            return;
        }
        if (!await ShowDraftsAsync(source.Account.Id)) return;
        var (draft, _) = await CreateDraftAsync(source, action);
        await OpenComposerAsync(draft);
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
            var sentFolder = await Services.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(source.Account.Id, SpecialFolderType.Sent);
            if (alias is not null && sentFolder is not null && draft.AssignedFolder is not null)
            {
                await Services.GetRequiredService<IWinoRequestDelegator>().ExecuteAsync(
                    new SendDraftPreparationRequest(draft, alias, sentFolder, draft.AssignedFolder, source.Account.Preferences, base64));
                return;
            }
            Serilog.Log.Information("Inline reply could not be sent directly; opening the composer.");
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Inline reply failed; opening the composer.");
        }

        if (!await ShowDraftsAsync(source.Account.Id)) return;
        if (draft is null) (draft, _) = await CreateDraftAsync(source, MailOperation.Reply, text);
        await OpenComposerAsync(draft);
    }

    /// <summary>The alias in the draft's From header (MailService picks it from the original message), else the primary one.</summary>
    private async Task<MailAccountAlias?> ResolveSendingAliasAsync(Guid accountId, string base64Mime)
    {
        var accountService = Services.GetRequiredService<IAccountService>();
        var from = base64Mime.GetMimeMessageFromBase64().From.Mailboxes.FirstOrDefault()?.Address;
        if (!string.IsNullOrWhiteSpace(from))
        {
            var aliases = await accountService.GetAccountAliasesAsync(accountId);
            var match = aliases.FirstOrDefault(alias => string.Equals(alias.AliasAddress, from, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return await accountService.GetPrimaryAccountAliasAsync(accountId);
    }

    /// <summary>Shows mail mode on the account's Drafts folder so the reading pane can host the composer.</summary>
    private async Task<bool> ShowDraftsAsync(Guid accountId)
    {
        if (!await Navigation.EnsureShellAsync(WinoApplicationMode.Mail)) return false;
        var shell = Services.GetRequiredService<MailAppShellViewModel>();
        await RunOnUIAsync(async () =>
        {
            if (shell.MenuItems.TryGetAccountMenuItem(accountId, out IAccountMenuItem accountMenuItem))
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
            if (shell.MenuItems.TryGetSpecialFolderMenuItem(accountId, SpecialFolderType.Draft, out var draftFolderMenuItem))
                await shell.NavigateFolderAsync(draftFolderMenuItem);
        });
        return true;
    }

    private Task OpenComposerAsync(MailCopy draft)
        => RunOnUIAsync(() =>
        {
            Navigation.Navigate(WinoPage.ComposePage, new MailItemViewModel(draft) { ShouldFocusComposerOnOpen = true },
                NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
            return Task.CompletedTask;
        });

    #endregion

    private async Task RunOnUIAsync(Func<Task> action)
    {
        Task inner = Task.CompletedTask;
        await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => inner = action());
        await inner;
    }
}
