using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Services;

namespace Wino.SyncHost;

internal sealed class SyncHostNotificationActivationRouter
{
    private readonly IMailService _mailService;
    private readonly ICalendarService _calendarService;
    private readonly IWinoRequestProcessor _requestProcessor;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPreferencesService _preferencesService;
    private readonly INativeAppService _nativeAppService;
    private readonly PackagedAppEntryLauncher _appEntryLauncher;

    public SyncHostNotificationActivationRouter(
        IMailService mailService,
        ICalendarService calendarService,
        IWinoRequestProcessor requestProcessor,
        ISynchronizationManager synchronizationManager,
        INotificationBuilder notificationBuilder,
        IPreferencesService preferencesService,
        INativeAppService nativeAppService,
        PackagedAppEntryLauncher appEntryLauncher)
    {
        _mailService = mailService;
        _calendarService = calendarService;
        _requestProcessor = requestProcessor;
        _synchronizationManager = synchronizationManager;
        _notificationBuilder = notificationBuilder;
        _preferencesService = preferencesService;
        _nativeAppService = nativeAppService;
        _appEntryLauncher = appEntryLauncher;
    }

    public void Start()
    {
        var notificationManager = AppNotificationManager.Default;

        notificationManager.NotificationInvoked -= OnNotificationInvoked;
        notificationManager.NotificationInvoked += OnNotificationInvoked;

        TryHandleInitialActivation();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        => _ = HandleAsync(args.Argument, args.UserInput);

    private void TryHandleInitialActivation()
    {
        try
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activationArgs.Kind == ExtendedActivationKind.AppNotification &&
                activationArgs.Data is AppNotificationActivatedEventArgs toastArgs)
            {
                Log.Information("Processing startup app notification activation in sync host. Arguments: {Arguments}", toastArgs.Argument);
                _ = HandleAsync(toastArgs.Argument, toastArgs.UserInput);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve startup app notification activation in sync host.");
        }
    }

    private async Task HandleAsync(string arguments, IDictionary<string, string>? userInput)
    {
        if (!TryParse(arguments, out var toastArguments))
        {
            Log.Debug("Ignoring notification activation without known Wino arguments: {Arguments}", arguments);
            return;
        }

        try
        {
            if (await TryHandleStoreUpdateAsync(toastArguments).ConfigureAwait(false))
                return;

            if (await TryHandleDismissAsync(toastArguments).ConfigureAwait(false))
                return;

            if (await TryHandleCalendarAsync(toastArguments, userInput).ConfigureAwait(false))
                return;

            if (await TryHandleMailAsync(toastArguments).ConfigureAwait(false))
                return;

            Log.Debug("No sync host notification activation route matched. Arguments: {Arguments}", arguments);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle sync host notification activation. Arguments: {Arguments}", arguments);
        }
    }

    private async Task<bool> TryHandleStoreUpdateAsync(NotificationArguments toastArguments)
    {
        if (!toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string storeUpdateAction) ||
            storeUpdateAction != Constants.ToastStoreUpdateActionInstall)
        {
            return false;
        }

