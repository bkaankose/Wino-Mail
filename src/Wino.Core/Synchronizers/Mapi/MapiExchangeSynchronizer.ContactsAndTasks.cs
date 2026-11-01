using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Rops;
using Wino.Core.Requests;

namespace Wino.Core.Synchronizers.Mapi;

// The default Contacts and Tasks folders read over MAPI, landing in the same address book and task list the EWS path
// fills. Rows are keyed "mapi:" plus the message id; the book and the list are keyed "mapi:" plus the folder id.
public sealed partial class MapiExchangeSynchronizer
{
    private ulong? _contactsFolderId;
    private MapiContactTags? _contactTags;
    private ulong? _tasksFolderId;
    private MapiTaskTags? _taskTags;

    #region Contacts

    private async Task<(ulong FolderId, MapiContactTags Tags)?> ResolveContactsAsync(MapiSession session, CancellationToken cancellationToken)
    {
        _contactsFolderId ??= await MapiContactOperations.FindContactsFolderIdAsync(session, RequireLogon(session).InboxFolderId, cancellationToken).ConfigureAwait(false);
        if (_contactsFolderId is not { } folderId)
        {
            Logger.Information("MAPI contacts {Account}: the mailbox has no Contacts folder pointer; nothing to sync.", Account.Address);
            return null;
        }

        _contactTags ??= await MapiContactOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);
        return (folderId, _contactTags);
    }

    private async Task<(ulong FolderId, MapiContactTags Tags)> RequireContactsAsync(MapiSession session, CancellationToken cancellationToken)
        => await ResolveContactsAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mailbox has no Contacts folder.");

    protected override async Task<ContactSynchronizationResult> SynchronizeProviderContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        if (await ResolveContactsAsync(session, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return ContactSynchronizationResult.Empty;

        var book = await GetOrCreateContactsBookAsync(ToMapiId(resolved.FolderId)).ConfigureAwait(false);
        var rows = await MapiContactOperations.ReadContactsAsync(session, resolved.FolderId, resolved.Tags, cancellationToken, Diagnostics).ConfigureAwait(false);

        var upserts = new List<AccountContact>();
        var photos = new List<(AccountContact, Func<Task<byte[]?>>)>();
        foreach (var row in rows.Where(r => r.IsContact))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contact = MapiContactMapper.ToAccountContact(row, ToMapiId(row.MessageId), Account.Id, book.Id);
            upserts.Add(contact);

            if (row.HasAttachments)
            {
                var folderId = resolved.FolderId;
                var messageId = row.MessageId;
                photos.Add((contact, () => MapiContactOperations.ReadContactPhotoAsync(session, folderId, messageId, cancellationToken)));
            }
        }

        // Photos are read on the session this pass holds, so they are fetched before the lease ends.
        await RekeyContactsFromOtherTransportAsync(book, upserts).ConfigureAwait(false);
        await DownloadContactPhotosAsync(photos, book, cancellationToken).ConfigureAwait(false);
        await RequireContactService().ReplaceAddressBookAsync(book.Id, upserts, null).ConfigureAwait(false);

        Logger.Information("MAPI contacts {Account}: {Rows} rows, {Upserted} contacts.", Account.Address, rows.Count, upserts.Count);
        return ContactSynchronizationResult.Completed(upserts.Count, upserts.Count, 0);
    }

    protected override async Task ExecuteProviderContactRequestsAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = await ResolveRequestedContactAsync(request).ConfigureAwait(false);

            switch (request.Operation)
            {
                case ContactSynchronizerOperation.Create when local is not null:
                {
                    var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                    var id = await MapiContactOperations.CreateContactAsync(session, resolved.FolderId, resolved.Tags, MapiContactMapper.ToWrite(local), cancellationToken).ConfigureAwait(false);
                    Logger.Information("MAPI contacts {Account}: created 0x{Id:X16}.", Account.Address, id);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteId = ToMapiId(id);
                    mapped.SourceKind = ContactSourceKind.Exchange;
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Update when local is not null:
                {
                    if (!TryParseMapiId(local.RemoteId, out var messageId))
                        throw new InvalidOperationException($"Contact '{local.DisplayName}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");

                    var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                    await MapiContactOperations.UpdateContactAsync(session, resolved.FolderId, messageId, resolved.Tags, MapiContactMapper.ToWrite(local), cancellationToken).ConfigureAwait(false);
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, RequestEntityCloner.Contact(local), false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Delete:
                {
                    if (local is not null && TryParseMapiId(local.RemoteId, out var messageId))
                    {
                        var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await MapiMessageOperations.MoveAsync(session, resolved.FolderId, RequireLogon(session).DeletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task<(ulong FolderId, MapiTaskTags Tags)?> ResolveTasksAsync(MapiSession session, CancellationToken cancellationToken)
    {
        _tasksFolderId ??= await MapiTaskOperations.FindTasksFolderIdAsync(session, RequireLogon(session).InboxFolderId, cancellationToken).ConfigureAwait(false);
        if (_tasksFolderId is not { } folderId)
        {
            Logger.Information("MAPI tasks {Account}: the mailbox has no Tasks folder pointer; nothing to sync.", Account.Address);
            return null;
        }

        _taskTags ??= await MapiTaskOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);
        return (folderId, _taskTags);
    }

    private async Task<(ulong FolderId, MapiTaskTags Tags)> RequireTasksAsync(MapiSession session, CancellationToken cancellationToken)
        => await ResolveTasksAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mailbox has no Tasks folder.");

    protected override async Task<TaskSynchronizationResult> SynchronizeProviderTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        if (await ResolveTasksAsync(session, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return TaskSynchronizationResult.Empty;

        var list = await EnsureTaskListAsync(ToMapiId(resolved.FolderId), null).ConfigureAwait(false);
        var rows = await MapiTaskOperations.ReadTasksAsync(session, resolved.FolderId, resolved.Tags, cancellationToken, Diagnostics).ConfigureAwait(false);

        var tasks = rows.Where(r => r.IsTask)
            .Select(row => MapiTaskMapper.ToAccountTask(row, ToMapiId(row.MessageId), list))
            .ToList();

        var result = await ApplyTaskSnapshotAsync(list, tasks).ConfigureAwait(false);

        Logger.Information("MAPI tasks {Account}: {Rows} rows, {Tasks} tasks, {Removed} removed.", Account.Address, rows.Count, tasks.Count, result.DeletedCount);
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

            var (localTask, _) = await ResolveRequestedTaskAsync(request).ConfigureAwait(false);
            var resolved = await RequireTasksAsync(session, cancellationToken).ConfigureAwait(false);

            if (request.Operation == TaskSynchronizerOperation.DeleteTask)
            {
                if (TryParseMapiId(localTask.RemoteId, out var deletedId))
                {
                    try
                    {
                        await MapiMessageOperations.MoveAsync(session, resolved.FolderId, RequireLogon(session).DeletedItemsFolderId, [deletedId], cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                    {
                        Logger.Warning("MAPI task {RemoteId} was already gone when it was deleted.", localTask.RemoteId);
                    }
                }

                await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }

            if (TryParseMapiId(localTask.RemoteId, out var messageId))
            {
                await MapiTaskOperations.UpdateTaskAsync(session, resolved.FolderId, messageId, resolved.Tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(localTask.RemoteId))
            {
                throw new InvalidOperationException($"Task '{localTask.Title}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");
            }
            else
            {
                messageId = await MapiTaskOperations.CreateTaskAsync(session, resolved.FolderId, resolved.Tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);
                Logger.Information("MAPI tasks {Account}: created 0x{Id:X16}.", Account.Address, messageId);
            }

            await CommitTaskAsync(request, localTask, ToMapiId(messageId), null).ConfigureAwait(false);
            MarkTaskRequestProcessed(request);
        }
    }

    #endregion
}
