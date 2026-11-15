using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Messaging;
using ITransferProgress = MailKit.ITransferProgress;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Core.Synchronizers.Mail;
using Wino.Services;

namespace Wino.Core.Synchronizers.Exchange.ActiveSync;

internal sealed class ActiveSyncExchangeSynchronizer : WinoSynchronizer<EwsRequest, MimeMessage, CalendarItem, AccountContact>
{
    private readonly EasSynchronizationEngine _engine;
    private readonly IMailService _mail;
    private readonly IFolderService _folders;
    private readonly IContactService _contacts;
    private readonly ITaskService _tasks;
    private readonly ICalendarService _calendars;
    private readonly IMimeFileService _mime;
    private readonly IPictureStorageService _pictures;
    private readonly IImapChangeProcessor _changeProcessor;
    private readonly LocalContactSynchronizer _localContacts;
    private readonly LocalTaskSynchronizer _localTasks;
    private readonly ImapSynchronizer.LocalCalendarOperationHandler _localCalendar;

    internal ActiveSyncExchangeSynchronizer(MailAccount account, IDatabaseService database, IAccountService accounts,
        IFolderService folders, IMailService mail, ICalendarService calendars, IContactService contacts, ITaskService tasks,
        IMimeFileService mime, IPictureStorageService pictures, IImapChangeProcessor changeProcessor, string applicationDataFolder,
        EasClient? client = null)
        : base(account, WeakReferenceMessenger.Default)
    {
        _engine = new EasSynchronizationEngine(account, database, accounts, folders, mail, calendars, contacts, tasks, pictures, client);
        _mail = mail; _folders = folders; _contacts = contacts; _tasks = tasks; _calendars = calendars; _mime = mime;
        _pictures = pictures;
        _changeProcessor = changeProcessor;
        _localContacts = new LocalContactSynchronizer(contacts, pictures); _localTasks = new LocalTaskSynchronizer(tasks);
        _localCalendar = new ImapSynchronizer.LocalCalendarOperationHandler(account, changeProcessor, calendars, applicationDataFolder, "local");
    }

