using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;
using Wino.Providers.Exchange.MAPI;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

// The contact folders and the task folders read over MAPI, landing in the same address books and task lists the
// EWS path fills. Rows are keyed "mapi:" plus the message id; the books and the lists are keyed "mapi:" plus the folder id.
public sealed partial class MapiExchangeSynchronizer
{
    private ulong? _contactsFolderId;
    private MapiContactTags? _contactTags;
    private ulong? _tasksFolderId;
    private MapiTaskTags? _taskTags;

    /// <summary>The folders of the IPM subtree whose container class matches, that are not hidden and not under Deleted Items.</summary>
    private async Task<List<MapiFolderInfo>> ReadIpmFoldersAsync(MapiSession session, Func<string?, bool> isFolderClass, CancellationToken cancellationToken)
    {
        var logon = RequireLogon(session);
        var hierarchy = await MapiFolderOperations.ReadHierarchyAsync(session, logon.IpmSubtreeFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);
        var parents = hierarchy
            .GroupBy(folder => folder.FolderId)
            .ToDictionary(group => group.Key, group => group.First().ParentFolderId);

        bool IsDiscarded(ulong folderId)
        {
            for (var depth = 0; depth < 64; depth++)
            {
                if (folderId == logon.DeletedItemsFolderId)
                    return true;
                if (!parents.TryGetValue(folderId, out var parent))
                    return false;
                folderId = parent;
            }

            return false;
        }

        return hierarchy
            .Where(folder => isFolderClass(folder.ContainerClass) && !folder.IsHidden && !IsDiscarded(folder.FolderId))
            .ToList();
    }

    #region Contacts

    private async Task<MapiContactTags> RequireContactTagsAsync(MapiSession session, CancellationToken cancellationToken)
        => _contactTags ??= await MapiContactOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The mailbox's contact folders under the IPM subtree that are not hidden and not under Deleted Items, the default
    /// Contacts folder (PidTagIpmContactEntryId) first.
    /// </summary>
    private async Task<List<ExchangeContactFolder>> ReadContactFoldersAsync(MapiSession session, CancellationToken cancellationToken)
    {
        var logon = RequireLogon(session);
        _contactsFolderId ??= await MapiContactOperations.FindContactsFolderIdAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);

        var folders = await ReadIpmFoldersAsync(session, IsContactFolderClass, cancellationToken).ConfigureAwait(false);
        var result = new List<ExchangeContactFolder>();
        if (_contactsFolderId is { } defaultId)
        {
            var title = folders.FirstOrDefault(folder => folder.FolderId == defaultId)?.DisplayName;
            result.Add(new ExchangeContactFolder(ToMapiId(defaultId), title, true));
        }

        foreach (var folder in folders.Where(folder => folder.FolderId != _contactsFolderId))
            result.Add(new ExchangeContactFolder(ToMapiId(folder.FolderId), folder.DisplayName, false));

