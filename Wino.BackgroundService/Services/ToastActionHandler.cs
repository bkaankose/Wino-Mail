using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Serilog;
using Wino.BackgroundService.Tray;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Handles toast activation arguments inside the companion. Quick actions that need no
/// UI (mark read, delete, archive, junk) execute directly against the local services —
/// this replaces the old "launch the WinUI app, sync, exit" path. Anything that needs
/// UI (navigate, reply, calendar) is forwarded to the Mail app entry.
/// </summary>
public sealed class ToastActionHandler
{
    private static readonly MailOperation[] QuickActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.Archive,
        MailOperation.MoveToJunk,
    ];

    private readonly IMailService _mailService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly PackagedAppEntryLauncher _appEntryLauncher;
    private readonly ILogger _logger = Log.ForContext<ToastActionHandler>();

    public ToastActionHandler(IMailService mailService,
                              IWinoRequestDelegator requestDelegator,
                              INotificationBuilder notificationBuilder,
                              PackagedAppEntryLauncher appEntryLauncher)
    {
        _mailService = mailService;
        _requestDelegator = requestDelegator;
        _notificationBuilder = notificationBuilder;
        _appEntryLauncher = appEntryLauncher;
    }

    public async Task HandleAsync(string toastArguments)
    {
        if (string.IsNullOrWhiteSpace(toastArguments))
            return;

        var arguments = ParseArguments(toastArguments);

        // Dismiss only.
        if (arguments.ContainsKey(Constants.ToastDismissActionKey))
            return;

        if (arguments.TryGetValue(Constants.ToastActionKey, out var actionString) &&
            Enum.TryParse<MailOperation>(actionString, out var action) &&
            QuickActions.Contains(action) &&
            arguments.TryGetValue(Constants.ToastMailUniqueIdKey, out var mailIdString) &&
            Guid.TryParse(mailIdString, out var mailUniqueId))
        {
            await ExecuteQuickActionAsync(action, mailUniqueId).ConfigureAwait(false);
            return;
        }

        // Everything else needs the UI process.
        _logger.Information("Forwarding toast activation to the Mail UI.");
        _appEntryLauncher.TryActivateMailWithArguments(toastArguments);
    }

    private async Task ExecuteQuickActionAsync(MailOperation action, Guid mailUniqueId)
    {
        _logger.Information("Executing toast quick action {Action} for {MailUniqueId} in companion.", action, mailUniqueId);

        var mailItem = await _mailService.GetSingleMailItemAsync(mailUniqueId).ConfigureAwait(false);

        if (mailItem == null)
        {
            _logger.Warning("Toast quick action target mail was not found.");
            return;
        }

        var package = new MailOperationPreperationRequest(action, mailItem);
        await _requestDelegator.ExecuteAsync(package).ConfigureAwait(false);

        _notificationBuilder.RemoveNotification(mailUniqueId);
        await _notificationBuilder.UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseArguments(string encodedArguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in encodedArguments.Split([';', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');

            if (separatorIndex < 0)
            {
                values[WebUtility.UrlDecode(pair)] = string.Empty;
                continue;
            }

            values[WebUtility.UrlDecode(pair[..separatorIndex])] = WebUtility.UrlDecode(pair[(separatorIndex + 1)..]);
        }

        return values;
    }
}