        await _appEntryLauncher.LaunchAsync(WinoApplicationMode.Mail).ConfigureAwait(false);
        return true;
    }

    private static Task<bool> TryHandleDismissAsync(NotificationArguments toastArguments)
    {
        if (!toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _))
            return Task.FromResult(false);

        Log.Debug("Handled notification dismiss activation in sync host.");
        return Task.FromResult(true);
    }

    private async Task<bool> TryHandleCalendarAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput)
    {
        if (!toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) ||
            !toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdText) ||
            !Guid.TryParse(calendarItemIdText, out var calendarItemId))
        {
            return false;
        }

        switch (calendarAction)
        {
            case Constants.ToastCalendarSnoozeAction:
                await SnoozeCalendarItemAsync(calendarItemId, userInput).ConfigureAwait(false);
                break;

            case Constants.ToastCalendarJoinOnlineAction:
                await JoinCalendarItemOnlineAsync(calendarItemId).ConfigureAwait(false);
                break;

            case Constants.ToastCalendarNavigateAction:
                await LaunchCalendarNotificationProtocolAsync(calendarItemId).ConfigureAwait(false);
                break;

            default:
                return false;
        }

        return true;
    }

    private async Task SnoozeCalendarItemAsync(Guid calendarItemId, IDictionary<string, string>? userInput)
    {
        if (!TryGetSnoozeDurationMinutes(userInput, out var snoozeDurationMinutes))
            return;

        await _calendarService
            .SnoozeCalendarItemAsync(calendarItemId, DateTime.Now.AddMinutes(snoozeDurationMinutes))
            .ConfigureAwait(false);
    }

    private async Task JoinCalendarItemOnlineAsync(Guid calendarItemId)
    {
        var calendarItem = await _calendarService.GetCalendarItemAsync(calendarItemId).ConfigureAwait(false);
        if (calendarItem == null ||
            !Uri.TryCreate(calendarItem.HtmlLink, UriKind.Absolute, out var joinUri))
        {
            return;
        }

        await _nativeAppService.LaunchUriAsync(joinUri).ConfigureAwait(false);
    }

    private bool TryGetSnoozeDurationMinutes(IDictionary<string, string>? userInput, out int snoozeDurationMinutes)
    {
        snoozeDurationMinutes = _preferencesService.DefaultSnoozeDurationInMinutes;

        if (userInput == null ||
            !userInput.TryGetValue(Constants.ToastCalendarSnoozeDurationInputId, out var selectedValue) ||
            selectedValue == null)
        {
            return snoozeDurationMinutes > 0;
        }

        return int.TryParse(selectedValue.ToString(), out snoozeDurationMinutes) && snoozeDurationMinutes > 0;
    }

    private async Task<bool> TryHandleMailAsync(NotificationArguments toastArguments)
    {
        if (!toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) ||
            !toastArguments.TryGetValue(Constants.ToastMailUniqueIdKey, out string mailItemIdText) ||
            !Guid.TryParse(mailItemIdText, out var mailItemUniqueId))
        {
            return false;
        }

        if (action is MailOperation.Navigate)
        {
            await LaunchMailNotificationProtocolAsync(mailItemUniqueId, action).ConfigureAwait(false);
            return true;
        }

        if (IsComposeToastAction(action))
        {
            await LaunchMailNotificationProtocolAsync(mailItemUniqueId, action).ConfigureAwait(false);
            return true;
        }

        if (!IsHeadlessMailToastAction(action))
        {
            Log.Warning("Unsupported headless mail notification action {Action}. Launching UI.", action);
            await _appEntryLauncher.LaunchAsync(WinoApplicationMode.Mail).ConfigureAwait(false);
            return true;
        }

        await ExecuteMailActionAsync(action, mailItemUniqueId).ConfigureAwait(false);
        return true;
    }

    private async Task ExecuteMailActionAsync(MailOperation action, Guid mailItemUniqueId)
    {
        Log.Information("Handling headless mail notification action {Action} for {MailItemUniqueId}", action, mailItemUniqueId);

        var mailItem = await _mailService.GetSingleMailItemAsync(mailItemUniqueId).ConfigureAwait(false);
        if (mailItem == null)
        {
            Log.Warning("Mail notification action target was not found. MailItemUniqueId: {MailItemUniqueId}", mailItemUniqueId);
            return;
        }

        var requestPackage = new MailOperationPreperationRequest(action, mailItem);
        var requests = await _requestProcessor.PrepareRequestsAsync(requestPackage).ConfigureAwait(false);
        if (requests == null || requests.Count == 0)
            return;

        foreach (var accountGroup in requests.GroupBy(request => request.Item.AssignedAccount.Id))
        {
            var accountId = accountGroup.Key;
            await _synchronizationManager.QueueRequestsAsync(accountGroup, accountId, triggerSynchronization: false).ConfigureAwait(false);

            await _synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions
            {
                AccountId = accountId,
                Type = MailSynchronizationType.ExecuteRequests
            }).ConfigureAwait(false);
        }

        await _notificationBuilder.UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
    }

    private Task<bool> LaunchMailNotificationProtocolAsync(Guid mailItemUniqueId, MailOperation action)
        => _nativeAppService.LaunchUriAsync(new Uri($"wino://notification/mail/{mailItemUniqueId}?action={action}"));

    private Task<bool> LaunchCalendarNotificationProtocolAsync(Guid calendarItemId)
        => _nativeAppService.LaunchUriAsync(new Uri($"wino://notification/calendar/{calendarItemId}?action=Navigate"));

    private static bool TryParse(string? argument, out NotificationArguments toastArguments)
    {
        toastArguments = default!;

        if (string.IsNullOrWhiteSpace(argument))
            return false;

        try
        {
            var parsedArguments = NotificationArguments.Parse(argument);
            if (!ContainsKnownToastKey(parsedArguments))
                return false;

            toastArguments = parsedArguments;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsKnownToastKey(NotificationArguments toastArguments)
        => toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _) ||
           toastArguments.TryGetValue(Constants.ToastActionKey, out string _);

    private static bool IsComposeToastAction(MailOperation action)
        => action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    private static bool IsHeadlessMailToastAction(MailOperation action)
        => action is MailOperation.MarkAsRead
            or MailOperation.SoftDelete
            or MailOperation.MoveToJunk
            or MailOperation.Archive;
}