        return result;
    }

    private static ulong RequireContactFolderId(ContactAddressBook book)
        => TryParseMapiId(book.RemoteId, out var folderId)
            ? folderId
            : throw new InvalidOperationException($"Address book '{book.DisplayName}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");

    private static ulong RequireContactMessageId(AccountContact contact)
        => TryParseMapiId(contact.RemoteId, out var messageId)
            ? messageId
            : throw new InvalidOperationException($"Contact '{contact.DisplayName}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");

    protected override async Task<ContactSynchronizationResult> SynchronizeProviderContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var folders = await ReadContactFoldersAsync(session, cancellationToken).ConfigureAwait(false);
        if (folders.Count == 0)
        {
            Logger.Information("MAPI contacts {Account}: the mailbox has no contact folders; nothing to sync.", Account.Address);
            return ContactSynchronizationResult.Empty;
        }

        var books = await EnsureContactBooksAsync(folders, cancellationToken).ConfigureAwait(false);
        var tags = await RequireContactTagsAsync(session, cancellationToken).ConfigureAwait(false);

        var total = 0;
        var rowCount = 0;
        foreach (var book in SelectContactBooksToSynchronize(books, options))
        {
            if (!TryParseMapiId(book.RemoteId, out var folderId))
                continue;

            var rows = await MapiContactOperations.ReadContactsAsync(session, folderId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
            rowCount += rows.Count;

            var upserts = new List<AccountContact>();
            var photos = new List<(AccountContact, Func<Task<byte[]?>>)>();
            foreach (var row in rows.Where(r => r.IsContact))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var contact = MapiContactMapper.ToAccountContact(row, ToMapiId(row.MessageId), Account.Id, book.Id);
                upserts.Add(contact);

                if (row.HasPicture)
                {
                    var messageId = row.MessageId;
                    photos.Add((contact, () => MapiContactOperations.ReadContactPhotoAsync(session, folderId, messageId, cancellationToken)));
                }
            }

            // Photos are read on the session this pass holds, so they are fetched before the lease ends.
            await RekeyContactsFromOtherTransportAsync(book, upserts).ConfigureAwait(false);
            await DownloadContactPhotosAsync(photos, book, cancellationToken).ConfigureAwait(false);
            await RequireContactService().ReplaceAddressBookAsync(book.Id, upserts, null).ConfigureAwait(false);
            total += upserts.Count;
        }

        Logger.Information("MAPI contacts {Account}: {Books} address books, {Rows} rows, {Upserted} contacts.", Account.Address, books.Count, rowCount, total);
        return ContactSynchronizationResult.Completed(total, total, 0);
    }

    protected override async Task ExecuteProviderContactRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var tags = await RequireContactTagsAsync(session, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is ContactCategoryRequest categoryRequest)
            {
                var stored = await RequireContactService().GetContactAsync(categoryRequest.LocalContactId).ConfigureAwait(false) ?? categoryRequest.Contact;
                var categoryBook = await ResolveRequestedContactBookAsync(request, stored).ConfigureAwait(false);
                await MapiContactOperations.SetCategoriesAsync(session, RequireContactFolderId(categoryBook), RequireContactMessageId(stored), tags, categoryRequest.CategoryNames, cancellationToken).ConfigureAwait(false);
                await RequireContactService().SetContactCategoriesAsync(categoryRequest.LocalContactId, categoryRequest.CategoryNames).ConfigureAwait(false);
                continue;
            }

            var local = await ResolveRequestedContactAsync(request).ConfigureAwait(false);

            switch (request.Operation)
            {
                case ContactSynchronizerOperation.Create when local is not null:
                {
                    var book = await ResolveRequestedContactBookAsync(request, local).ConfigureAwait(false);
                    var id = await MapiContactOperations.CreateContactAsync(session, RequireContactFolderId(book), tags, MapiContactMapper.ToWrite(local, null), cancellationToken).ConfigureAwait(false);
                    Logger.Information("MAPI contacts {Account}: created 0x{Id:X16}.", Account.Address, id);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteId = ToMapiId(id);
                    mapped.SourceKind = ContactSourceKind.Exchange;
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Update when local is not null:
                {
                    var messageId = RequireContactMessageId(local);
                    var book = await ResolveRequestedContactBookAsync(request, local).ConfigureAwait(false);
                    var original = (request as ContactActionRequest)?.OriginalContact;
                    await MapiContactOperations.UpdateContactAsync(session, RequireContactFolderId(book), messageId, tags, MapiContactMapper.ToWrite(local, original), cancellationToken).ConfigureAwait(false);
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, RequestEntityCloner.Contact(local), false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.SetPhoto or ContactSynchronizerOperation.DeletePhoto when local is not null && request is ContactActionRequest photoRequest:
                {
                    var messageId = RequireContactMessageId(local);
                    var book = await ResolveRequestedContactBookAsync(request, local).ConfigureAwait(false);
                    var photo = request.Operation == ContactSynchronizerOperation.SetPhoto ? photoRequest.Photo : null;
                    if (request.Operation == ContactSynchronizerOperation.SetPhoto && photo is not { Length: > 0 })
                        throw new InvalidOperationException($"Contact {local.Id} has no photo to write.");

                    await MapiContactOperations.SetContactPhotoAsync(session, RequireContactFolderId(book), messageId, tags, photo, cancellationToken).ConfigureAwait(false);
                    await CommitContactPhotoAsync(photoRequest, local, null, MapiContactMapper.PhotoKey).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Delete:
                {
                    if (local is not null && TryParseMapiId(local.RemoteId, out var messageId))
                    {
                        var book = await ResolveRequestedContactBookAsync(request, local).ConfigureAwait(false);
                        try
                        {
                            await MapiMessageOperations.MoveAsync(session, RequireContactFolderId(book), RequireLogon(session).DeletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                        {
                            Logger.Warning("MAPI contact {RemoteId} was already gone when it was deleted.", local.RemoteId);
                        }
                    }

                    if (local is not null)
                        await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, null, true).ConfigureAwait(false);
                    break;
                }
                default:
                    throw UnsupportedContactOperation(request.Operation);
            }
        }
    }

    #endregion

    #region Tasks

    /// <summary>
    /// The mailbox's task folders under the IPM subtree, outside Deleted Items, and the default Tasks folder's id (null
    /// when the mailbox has no Tasks folder pointer).
    /// </summary>
    private async Task<(ulong? DefaultFolderId, List<MapiFolderInfo> Folders)> ReadTaskFoldersAsync(MapiSession session, CancellationToken cancellationToken)
    {
        var logon = RequireLogon(session);
        _tasksFolderId ??= await MapiTaskOperations.FindTasksFolderIdAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);

        var folders = await ReadIpmFoldersAsync(session, IsTaskFolderClass, cancellationToken).ConfigureAwait(false);
        return (_tasksFolderId, folders);
    }

    private static List<ExchangeTaskFolder> ToTaskFolders(ulong? defaultFolderId, List<MapiFolderInfo> folders)
    {
        var result = new List<ExchangeTaskFolder>();
        if (defaultFolderId is { } defaultId)
        {
            var title = folders.FirstOrDefault(folder => folder.FolderId == defaultId)?.DisplayName;
            result.Add(new ExchangeTaskFolder(ToMapiId(defaultId), title, true));
        }

        foreach (var folder in folders.Where(folder => folder.FolderId != defaultFolderId))
            result.Add(new ExchangeTaskFolder(ToMapiId(folder.FolderId), folder.DisplayName, false));

        return result;
    }

    private static ulong RequireTaskFolderId(AccountTaskList list)
        => TryParseMapiId(list.RemoteId, out var folderId)
            ? folderId
            : throw new InvalidOperationException($"Task list '{list.Title}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");

    protected override async Task<TaskSynchronizationResult> SynchronizeProviderTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var (defaultFolderId, folders) = await ReadTaskFoldersAsync(session, cancellationToken).ConfigureAwait(false);
        if (defaultFolderId is null && folders.Count == 0)
        {
            Logger.Information("MAPI tasks {Account}: the mailbox has no task folders; nothing to sync.", Account.Address);
            return TaskSynchronizationResult.Empty;
        }

        var lists = await EnsureTaskListsAsync(ToTaskFolders(defaultFolderId, folders), cancellationToken).ConfigureAwait(false);
        _taskTags ??= await MapiTaskOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);

        var results = new List<TaskSynchronizationResult>();
        var rowCount = 0;
        foreach (var list in SelectTaskListsToSynchronize(lists, options))
        {
            if (!TryParseMapiId(list.RemoteId, out var folderId))
                continue;

            var rows = await MapiTaskOperations.ReadTasksAsync(session, folderId, _taskTags, cancellationToken, Diagnostics).ConfigureAwait(false);
            rowCount += rows.Count;

            var tasks = rows.Where(r => r.IsTask)
                .Select(row => MapiTaskMapper.ToAccountTask(row, ToMapiId(row.MessageId), list))
                .ToList();

            results.Add(await ApplyTaskSnapshotAsync(list, tasks).ConfigureAwait(false));
        }

        var result = CombineTaskResults(results);
        Logger.Information("MAPI tasks {Account}: {Lists} of {AllLists} lists, {Rows} rows, {Tasks} tasks, {Removed} removed.",
            Account.Address, results.Count, lists.Count, rowCount, result.DownloadedCount, result.DeletedCount);
        return result;
    }

    protected override async Task ExecuteProviderTaskRequestsAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryCompleteLocalTaskRequestAsync(request).ConfigureAwait(false))
            {
                MarkTaskRequestProcessed(request);
                continue;
            }

            switch (request.Operation)
            {
                case TaskSynchronizerOperation.CreateList:
                    await CreateMapiTaskListAsync(session, request, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.UpdateList:
                    await RenameMapiTaskListAsync(session, request, cancellationToken).ConfigureAwait(false);
                    break;
                case TaskSynchronizerOperation.DeleteList:
                    await DeleteMapiTaskListAsync(session, request, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await ExecuteMapiTaskMutationAsync(session, request, cancellationToken).ConfigureAwait(false);
                    break;
            }

            MarkTaskRequestProcessed(request);
        }
    }

    /// <summary>New lists become folders under the default Tasks folder, where Outlook creates them.</summary>
    private async Task CreateMapiTaskListAsync(MapiSession session, ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        var (defaultFolderId, _) = await ReadTaskFoldersAsync(session, cancellationToken).ConfigureAwait(false);
        var parentId = defaultFolderId ?? RequireLogon(session).IpmSubtreeFolderId;

        ulong folderId;
        try
        {
            folderId = await MapiFolderOperations.CreateFolderAsync(session, parentId, list.Title.Trim(), cancellationToken, TaskFolderClass).ConfigureAwait(false);
        }
        catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.Collision)
        {
            throw new InvalidOperationException(Translator.Synchronizer_ExchangeTaskListNameExists, ex);
        }

        Logger.Information("MAPI tasks {Account}: created task folder 0x{Id:X16}.", Account.Address, folderId);
        await CommitTaskListAsync(list, ToMapiId(folderId)).ConfigureAwait(false);
    }

    private async Task RenameMapiTaskListAsync(MapiSession session, ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        EnsureTaskListIsNotDefault(list);
        var folderId = RequireTaskFolderId(list);

        try
        {
            await MapiFolderOperations.RenameFolderAsync(session, folderId, list.Title.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.Collision)
        {
            throw new InvalidOperationException(Translator.Synchronizer_ExchangeTaskListNameExists, ex);
        }

        await CommitTaskListAsync(list, list.RemoteId).ConfigureAwait(false);
    }

    /// <summary>The folder goes to Deleted Items with its tasks, as Outlook deletes a task folder.</summary>
    private async Task DeleteMapiTaskListAsync(MapiSession session, ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var list = await ResolveRequestedTaskListAsync(request).ConfigureAwait(false);
        EnsureTaskListIsNotDefault(list);

        if (!string.IsNullOrWhiteSpace(list.RemoteId))
        {
            var folderId = RequireTaskFolderId(list);
            var (_, folders) = await ReadTaskFoldersAsync(session, cancellationToken).ConfigureAwait(false);

            if (folders.FirstOrDefault(folder => folder.FolderId == folderId) is { } folder)
            {
                var deletedItemsId = RequireLogon(session).DeletedItemsFolderId;
                try
                {
                    await MapiFolderOperations.MoveFolderAsync(session, folderId, folder.ParentFolderId, deletedItemsId, folder.DisplayName, cancellationToken).ConfigureAwait(false);
                }
                catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.Collision)
                {
                    // Deleted Items already holds a folder of this name; a dated name lets this one join it.
                    var name = $"{folder.DisplayName} ({DateTime.Now:yyyy-MM-dd HH.mm.ss})";
                    await MapiFolderOperations.MoveFolderAsync(session, folderId, folder.ParentFolderId, deletedItemsId, name, cancellationToken).ConfigureAwait(false);
                }
                catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                {
                    Logger.Warning("MAPI task folder {RemoteId} was already gone when it was deleted.", list.RemoteId);
                }
            }
            else
            {
                Logger.Warning("MAPI task folder {RemoteId} is no longer a task folder of the mailbox; removing the list locally.", list.RemoteId);
            }
        }

        await ExchangeChangeProcessor.CommitTaskListMutationAsync(list.Id, null, true).ConfigureAwait(false);
    }

    private async Task ExecuteMapiTaskMutationAsync(MapiSession session, ITaskActionRequest request, CancellationToken cancellationToken)
    {
        var (localTask, list) = await ResolveRequestedTaskAsync(request).ConfigureAwait(false);
        var folderId = RequireTaskFolderId(list);
        var tags = _taskTags ??= await MapiTaskOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);

        if (request.Operation == TaskSynchronizerOperation.DeleteTask)
        {
            if (TryParseMapiId(localTask.RemoteId, out var deletedId))
            {
                try
                {
                    await MapiMessageOperations.MoveAsync(session, folderId, RequireLogon(session).DeletedItemsFolderId, [deletedId], cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                {
                    Logger.Warning("MAPI task {RemoteId} was already gone when it was deleted.", localTask.RemoteId);
                }
            }

            await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
            return;
        }

        var source = await ResolveMoveSourceListAsync(request, localTask).ConfigureAwait(false);

        if (TryParseMapiId(localTask.RemoteId, out var messageId))
        {
            var currentFolderId = source is null ? folderId : RequireTaskFolderId(source);
            await MapiTaskOperations.UpdateTaskAsync(session, currentFolderId, messageId, tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);

            if (currentFolderId != folderId)
            {
                if (await MapiTaskOperations.MoveTaskAsync(session, currentFolderId, messageId, folderId, cancellationToken).ConfigureAwait(false) is not { } movedId)
                {
                    // The task moved but its new id could not be told apart. The local row goes, and the destination's
                    // next pull imports the moved task.
                    Logger.Warning("MAPI tasks {Account}: moved 0x{Id:X16} but could not find it in its new folder.", Account.Address, messageId);
                    await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
                    return;
                }

                messageId = movedId;
            }
        }
        else if (!string.IsNullOrWhiteSpace(localTask.RemoteId))
        {
            throw new InvalidOperationException($"Task '{localTask.Title}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");
        }
        else
        {
            messageId = await MapiTaskOperations.CreateTaskAsync(session, folderId, tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);
            Logger.Information("MAPI tasks {Account}: created 0x{Id:X16}.", Account.Address, messageId);
        }

        await CommitTaskAsync(request, localTask, ToMapiId(messageId), null).ConfigureAwait(false);
    }

    #endregion
}
