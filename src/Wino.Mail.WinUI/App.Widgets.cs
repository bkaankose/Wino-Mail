using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;
using Wino.Mail.ViewModels;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.WinUI;

/// <summary>
/// Actions from the Widgets Board. Wino.WidgetHost forwards them as notification activations; each one opens the same
/// place its companion counterpart does.
/// </summary>
public partial class App
{
    private Task HandleWidgetActionAsync(string action, string? value)
    {
        LogActivation($"Handling widget action {action}.");

        return ExecuteCompanionNavigationAsync(() => ExecuteWidgetActionAsync(action, value), CancellationToken.None);
    }

    private async Task ExecuteWidgetActionAsync(string action, string? value)
    {
        if (action == WidgetActions.EnableWidgets)
        {
            await OpenWidgetsSettingsAsync();
            return;
        }

        if (action == WidgetActions.OpenWino || !_hasConfiguredAccounts)
        {
            await ActivatePreferredWindowAsync();
            return;
        }

        var id = Guid.TryParse(value, out var parsed) ? parsed : (Guid?)null;

        switch (action)
        {
            case WidgetActions.OpenMail when id is { } mailId:
                await HandleToastNavigationAsync(mailId);
                break;
            case WidgetActions.NewMail:
                await OpenCompanionNewMailAsync(null);
                break;
            case WidgetActions.OpenEvent when id is { } eventId:
                await HandleCalendarToastNavigationAsync(eventId);
                break;
            case WidgetActions.OpenEvent:
                await EnsureShellWindowAsync(WinoApplicationMode.Calendar, activateWindow: true);
                break;
            case WidgetActions.JoinEvent when id is { } joinId:
                await HandleCalendarToastJoinOnlineAsync(joinId);
                break;
            case WidgetActions.NewEvent:
                await OpenCompanionNewEventAsync(null, null);
                break;
            case WidgetActions.OpenTasks:
            case WidgetActions.AddTask:
                await EnsureShellWindowAsync(WinoApplicationMode.Tasks, activateWindow: true);
                break;
            case WidgetActions.OpenContact:
                await EnsureShellWindowAsync(WinoApplicationMode.Contacts, activateWindow: true);
                if (id is { } contactId)
                    await Services.GetRequiredService<ContactsPageViewModel>().LoadAndSelectContactAsync(contactId);
                break;
            case WidgetActions.EmailContact when !string.IsNullOrWhiteSpace(value):
                await HandleMailToProtocolActivationAsync(new MailToUri($"mailto:{Uri.EscapeDataString(value)}"), activateWindow: true);
                break;
            default:
                await OpenCompanionInboxAsync(null);
                break;
        }
    }

    private async Task OpenWidgetsSettingsAsync()
    {
        // Building a new shell straight into Settings crashes Native AOT builds (see OpenCompanionSettingsAsync), so a
        // closed app first comes up in Mail and lets that shell load.
        if (!HasShellWindow())
        {
            await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);

            var dispatcherQueue = GetActivationDispatcherQueue();
            var shellLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (dispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () => shellLoaded.SetResult()) == true)
                await shellLoaded.Task;
        }

        await EnsureShellWindowAsync(
            WinoApplicationMode.Settings,
            activateWindow: true,
            activationParameter: WinoPage.WidgetsLiveTilesSettingsPage);
    }
}
