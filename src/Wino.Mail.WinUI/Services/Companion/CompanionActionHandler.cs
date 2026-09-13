using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Services.Companion;

internal sealed class CompanionActionHandler(
    IServiceProvider services,
    CompanionNavigationCallbacks navigation) : ICompanionActionHandler
{
    private const string TraceSource = "tray-companion";

    public Task OpenWinoAsync(CancellationToken cancellationToken) => navigation.OpenWino(cancellationToken);

    public Task OpenCalendarAsync(CancellationToken cancellationToken) => navigation.OpenCalendar(cancellationToken);

    public Task OpenTasksAsync(CancellationToken cancellationToken) => navigation.OpenTasks(cancellationToken);

    public Task OpenInboxAsync(Guid? accountId, CancellationToken cancellationToken)
        => navigation.OpenInbox(accountId, cancellationToken);

    public async Task OpenMailAsync(MailItemViewModel mail, CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<IMailService>()
            .GetSingleMailItemAsync(mail.Id)
            .ConfigureAwait(false);
        if (stored?.AssignedAccount?.Id == mail.MailCopy.AssignedAccount?.Id)
            await navigation.OpenMail(stored.AssignedAccount.Id, stored.UniqueId, cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<ICalendarService>()
            .GetCalendarItemAsync(calendarItem.Id)
            .ConfigureAwait(false);
        if (stored?.AssignedCalendar?.AccountId == calendarItem.AssignedCalendar?.AccountId)
            await navigation.OpenCalendarEvent(stored.AssignedCalendar.AccountId, stored.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task JoinCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<ICalendarService>()
            .GetCalendarItemAsync(calendarItem.Id)
            .ConfigureAwait(false);
        if (stored?.AssignedCalendar?.AccountId == calendarItem.AssignedCalendar?.AccountId)
            await navigation.JoinCalendarEvent(stored.AssignedCalendar.AccountId, stored.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task FindContactAsync(AccountContactViewModel? contact, CancellationToken cancellationToken)
        => navigation.FindContact(contact?.Address, cancellationToken);

    public Task NewMailAsync(CancellationToken cancellationToken) => navigation.NewMail(null, cancellationToken);

    public Task NewEventAsync(CancellationToken cancellationToken) => navigation.NewEvent(null, null, cancellationToken);

    public Task OpenSettingsAsync(CancellationToken cancellationToken) => navigation.OpenSettings(cancellationToken);

    public Task SetNotificationsPausedAsync(bool isPaused, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        services.GetRequiredService<IPreferencesService>().SnoozeNotifications = isPaused;
        return Task.CompletedTask;
    }

    public async Task SetMailReadAsync(MailItemViewModel mail, bool isRead, CancellationToken cancellationToken)
    {
        var operation = isRead ? MailOperation.MarkAsRead : MailOperation.MarkAsUnread;
        await DispatchMailAsync(mail, operation, cancellationToken).ConfigureAwait(false);
    }

    public Task ArchiveMailAsync(MailItemViewModel mail, CancellationToken cancellationToken)
        => DispatchMailAsync(mail, MailOperation.Archive, cancellationToken);

    public async Task SetTaskCompletedAsync(TaskItemViewModel task, bool isCompleted, CancellationToken cancellationToken)
    {
        var service = services.GetRequiredService<ITaskService>();
        var stored = await service.GetTaskAsync(task.Id).ConfigureAwait(false);
        if (stored?.MailAccountId != task.Task.MailAccountId)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var original = RequestEntityCloner.Task(stored);
        var desired = RequestEntityCloner.Task(stored);
        desired.IsCompleted = isCompleted;
        desired.CompletedAtUtc = isCompleted ? DateTime.UtcNow : null;
        var request = new TaskActionRequest(
            stored.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)
        {
            Trace = new RequestTrace(TraceSource, Guid.NewGuid())
        };

        await services.GetRequiredService<IWinoRequestDelegator>()
            .ExecuteAsync(stored.MailAccountId, (IRequestBase[])[request])
            .ConfigureAwait(false);
    }

    public async Task CreateMyDayTaskAsync(string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;

        var taskService = services.GetRequiredService<ITaskService>();
        var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var preferences = services.GetRequiredService<IPreferencesService>();
        var destination = lists.FirstOrDefault(list =>
                list.Id == preferences.LastUsedTaskListId && !list.IsReadOnly)
            ?? lists.FirstOrDefault(static list => !list.IsReadOnly);
        if (destination is null)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var taskId = Guid.NewGuid();
        if (await taskService.GetTaskAsync(taskId).ConfigureAwait(false) is not null)
            return;

        var task = new AccountTask
        {
            Id = taskId,
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = title.Trim(),
            MyDayDateUtc = DateTime.UtcNow.Date
        };
        var request = new TaskActionRequest(
            destination.MailAccountId,
            TaskSynchronizerOperation.CreateTask,
            Task: task)
        {
            Trace = new RequestTrace(TraceSource, Guid.NewGuid())
        };

        await services.GetRequiredService<IWinoRequestDelegator>()
            .ExecuteAsync(destination.MailAccountId, (IRequestBase[])[request])
            .ConfigureAwait(false);
        preferences.LastUsedTaskListId = destination.Id;
    }

    private async Task DispatchMailAsync(
        MailItemViewModel mail,
        MailOperation operation,
        CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<IMailService>()
            .GetSingleMailItemAsync(mail.Id)
            .ConfigureAwait(false);
        if (stored?.AssignedAccount?.Id != mail.MailCopy.AssignedAccount?.Id)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        var requests = await services.GetRequiredService<IWinoRequestProcessor>()
            .PrepareRequestsAsync(new MailOperationPreperationRequest(operation, stored))
            .ConfigureAwait(false);
        if (requests is null || requests.Count == 0)
            return;

        SetTrace(requests);
        await services.GetRequiredService<IWinoRequestDelegator>()
            .ExecuteAsync(stored.AssignedAccount.Id, requests)
            .ConfigureAwait(false);
    }

    private static void SetTrace(IEnumerable<IRequestBase> requests)
    {
        var operationId = Guid.NewGuid();
        foreach (var request in requests)
            request.Trace = new RequestTrace(TraceSource, operationId);
    }
}
