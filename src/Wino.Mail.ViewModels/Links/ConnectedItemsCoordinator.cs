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
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Links;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Requests.Tasks;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.Links;

/// <summary>
/// Creates a task or an event from another item and keeps the link between them, and lists and opens linked items.
/// Every head runs these rules; it only shows the dialogs and the rows.
/// </summary>
public sealed partial class ConnectedItemsCoordinator(
    IItemLinkService itemLinks,
    ITaskService taskService,
    IAccountService accountService,
    ICalendarService calendarService,
    IMailService mailService,
    IMimeFileService mimeFileService,
    IMailDialogService dialogService,
    IWinoRequestDelegator requestDelegator,
    IPreferencesService preferencesService,
    INavigationService navigationService,
    INotificationResponseNavigator mailNavigator,
    IDispatcher dispatcher)
{
    private const int NotesSnippetLength = 300;

    private readonly ILogger _logger = Log.ForContext<ConnectedItemsCoordinator>();

    public static bool IsConnectedMailOperation(MailOperation operation)
        => operation is MailOperation.CreateTask or MailOperation.CreateEvent;

    /// <summary>Runs Create task or Create event for the first of the selected mails.</summary>
    public Task ExecuteMailOperationAsync(MailOperation operation, IEnumerable<MailCopy> mails)
    {
        var mail = mails?.FirstOrDefault(static item => item is not null && !item.IsDraft);
        if (mail is null)
            return Task.CompletedTask;

        return operation switch
        {
            MailOperation.CreateTask => CreateTaskFromMailAsync(mail),
            MailOperation.CreateEvent => CreateEventFromMailAsync(mail),
            _ => Task.CompletedTask
        };
    }

    public async Task CreateTaskFromMailAsync(MailCopy mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var mailAccountId = GetMailAccountId(mail);
        var title = string.IsNullOrWhiteSpace(mail.Subject) ? Translator.MailItemNoSubject : mail.Subject.Trim();

        await CreateTaskAsync(
            new TaskSeed(title, Snippet(mail.PreviewText), null, mailAccountId),
            new ItemLink
            {
                SourceKind = ItemLinkKind.Mail,
                SourceAccountId = mailAccountId,
                SourceKey = ItemLinkKeys.ForMail(mail),
                SourceTitle = title,
                SourceSubtitle = string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName,
                TargetKind = ItemLinkKind.Task,
                Relation = ItemLinkRelation.CreatedFrom
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows the prefilled task, then queues its create. The link is held until the provider confirms the task.
    /// </summary>
    private async Task CreateTaskAsync(TaskSeed seed, ItemLink link)
    {
        var lists = (await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [])
            .Where(static list => !list.IsReadOnly)
            .ToList();

        if (lists.Count == 0)
        {
            dialogService.InfoBarMessage(Translator.ConnectedItems_NoTaskListTitle, Translator.ConnectedItems_NoTaskListMessage, InfoBarMessageType.Warning);
            return;
        }

        var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var orderedLists = lists
            .OrderBy(list => accounts.FindIndex(account => account.Id == list.MailAccountId) is var index && index >= 0 ? index : int.MaxValue)
            .ThenByDescending(static list => list.IsDefault)
            .ThenBy(static list => list.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var draft = new TaskFromItemDraft
        {
            Title = seed.Title,
            Notes = seed.Notes,
            DueDate = seed.DueDate,
            Lists = orderedLists,
            Accounts = accounts,
            SelectedListId = ChooseDefaultList(orderedLists, seed.PreferredAccountId)?.Id
        };

        var result = await ShowTaskDialogAsync(draft).ConfigureAwait(false);
        if (result?.List is not { } destination)
            return;

        var title = string.IsNullOrWhiteSpace(result.Title) ? seed.Title : result.Title.Trim();
        var task = new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = title,
            Notes = result.Notes?.Trim() ?? string.Empty,
            DueDate = result.DueDate?.Date
        };

        link.TargetKind = ItemLinkKind.Task;
        link.TargetAccountId = destination.MailAccountId;
        link.TargetTitle = title;
        itemLinks.ExpectLink(task.Id, link);

        try
        {
            await requestDelegator.ExecuteAsync(destination.MailAccountId, (IRequestBase[])[new TaskActionRequest(destination.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: task)]).ConfigureAwait(false);
        }
        catch (AppLockedException)
        {
            itemLinks.DiscardPendingLinks(task.Id);
            dialogService.InfoBarMessage(Translator.AppLock_LockedTitle, Translator.AppLock_ActionDenied, InfoBarMessageType.Warning);
            return;
        }
        catch (Exception ex)
        {
            itemLinks.DiscardPendingLinks(task.Id);
            _logger.Error(ex, "Could not queue a task created from a {Kind}.", link.SourceKind);
            dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
            return;
        }

        preferencesService.LastUsedTaskListId = destination.Id;
        dialogService.InfoBarMessage(
            Translator.ConnectedItems_TaskCreatedTitle,
            string.Format(CultureInfo.CurrentCulture, Translator.ConnectedItems_TaskCreatedMessage, title, destination.Title),
            InfoBarMessageType.Success,
            Translator.Buttons_Open,
            () => _ = OpenTaskAsync(task.Id));
    }

    private AccountTaskList? ChooseDefaultList(IReadOnlyList<AccountTaskList> lists, Guid? preferredAccountId)
        => lists.FirstOrDefault(list => list.Id == preferencesService.LastUsedTaskListId)
           ?? (preferredAccountId is Guid accountId
               ? lists.FirstOrDefault(list => list.MailAccountId == accountId && list.IsDefault) ?? lists.FirstOrDefault(list => list.MailAccountId == accountId)
               : null)
           ?? lists.FirstOrDefault(static list => list.IsDefault)
           ?? lists.FirstOrDefault();

    #region Linked items

    /// <summary>The items linked to the item of <paramref name="kind"/> known under <paramref name="keys"/>.</summary>
    public async Task<List<LinkedItemViewModel>> GetLinkedItemsAsync(ItemLinkKind kind, IReadOnlyCollection<string> keys)
    {
        var result = new List<LinkedItemViewModel>();
        if (keys is null || keys.Count == 0)
            return result;

        List<ItemLink> links;
        try
        {
            links = await itemLinks.GetLinksAsync(kind, keys).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not read the links of a {Kind}.", kind);
            return result;
        }

        var seen = new HashSet<(ItemLinkKind, string)>();
        foreach (var link in links)
        {
            var isSource = link.SourceKind == kind && keys.Contains(link.SourceKey);
            var otherKind = isSource ? link.TargetKind : link.SourceKind;
            var otherKey = isSource ? link.TargetKey : link.SourceKey;
            var otherAccountId = isSource ? link.TargetAccountId : link.SourceAccountId;
            var snapshotTitle = isSource ? link.TargetTitle : link.SourceTitle;

            if (!seen.Add((otherKind, otherKey)))
                continue;

            var item = await ResolveAsync(link, otherKind, otherAccountId, otherKey, snapshotTitle, isSource ? string.Empty : link.SourceSubtitle).ConfigureAwait(false);
            if (item is not null)
                result.Add(item);
        }

        return result;
    }

    private async Task<LinkedItemViewModel?> ResolveAsync(ItemLink link, ItemLinkKind kind, Guid accountId, string key, string snapshotTitle, string snapshotSubtitle)
    {
        try
        {
            switch (kind)
            {
                case ItemLinkKind.Task:
                    // A deleted task loses its links, so a missing one is a create still waiting for the server.
                    if (!Guid.TryParse(key, out var taskId) || await taskService.GetTaskAsync(taskId).ConfigureAwait(false) is not { } task)
                        return null;
                    return new LinkedItemViewModel(link, kind, task.MailAccountId, key, task.Title, string.Empty, true, task.IsCompleted);

                case ItemLinkKind.Mail:
                    var mail = await FindMailAsync(accountId, key).ConfigureAwait(false);
                    return mail is null
                        ? new LinkedItemViewModel(link, kind, accountId, key, snapshotTitle, Translator.ConnectedItems_MailUnavailable, false, false)
                        : new LinkedItemViewModel(link, kind, accountId, key, mail.Subject, string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName, true, false);

                case ItemLinkKind.Event:
                    var calendarItem = await calendarService.GetCalendarItemByLinkKeyAsync(accountId, key).ConfigureAwait(false);
                    return calendarItem is null
                        ? new LinkedItemViewModel(link, kind, accountId, key, snapshotTitle, Translator.ConnectedItems_EventUnavailable, false, false)
                        : new LinkedItemViewModel(link, kind, accountId, key, calendarItem.Title, calendarItem.LocalStartDate.ToString("g", CultureInfo.CurrentCulture), true, false);

                default:
                    return new LinkedItemViewModel(link, kind, accountId, key, snapshotTitle, snapshotSubtitle, false, false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Could not resolve a linked {Kind}.", kind);
            return null;
        }
    }

    public Task OpenLinkedItemAsync(LinkedItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return item.Kind switch
        {
            ItemLinkKind.Mail => OpenMailAsync(item.AccountId, item.Key),
            ItemLinkKind.Task when Guid.TryParse(item.Key, out var taskId) => OpenTaskAsync(taskId),
            ItemLinkKind.Event => OpenEventAsync(item.AccountId, item.Key),
            _ => Task.CompletedTask
        };
    }

    public async Task OpenMailAsync(Guid accountId, string mailKey)
    {
        var mail = await FindMailAsync(accountId, mailKey).ConfigureAwait(false);
        if (mail is null)
        {
            dialogService.InfoBarMessage(Translator.ConnectedItems_ItemUnavailableTitle, Translator.ConnectedItems_MailUnavailable, InfoBarMessageType.Warning);
            return;
        }

        await OpenMailAsync(mail).ConfigureAwait(false);
    }

    public Task OpenMailAsync(MailCopy mail)
        => mailNavigator.OpenMailAsync(GetMailAccountId(mail), mail.FolderId, mail);

    public async Task OpenTaskAsync(Guid taskId)
    {
        if (await taskService.GetTaskAsync(taskId).ConfigureAwait(false) is null)
        {
            dialogService.InfoBarMessage(Translator.ConnectedItems_ItemUnavailableTitle, Translator.ConnectedItems_TaskUnavailable, InfoBarMessageType.Warning);
            return;
        }

        await dispatcher.ExecuteOnUIThread(() =>
            navigationService.ChangeApplicationMode(WinoApplicationMode.Tasks, new ShellModeActivationContext { Parameter = new ToDoNavigationArgs(taskId) }))
            .ConfigureAwait(false);
    }

    public async Task OpenEventAsync(Guid accountId, string eventKey)
    {
        var calendarItem = await calendarService.GetCalendarItemByLinkKeyAsync(accountId, eventKey).ConfigureAwait(false);
        if (calendarItem is null)
        {
            dialogService.InfoBarMessage(Translator.ConnectedItems_ItemUnavailableTitle, Translator.ConnectedItems_EventUnavailable, InfoBarMessageType.Warning);
            return;
        }

        var navigationArgs = new CalendarPageNavigationArgs
        {
            NavigationDate = calendarItem.LocalStartDate,
            PendingTarget = new CalendarItemTarget(calendarItem, CalendarEventTargetType.Single)
        };

        await dispatcher.ExecuteOnUIThread(() =>
            navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar, new ShellModeActivationContext { Parameter = navigationArgs }))
            .ConfigureAwait(false);
    }

    private async Task<MailCopy?> FindMailAsync(Guid accountId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return ItemLinkKeys.TryGetMailCopyId(key, out var mailCopyId)
            ? await mailService.GetSingleMailItemAsync(mailCopyId).ConfigureAwait(false)
            : await mailService.GetMailCopyByMessageIdAsync(accountId, key).ConfigureAwait(false);
    }

    #endregion

    private static Guid GetMailAccountId(MailCopy mail)
        => mail.AssignedAccount?.Id ?? mail.AssignedFolder?.MailAccountId ?? Guid.Empty;

    private static string Snippet(string? text)
    {
        var normalized = string.Join(' ', (text ?? string.Empty).Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= NotesSnippetLength ? normalized : normalized[..NotesSnippetLength].TrimEnd() + "…";
    }

    private async Task<TaskFromItemResult?> ShowTaskDialogAsync(TaskFromItemDraft draft)
    {
        Task<TaskFromItemResult?>? dialog = null;
        await dispatcher.ExecuteOnUIThread(() => dialog = dialogService.ShowCreateTaskFromItemDialogAsync(draft)).ConfigureAwait(false);
        return dialog is null ? null : await dialog.ConfigureAwait(false);
    }

    private sealed record TaskSeed(string Title, string Notes, DateTime? DueDate, Guid? PreferredAccountId);
}