    protected override bool SerializeMailOperations => true;
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 100;

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(MimeMessage message, MailItemFolder folder, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("An EAS message requires its server identifier and application data.");

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Type == MailSynchronizationType.FoldersOnly)
        {
            await _engine.SynchronizeFolderMetadataAsync(cancellationToken).ConfigureAwait(false);
            return MailSynchronizationResult.Completed(new List<MailCopy>());
        }
        return MailSynchronizationResult.Completed(await _engine.SynchronizeAsync("Email", cancellationToken).ConfigureAwait(false));
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Calendar", cancellationToken).ConfigureAwait(false);
        return CalendarSynchronizationResult.Empty;
    }

    protected override async Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.ContactIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Contacts", cancellationToken).ConfigureAwait(false);
        return ContactSynchronizationResult.Empty;
    }

    protected override async Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Tasks", cancellationToken).ConfigureAwait(false);
        return TaskSynchronizationResult.Empty;
    }

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> requests, CancellationToken cancellationToken = default)
    {
        ApplyOptimisticUiChanges(requests);
        foreach (var bundle in requests)
        {
            try { await bundle.NativeRequest.IntegratorTask(bundle.Request, cancellationToken).ConfigureAwait(false); }
            catch { RequestUiChangeCoordinator.RevertBundle(bundle); throw; }
            finally { RequestUiChangeCoordinator.CompleteRequests(new[] { bundle.Request }); }
        }
    }

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests) => Bundle(async token =>
    {
        XNamespace email = "Email:";
        foreach (var request in requests)
        {
            if (request.Item.IsLocalDraft)
            {
                await _mail.ChangeReadStatusAsync(request.Item.Id, request.IsRead).ConfigureAwait(false);
                continue;
            }
            var (folder, id) = await RemoteMailAsync(request.Item).ConfigureAwait(false);
            await _engine.MutateAsync(folder.RemoteFolderId, "Change", id,
                new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(email + "Read", request.IsRead ? "1" : "0")), string.Empty, token).ConfigureAwait(false);
            await _mail.ChangeReadStatusAsync(request.Item.Id, request.IsRead).ConfigureAwait(false);
        }
    }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests) => Bundle(async token =>
    {
        foreach (var request in requests)
        {
            if (request.Item.IsLocalDraft)
            {
                await _mail.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
                continue;
            }
            var (folder, id) = await RemoteMailAsync(request.Item).ConfigureAwait(false);
            await _engine.MutateAsync(folder.RemoteFolderId, "Change", id,
                EasDataMapper.WriteFlag(request.IsFlagged), string.Empty, token).ConfigureAwait(false);
            await _mail.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }
    }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests) => Bundle(async token =>
    {
        foreach (var request in requests)
        {
            var (folder, id) = await RemoteMailAsync(request.Item).ConfigureAwait(false);
            await _engine.MoveAsync(folder.RemoteFolderId, id, request.ToFolder.RemoteFolderId, token).ConfigureAwait(false);
        }
    }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest requests)
        => Move(new BatchMoveRequest(requests.Select(request => new MoveRequest(request.Item, request.FromFolder, request.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
        => Move(new BatchMoveRequest(requests.Select(request => new MoveRequest(request.Item, request.FromFolder, request.TargetFolder))));

    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests) => Bundle(async token =>
    {
        foreach (var request in requests)
        {
            if (request.Item.IsLocalDraft)
            {
                await _mail.DiscardLocalDraftAsync(Account.Id, request.Item.UniqueId).ConfigureAwait(false);
                continue;
            }
            var (folder, id) = await RemoteMailAsync(request.Item).ConfigureAwait(false);
            await _engine.MutateAsync(folder.RemoteFolderId, "Delete", id, null, string.Empty, token).ConfigureAwait(false);
            await _mail.DeleteMailAsync(Account.Id, request.Item.Id).ConfigureAwait(false);
        }
    }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(mail => new DeleteRequest(mail))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(mail => new MarkReadRequest(mail, true))));

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderCreate", string.Empty, "0", request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderCreate", string.Empty, request.Folder.RemoteFolderId, request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderUpdate", request.Folder.RemoteFolderId, request.Folder.ParentRemoteFolderId, request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderDelete", request.Folder.RemoteFolderId, string.Empty, string.Empty, 12, token).ConfigureAwait(false), request, request);

    // shortcut: EAS 12.x/14.x cannot upload drafts; compose locally and send with SendMail.
    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request) => Bundle(_ => Task.CompletedTask, request, request);

    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request) => Bundle(async token =>
    {
        using var stream = new MemoryStream();
        await request.Request.Mime.WriteToAsync(stream, token).ConfigureAwait(false);
        await _engine.SendAsync(stream.ToArray(), request.Request.MailItem.UniqueId.ToString("N"), token).ConfigureAwait(false);
        try { await _mail.DeleteMailAsync(Account.Id, request.Request.MailItem.Id).ConfigureAwait(false); }
        catch (Exception exception) { Logger.Error(exception, "EAS accepted message {MailId}, but removing the local draft failed.", request.Request.MailItem.Id); }
    }, request, request);

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mail, ITransferProgress transferProgress = null, CancellationToken cancellationToken = default)
    {
        var (folder, id) = await RemoteMailAsync(mail).ConfigureAwait(false);
        var data = await _engine.FetchAsync(folder.RemoteFolderId, id, cancellationToken).ConfigureAwait(false);
        if (!await _mime.SaveMimeMessageAsync(mail.FileId, EasDataMapper.ReadMime(data), Account.Id).ConfigureAwait(false))
            throw new IOException("The EAS MIME message could not be saved.");
    }

    protected override async Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            if (request is ContactListRequest || request.SourceKind == ContactSourceKind.Local)
            {
                await _localContacts.ExecuteRequestsAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (request is ContactCategoryRequest categories)
            {
                var categoryBook = (await _contacts.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(book => book.Id == categories.AddressBookId);
                await _engine.MutateAsync(EasDataMapper.ServerId(categoryBook.RemoteId), "Change", EasDataMapper.ServerId(categories.Contact.RemoteId),
                    new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(EasDataMapper.Contact + "Categories",
                        categories.CategoryNames.Select(name => new XElement(EasDataMapper.Contact + "Category", name)))), string.Empty, cancellationToken).ConfigureAwait(false);
                await _contacts.SetContactCategoriesAsync(categories.LocalContactId, categories.CategoryNames).ConfigureAwait(false);
                continue;
            }
            if (request is not ContactActionRequest action)
                throw new NotSupportedException("This EAS contact operation is not supported.");

            var book = (await _contacts.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(book => book.Id == action.AddressBookId);
            var operation = action.Operation switch { ContactSynchronizerOperation.Create => "Add", ContactSynchronizerOperation.Delete => "Delete", _ => "Change" };
            var data = operation == "Delete" ? null : EasDataMapper.WriteContact(action.Contact);
            if (operation == "Change" && action.OriginalContact != null && data != null)
                data = EasDataMapper.ChangedFields(data, EasDataMapper.WriteContact(action.OriginalContact));
            if (action.Operation is ContactSynchronizerOperation.SetPhoto or ContactSynchronizerOperation.DeletePhoto)
                data = new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(EasDataMapper.Contact + "Picture",
                    action.Operation == ContactSynchronizerOperation.SetPhoto ? Convert.ToBase64String(action.Photo) : string.Empty));
            async Task PersistContactAsync(string remoteId)
            {
                var committed = RequestEntityCloner.Contact(action.Contact);
                committed.RemoteId = EasDataMapper.ProviderId(remoteId);
                if (action.Operation == ContactSynchronizerOperation.SetPhoto)
                {
                    committed.ContactPictureFileId = await _pictures.SavePictureAsync(PictureKind.Contact, action.Photo).ConfigureAwait(false);
                    committed.RemotePhotoKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(action.Photo));
                }
                await _contacts.CompleteMutationAsync(action.LocalContactId, committed, operation == "Delete").ConfigureAwait(false);
                if (action.Operation == ContactSynchronizerOperation.DeletePhoto)
                    await _contacts.SuppressContactPictureAsync(action.LocalContactId, string.Empty).ConfigureAwait(false);
            }
            await _engine.MutateAsync(EasDataMapper.ServerId(book.RemoteId), operation, EasDataMapper.ServerId(action.Contact.RemoteId), data, action.LocalContactId.ToString("N"),
                cancellationToken, PersistContactAsync).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            if (request is not TaskActionRequest action)
                throw new NotSupportedException("This EAS task operation is not supported.");
            if ((action.Task?.SourceKind ?? action.List?.SourceKind) == TaskSourceKind.Local || action.Step != null || action.Group != null || action.Operation == TaskSynchronizerOperation.UpdateListPlacement)
            {
                await _localTasks.ExecuteRequestsAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }
            if (action.Task == null && action.List != null)
            {
                var folderCommand = action.Operation switch { TaskSynchronizerOperation.CreateList => "FolderCreate",
                    TaskSynchronizerOperation.UpdateList => "FolderUpdate", TaskSynchronizerOperation.DeleteList => "FolderDelete",
                    _ => throw new NotSupportedException("This EAS task list operation is not supported.") };
                if (action.List.IsDefault && folderCommand == "FolderDelete")
                    throw new InvalidOperationException("The default EAS task list cannot be deleted.");
                var id = await _engine.ChangeFolderAsync(folderCommand, EasDataMapper.ServerId(action.List.RemoteId), "0", action.List.Title, 15, cancellationToken).ConfigureAwait(false);
                var committedList = RequestEntityCloner.TaskList(action.List);
                committedList.RemoteId = EasDataMapper.ProviderId(id);
                await _tasks.CompleteListMutationAsync(action.List.Id, committedList, folderCommand == "FolderDelete").ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }
            if (action.Task == null)
                throw new NotSupportedException("This EAS task operation is not supported.");

            var list = await _tasks.GetTaskListAsync(action.Task.TaskListId).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS task list is missing.");
            var operation = action.Operation switch { TaskSynchronizerOperation.CreateTask => "Add", TaskSynchronizerOperation.UpdateTask => "Change",
                TaskSynchronizerOperation.DeleteTask => "Delete", _ => throw new NotSupportedException("This EAS task operation is not supported.") };
            var data = operation == "Delete" ? null : EasDataMapper.WriteTask(action.Task);
            if (operation == "Change" && action.OriginalTask != null && data != null)
                data = EasDataMapper.ChangedFields(data, EasDataMapper.WriteTask(action.OriginalTask));
            await _engine.MutateAsync(EasDataMapper.ServerId(list.RemoteId), operation, EasDataMapper.ServerId(action.Task.RemoteId),
                data, action.Task.Id.ToString("N"), cancellationToken, async remoteId =>
                {
                    var committed = RequestEntityCloner.Task(action.Task);
                    committed.RemoteId = EasDataMapper.ProviderId(remoteId);
                    await _tasks.CompleteTaskMutationAsync(action.Task.Id, committed, operation == "Delete", action.Task, action.Operation).ConfigureAwait(false);
                }).ConfigureAwait(false);
            MarkTaskRequestProcessed(request);
        }
    }

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.CreateCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.ComposeResult.Attachments?.Count > 0)
            throw new NotSupportedException("EAS event attachments are not supported yet.");
        var data = EasDataMapper.WriteEvent(request.PreparedItem, request.PreparedEvent.Attendees, request.PreparedEvent.Reminders);
        data.Add(new XElement(EasDataMapper.Calendar + "UID", request.PreparedItem.Id.ToString("N")));
        await _engine.CreateCalendarEventAsync(request.AssignedCalendar.RemoteCalendarId, data, request.PreparedItem.Id.ToString("N"), async id =>
        {
            var trackedId = id.WithClientTrackingId(request.PreparedItem.Id);
            await _changeProcessor.PersistCreatedCalendarEventAsync(request.PreparedItem, request.PreparedEvent.Attendees,
                request.PreparedEvent.Reminders, trackedId).ConfigureAwait(false);
            var stored = await _calendars.GetCalendarItemAsync(request.AssignedCalendar.Id, id).ConfigureAwait(false);
            if (stored != null && stored.Id != request.PreparedItem.Id)
            {
                request.RevertUIChanges();
                stored.RemoteEventId = trackedId;
                await _calendars.UpdateCalendarItemAsync(stored, await _calendars.GetAttendeesAsync(stored.Id).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }, request, request);

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.UpdateCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Item.IsRecurringChild || request.Attachments?.Count > 0)
            throw new NotSupportedException("EAS occurrence edits and event attachments are not supported yet.");
        var calendar = await _calendars.GetAccountCalendarAsync(request.Item.CalendarId).ConfigureAwait(false);
        var attendees = request.Attendees ?? await _calendars.GetAttendeesAsync(request.Item.Id).ConfigureAwait(false);
        var reminders = request.Reminders ?? await _calendars.GetRemindersAsync(request.Item.Id).ConfigureAwait(false);
        await _engine.MutateAsync(calendar.RemoteCalendarId, "Change", request.Item.RemoteEventId.GetProviderRemoteEventId(),
            EasDataMapper.WriteEvent(request.Item, attendees, reminders), string.Empty, token, async _ =>
            {
                await _calendars.UpdateCalendarItemAsync(request.Item, attendees).ConfigureAwait(false);
                await _calendars.SaveRemindersAsync(request.Item.Id, reminders).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }, request, request);

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request) => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.DeleteCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Item.IsRecurringChild)
            throw new NotSupportedException("Deleting a single EAS event occurrence is not supported yet.");
        var calendar = await _calendars.GetAccountCalendarAsync(request.Item.CalendarId).ConfigureAwait(false);
        await _engine.MutateAsync(calendar.RemoteCalendarId, "Delete", request.Item.RemoteEventId.GetProviderRemoteEventId(), null, string.Empty, token,
            _ => _calendars.DeleteCalendarItemAsync(request.Item.Id)).ConfigureAwait(false);
    }, request, request);

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.AcceptEventAsync(request), request, request) : MeetingResponse(request, request.Item, 1);
    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.TentativeEventAsync(request), request, request) : MeetingResponse(request, request.Item, 2);
    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.DeclineEventAsync(request), request, request) : MeetingResponse(request, request.Item, 3);

    private List<IRequestBundle<EwsRequest>> MeetingResponse(IRequestBase request, CalendarItem item, int response) => Bundle(async token =>
    {
        var calendar = await _calendars.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);
        await _engine.RespondToMeetingAsync(calendar.RemoteCalendarId, item.RemoteEventId.GetProviderRemoteEventId(), response, token).ConfigureAwait(false);
    }, request, (IUIChangeRequest)request);

    private async Task<(MailItemFolder Folder, string RemoteId)> RemoteMailAsync(MailCopy mail)
    {
        var folder = await _folders.GetFolderAsync(mail.FolderId).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS mail folder is missing.");
        var prefix = EasDataMapper.Key(Account.Id, folder.RemoteFolderId, string.Empty);
        if (!mail.Id.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The message does not belong to this EAS folder.");
        return (folder, Uri.UnescapeDataString(mail.Id[prefix.Length..]));
    }

    private static List<IRequestBundle<EwsRequest>> Bundle(Func<CancellationToken, Task> action, IRequestBase request, IUIChangeRequest ui)
        => request == null ? [] : [new EwsRequestBundle(new EwsRequest((_, token) => action(token), request), request, ui)];

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync().ConfigureAwait(false);
        _engine.Dispose();
    }
}
