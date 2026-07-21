using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Launch;
using Wino.Mail.Uwp.Extensions;
using Wino.Messaging.Client.Accounts;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Wino.Mail.Uwp.Activation;

/// <summary>
/// Converts durable, serialization-only activation envelopes into the typed
/// parameters consumed by the production shell ViewModels.
/// </summary>
internal sealed class ActivationPayloadRouter(
    ILaunchProtocolService launchProtocolService,
    IShareActivationService shareActivationService,
    IMailService mailService,
    ICalendarService calendarService)
{
    public async Task<object?> PrepareAsync(ActivationEnvelope envelope)
    {
        if (envelope.TargetSurface == ActivationTargetSurface.Calendar)
        {
            return await PrepareCalendarAsync(envelope);
        }

        return await PrepareMailAsync(envelope);
    }

    private async Task<object?> PrepareMailAsync(ActivationEnvelope envelope)
    {
        if (envelope.Kind == WinoActivationKind.Protocol &&
            Uri.TryCreate(envelope.Arguments, UriKind.Absolute, out var protocolUri) &&
            protocolUri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
        {
            var mailToUri = new MailToUri(protocolUri.AbsoluteUri);
            launchProtocolService.MailToUri = mailToUri;
            return mailToUri;
        }

        if (envelope.Kind is WinoActivationKind.Share or WinoActivationKind.File)
        {
            var sharedFiles = await MaterializeSharedFilesAsync(envelope.SharedStorageTokens);
            if (sharedFiles.Count > 0)
            {
                var shareRequest = new MailShareRequest(sharedFiles);
                shareActivationService.PendingShareRequest = shareRequest;
                return shareRequest;
            }

            return null;
        }

        var arguments = NotificationArguments.Parse(envelope.Arguments);
        if (TryCreateMailFolderRequest(arguments, out var folderRequest))
        {
            return folderRequest;
        }

        if (envelope.Kind == WinoActivationKind.Toast &&
            arguments.TryGetValue(Constants.ToastMailUniqueIdKey, out var mailIdText) &&
            Guid.TryParse(mailIdText, out var mailId) &&
            arguments.TryGetValue(Constants.ToastActionKey, out MailOperation action))
        {
            if (action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward)
            {
                return new MailToastComposeRequest(mailId, action);
            }

            if (action == MailOperation.Navigate)
            {
                var mail = await mailService.GetSingleMailItemAsync(mailId);
                if (mail is not null)
                {
                    var navigation = new AccountMenuItemExtended(mail.AssignedFolder.Id, mail);
                    launchProtocolService.LaunchParameter = navigation;
                    return navigation;
                }
            }
        }

        return null;
    }

    private async Task<CalendarPageNavigationArgs> PrepareCalendarAsync(ActivationEnvelope envelope)
    {
        var fallback = new CalendarPageNavigationArgs { RequestDefaultNavigation = true };
        if (envelope.Kind != WinoActivationKind.Toast)
        {
            return fallback;
        }

        var arguments = NotificationArguments.Parse(envelope.Arguments);
        if (!arguments.TryGetValue(Constants.ToastCalendarActionKey, out var action) ||
            !string.Equals(action, Constants.ToastCalendarNavigateAction, StringComparison.Ordinal) ||
            !arguments.TryGetValue(Constants.ToastCalendarItemIdKey, out var itemIdText) ||
            !Guid.TryParse(itemIdText, out var itemId))
        {
            return fallback;
        }

        var calendarItem = await calendarService.GetCalendarItemAsync(itemId);
        if (calendarItem is null)
        {
            return fallback;
        }

        return new CalendarPageNavigationArgs
        {
            NavigationDate = calendarItem.LocalStartDate,
            PendingTarget = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single),
        };
    }

    private static bool TryCreateMailFolderRequest(
        NotificationArguments arguments,
        out MailFolderLaunchRequest? request)
    {
        request = null;
        if (!arguments.TryGetValue(Constants.JumpListActionKey, out var action) ||
            !string.Equals(action, Constants.JumpListOpenMailFolderAction, StringComparison.Ordinal) ||
            !arguments.TryGetValue(Constants.JumpListAccountIdKey, out var accountIdText) ||
            !arguments.TryGetValue(Constants.JumpListFolderIdKey, out var folderIdText) ||
            !Guid.TryParse(accountIdText, out var accountId) ||
            !Guid.TryParse(folderIdText, out var folderId))
        {
            return false;
        }

        request = new MailFolderLaunchRequest(accountId, folderId);
        return true;
    }

    private static async Task<List<Wino.Core.Domain.Models.Common.SharedFile>> MaterializeSharedFilesAsync(
        IEnumerable<string> tokens)
    {
        var files = new List<Wino.Core.Domain.Models.Common.SharedFile>();
        foreach (var token in tokens)
        {
            try
            {
                var file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token);
                files.Add(await file.ToSharedFileAsync());
            }
            catch (Exception)
            {
                // A single revoked token must not discard other valid share items.
            }
        }

        return files;
    }
}
