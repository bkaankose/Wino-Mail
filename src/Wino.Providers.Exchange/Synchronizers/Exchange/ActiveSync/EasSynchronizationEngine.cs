using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Tasks;
using Wino.Services;
using Wino.Services.Interfaces;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed partial class EasSynchronizationEngine : IDisposable
{
    private static readonly XNamespace FolderHierarchy = "FolderHierarchy:";

    private readonly MailAccount _account;
    private readonly IDatabaseService _database;
    private readonly IAccountService _accounts;
    private readonly IFolderService _folders;
    private readonly IMailService _mail;
    private readonly ICalendarService _calendars;
    private readonly IContactService _contacts;
    private readonly ITaskService _tasks;
    private readonly IPictureStorageService _pictures;
    private readonly EasClient _client;
    private readonly Action _hierarchyChanged;
    private readonly Action<EasCollection, Exception> _collectionFailed;
    private readonly Action<EasCollection, string, Exception> _itemFailed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _collectionPassesUtc = new(StringComparer.Ordinal);
    private long _hierarchyPassTicks;

    internal EasSynchronizationEngine(MailAccount account, IDatabaseService database, IAccountService accounts,
        IFolderService folders, IMailService mail, ICalendarService calendars, IContactService contacts, ITaskService tasks, IPictureStorageService pictures,
        Action hierarchyChanged, Action<EasCollection, Exception> collectionFailed, Action<EasCollection, string, Exception> itemFailed,
        EasClient? client = null)
    {
        _account = account; _database = database; _accounts = accounts; _folders = folders;
        _mail = mail; _calendars = calendars; _contacts = contacts; _tasks = tasks;
        _pictures = pictures;
        _hierarchyChanged = hierarchyChanged;
        _collectionFailed = collectionFailed;
        _itemFailed = itemFailed;
        var server = account.ServerInformation ?? throw new InvalidOperationException("EAS server settings are missing.");
        if (server.UseOAuthAuthentication)
            throw new NotSupportedException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncPasswordRequired);
        // Automatic detection keeps the EWS URL it started from; Normalize maps it to the ActiveSync path on the same host.
        var endpoint = ExchangeServerEndpoint.Normalize(server.IncomingServer, ExchangeTransport.ActiveSync)
            ?? throw new InvalidOperationException("The EAS server address is invalid.");
        _client = client ?? new EasClient(account, endpoint.AbsoluteUri, server.IncomingServerUsername,
            server.IncomingServerPassword ?? throw new InvalidOperationException("The EAS password is missing."), new EasAccountStateStore(database),
            new EasAccountData(accounts, account));
    }

    internal async Task<bool> IsProtocol16Async(CancellationToken cancellationToken)
        => Version.Parse(await GetProtocolVersionAsync(cancellationToken).ConfigureAwait(false)) >= new Version(16, 0);

    /// <summary>
    /// Whether the server keeps drafts, which needs 16.0. The version stored by an earlier session answers it, so a
    /// draft save does not wait for a connection.
    /// </summary>
    internal async Task<bool> SupportsServerDraftsAsync(CancellationToken cancellationToken)
    {
        var version = await _client.GetKnownVersionAsync().ConfigureAwait(false) ?? await GetProtocolVersionAsync(cancellationToken).ConfigureAwait(false);
        return Version.TryParse(version, out var parsed) && parsed >= new Version(16, 0);
    }

    /// <summary>
    /// Stores the threading headers of a message whose MIME was just fetched: synchronization reads no MIME, and
    /// ActiveSync carries no Message-ID field, so replies and threading learn them here.
    /// </summary>
    internal Task SaveMessageHeadersAsync(Guid uniqueId, string messageId, string inReplyTo, string references)
        => _database.Connection.ExecuteAsync("UPDATE MailCopy SET MessageId = ?, InReplyTo = ?, \"References\" = ? WHERE UniqueId = ?",
            messageId, inReplyTo, references, uniqueId);

    /// <summary>The negotiated MS-ASProtocolVersion; connects first when no request has been made yet.</summary>
    internal async Task<string> GetProtocolVersionAsync(CancellationToken cancellationToken)
    {
        if (_client.ProtocolVersion == null)
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _client.ProtocolVersion ?? throw new InvalidDataException("The EAS protocol version was not negotiated.");
    }

    private const int DraftsFolderType = 3;

    /// <summary>
    /// Synchronizes the mail folders the options select, the way the other synchronizers pick them: every enabled
    /// folder on a full pass, the Inbox set or the named folders otherwise. The hierarchy is read on a full pass and
    /// whenever no mail folder is known yet. Below 16.0 the Drafts folder is not synchronized: drafts stay local there.
    /// </summary>
    /// <param name="downloaded">Receives each new message as it is stored, so a pass that fails later still reports them.</param>
    internal async Task SynchronizeMailAsync(MailSynchronizationOptions options, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = await CountMailCollectionsAsync().ConfigureAwait(false);
            if (options.Type == MailSynchronizationType.FullFolders || known == 0)
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);

            var syncsDrafts = await IsProtocol16Async(cancellationToken).ConfigureAwait(false);
            var folders = (await _folders.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
                .Where(folder => folder.EasFolderType > 0 && !string.IsNullOrEmpty(folder.RemoteFolderId) && (syncsDrafts || folder.EasFolderType != DraftsFolderType))
                .GroupBy(folder => folder.Id)
                .Select(group => group.First())
                .ToList();
            foreach (var folder in folders)
                await SynchronizeIsolatedAsync(EasCollection.From(folder), downloaded, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task SynchronizeAsync(string itemClass, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            var states = await LoadStatesAsync().ConfigureAwait(false);
            if (!states.Any(state => ItemClass(state.Type) == itemClass))
                throw new NotSupportedException("The EAS server did not expose a " + itemClass + " collection for this account. Check synchronization availability with your mail provider.");
            foreach (var state in states.Where(state => ItemClass(state.Type) == itemClass))
                await SynchronizeIsolatedAsync(EasCollection.From(state), new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            if (itemClass == "Calendar" && _account.CalendarIntegrationSource == AccountIntegrationSource.Provider)
            {
                var activeCalendars = states.Where(state => ItemClass(state.Type) == "Calendar").Select(state => state.Id).ToHashSet();
                foreach (var previous in await _calendars.GetAccountCalendarsAsync(_account.Id).ConfigureAwait(false))
                {
                    if (!activeCalendars.Contains(previous.Id) && !string.IsNullOrWhiteSpace(previous.RemoteCalendarId) &&
                        !previous.RemoteCalendarId.StartsWith("local", StringComparison.Ordinal) &&
                        !previous.RemoteCalendarId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        previous.MailAccount = _account;
                        await _calendars.DeleteAccountCalendarAsync(previous).ConfigureAwait(false);
                    }
                }
                await ExtendSeriesWindowAsync(states.Where(state => ItemClass(state.Type) == "Calendar")).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task SynchronizeFolderMetadataAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task SynchronizeIsolatedAsync(EasCollection collection, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizeCollectionAsync(collection, downloaded, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCollectionFailure(exception))
        {
            _collectionFailed(collection, exception);
        }
    }

    /// <summary>
    /// Whether a failure belongs to one collection, so the pass continues with the others. Global ActiveSync statuses
    /// (100 and above), HTTP, authentication, policy and cancellation failures stop the pass.
    /// </summary>
    private static bool IsCollectionFailure(Exception exception) => exception switch
    {
        EasCommandException command => !int.TryParse(command.Status, out var status) || status < 100,
        InvalidDataException or XmlException or FormatException or EasResponseTooLargeException => true,
        _ => false
    };

    internal Task<string> MutateAsync(string folderId, string operation, string remoteId, XElement? data, string clientId,
        CancellationToken cancellationToken, Func<string, Task>? persistMutation = null)
        => MutateCoreAsync(folderId, operation, remoteId, data, clientId, persistMutation, cancellationToken);

    private async Task<string> MutateCoreAsync(string folderId, string operation, string remoteId, XElement? data, string clientId,
        Func<string, Task>? persistMutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetCollectionAsync(folderId).ConfigureAwait(false);
            await SynchronizeCollectionAsync(state, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            var sync = EasDataMapper.Sync;
            var command = new XElement(sync + operation,
                new XElement(sync + (operation == "Add" ? "ClientId" : "ServerId"), operation == "Add" ? clientId : remoteId));
            if (data != null)
            {
                var shadow = operation == "Add" ? null : await _database.Connection.FindAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, folderId, remoteId)).ConfigureAwait(false);
                var itemClass = ItemClass(state.Type);
                command.Add(itemClass is "Email" or "Contacts" ? data : itemClass == "Tasks" ? EasDataMapper.ForTaskChange(Merge(shadow?.ApplicationData, data)) : Merge(shadow?.ApplicationData, data));
            }
            var result = await _client.SyncAsync(folderId, state.SyncKey, ItemClass(state.Type), new XElement(sync + "Commands", command), cancellationToken).ConfigureAwait(false);
            // The server has moved to the new key even when the item itself failed.
            await SaveKeyAsync(state, result).ConfigureAwait(false);
            var response = result.Element(sync + "Responses")?.Element(sync + operation);
            if (response != null)
                RequireItemSuccess(response);
            else if (operation == "Add")
                throw new InvalidDataException("The EAS addition response is missing.");
            var resultId = response?.Element(sync + "ServerId")?.Value ?? remoteId;
            if (string.IsNullOrEmpty(resultId))
                throw new InvalidDataException("The EAS mutation response has no item identifier.");

            // Persist acknowledged mutations before projecting server echoes, while the gate is held.
            if (persistMutation != null)
                await persistMutation(resultId).ConfigureAwait(false);
            await ApplyPageAsync(state, result, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            if (operation == "Delete")
                await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, folderId, resultId)).ConfigureAwait(false);
            else if (data != null)
            {
                var key = EasDataMapper.Key(_account.Id, folderId, resultId);
                var shadow = await _database.Connection.FindAsync<EasItemShadow>(key).ConfigureAwait(false);
                await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                    CollectionId = state.Id, RemoteId = resultId, ApplicationData = Merge(shadow?.ApplicationData, data).ToString(SaveOptions.DisableFormatting),
                    Generation = state.Generation }, typeof(EasItemShadow)).ConfigureAwait(false);
            }
            return resultId;
        }
        finally { _gate.Release(); }
    }

    private const int MutationBatchSize = 100;

    /// <summary>
    /// Sends many item changes to one collection, up to <see cref="MutationBatchSize"/> commands in each Sync
    /// request, without reading the collection's pending changes first. The new sync key is saved after every request,
    /// also when single items fail. Results come back in the order of <paramref name="mutations"/>; a Change or
    /// Delete the server does not answer for succeeded, and an Add result carries the server id it was given.
    /// <paramref name="deletesAsMoves"/> false deletes permanently instead of moving to Deleted Items.
    /// </summary>
    internal async Task<IReadOnlyList<EasMutationResult>> MutateItemsAsync(string folderId, IReadOnlyList<EasMutation> mutations, bool deletesAsMoves,
        CancellationToken cancellationToken)
    {
        var results = new List<EasMutationResult>(mutations.Count);
        if (mutations.Count == 0)
            return results;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetCollectionAsync(folderId).ConfigureAwait(false);
            if (state.SyncKey == "0")
                await SynchronizeCollectionAsync(state, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);

            var sync = EasDataMapper.Sync;
            foreach (var chunk in mutations.Chunk(MutationBatchSize))
            {
                var commands = new XElement(sync + "Commands", chunk.Select(mutation => mutation.ToCommand()));
                XElement? response;
                try
                {
                    response = await SyncWithRecoveryAsync(state, state.SyncKey, commands, cancellationToken, getChanges: false, deletesAsMoves: deletesAsMoves).ConfigureAwait(false);
                }
                catch (EasCommandException exception) when (exception.Status == "3")
                {
                    await SynchronizeCollectionAsync(state, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
                    response = await SyncWithRecoveryAsync(state, state.SyncKey, commands, cancellationToken, getChanges: false, deletesAsMoves: deletesAsMoves).ConfigureAwait(false);
                }

                if (response == null)
                {
                    results.AddRange(chunk.Select(mutation => new EasMutationResult(mutation, "8", null)));
                    continue;
                }

                await SaveKeyAsync(state, response).ConfigureAwait(false);
                var answers = response.Element(sync + "Responses")?.Elements().ToList() ?? new List<XElement>();
                foreach (var mutation in chunk)
                {
                    var answer = answers.FirstOrDefault(element => element.Name.LocalName == mutation.Operation.ToString() &&
                        element.Element(sync + (mutation.Operation == EasItemOperation.Add ? "ClientId" : "ServerId"))?.Value == mutation.Id);
                    var status = answer?.Element(sync + "Status")?.Value ?? (mutation.Operation == EasItemOperation.Add ? "missing" : "1");
                    var serverId = mutation.Operation == EasItemOperation.Add ? answer?.Element(sync + "ServerId")?.Value : mutation.Id;
                    var result = new EasMutationResult(mutation, status, serverId, answer?.Element(sync + "ApplicationData"));
                    results.Add(result);

                    if (mutation.Operation == EasItemOperation.Delete && (result.Succeeded || result.IsNotFound) ||
                        mutation.Operation == EasItemOperation.Change && result.IsNotFound)
                        await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, state.ServerId, mutation.Id)).ConfigureAwait(false);
                    else if (mutation.Operation == EasItemOperation.Add && result.Succeeded && !string.IsNullOrEmpty(serverId) && mutation.Data != null)
                        await WriteShadowAsync(state, serverId, mutation.Data).ConfigureAwait(false);
                }
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Moves messages with MoveItems, up to <see cref="MutationBatchSize"/> in each request. Exchange answers out of
    /// order, so answers are matched by source id. A moved message is stored under the id it has in the destination
    /// (DstMsgId) right away; the destination's next Sync reports it again and updates that row. Status 1 means the
    /// source item is gone, and it is removed locally.
    /// </summary>
    internal async Task<IReadOnlyList<EasMoveResult>> MoveItemsAsync(IReadOnlyList<EasMove> moves, CancellationToken cancellationToken)
    {
        var results = new List<EasMoveResult>(moves.Count);
        if (moves.Count == 0)
            return results;

        XNamespace move = "Move:";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var chunk in moves.Chunk(MutationBatchSize))
            {
                var request = new XElement(move + "MoveItems", chunk.Select(item => new XElement(move + "Move", new XElement(move + "SrcMsgId", item.SourceId),
                    new XElement(move + "SrcFldId", item.SourceFolderId), new XElement(move + "DstFldId", item.DestinationFolderId))));
                var response = await _client.CommandAsync("MoveItems", request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The EAS move response is missing.");
                var answers = response.Elements(move + "Response").ToList();
                foreach (var item in chunk)
                {
                    var answer = answers.FirstOrDefault(element => element.Element(move + "SrcMsgId")?.Value == item.SourceId);
                    var result = new EasMoveResult(item, answer?.Element(move + "Status")?.Value ?? "missing", answer?.Element(move + "DstMsgId")?.Value);
                    results.Add(result);
                    if (result.Succeeded || result.IsNotFound)
                        await ProjectMoveAsync(item, result.Succeeded ? result.DestinationId : null).ConfigureAwait(false);
                }
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task ProjectMoveAsync(EasMove move, string? destinationId)
    {
        var source = await GetCollectionAsync(move.SourceFolderId).ConfigureAwait(false);
        var existing = await _mail.GetSingleMailItemAsync(EasDataMapper.Key(_account.Id, source.ServerId, move.SourceId), source.ServerId).ConfigureAwait(false);
        await DeleteProjectionAsync(source, move.SourceId).ConfigureAwait(false);
        if (existing == null || string.IsNullOrEmpty(destinationId))
            return;

        var folder = await _database.Connection.Table<MailItemFolder>()
            .FirstOrDefaultAsync(row => row.MailAccountId == _account.Id && row.RemoteFolderId == move.DestinationFolderId && row.EasFolderType > 0).ConfigureAwait(false);
        if (folder == null)
            return;

        var destination = EasCollection.From(folder);
        var package = EasDataMapper.Mail(folder, destinationId, new XElement(EasDataMapper.Sync + "ApplicationData"));
        var copy = package.Copy;
        copy.Subject = existing.Subject; copy.FromName = existing.FromName; copy.FromAddress = existing.FromAddress;
        copy.CreationDate = existing.CreationDate; copy.PreviewText = existing.PreviewText; copy.IsRead = existing.IsRead;
        copy.IsFlagged = existing.IsFlagged; copy.IsPinned = existing.IsPinned; copy.Importance = existing.Importance;
        copy.HasAttachments = existing.HasAttachments; copy.ItemType = existing.ItemType; copy.ThreadId = existing.ThreadId;
        copy.MessageId = existing.MessageId; copy.References = existing.References; copy.InReplyTo = existing.InReplyTo;
        await _mail.CreateMailAsync(_account.Id, package).ConfigureAwait(false);
        await WriteShadowAsync(destination, destinationId, new XElement(EasDataMapper.Sync + "ApplicationData")).ConfigureAwait(false);
    }

    /// <summary>
    /// FolderCreate, FolderUpdate or FolderDelete. The response carries the next hierarchy key, so the change is applied
    /// locally here: the following FolderSync does not report it again. Status 9 means the hierarchy key is stale; the
    /// hierarchy is read again and the command retried once.
    /// </summary>
    internal async Task<string> ChangeFolderAsync(string command, string remoteId, string parentId, string displayName, int folderType, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            XElement response;
            for (var attempt = 0; ; attempt++)
            {
                var request = new XElement(FolderHierarchy + command, new XElement(FolderHierarchy + "SyncKey", HierarchyKey()));
                if (command != "FolderCreate")
                    request.Add(new XElement(FolderHierarchy + "ServerId", remoteId));
                if (command != "FolderDelete")
                    request.Add(new XElement(FolderHierarchy + "ParentId", string.IsNullOrEmpty(parentId) ? "0" : parentId), new XElement(FolderHierarchy + "DisplayName", displayName));
                if (command == "FolderCreate")
                    request.Add(new XElement(FolderHierarchy + "Type", folderType));
                response = await _client.CommandAsync(command, request, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The EAS folder operation response is empty.");
                if (attempt == 0 && StatusOf(response) == "9")
                {
                    Serilog.Log.Information("EAS {Command} for {Account}: the folder hierarchy key is stale; reading the hierarchy again.", command, _account.Address);
                    await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                EasClient.RequireSuccess(response);
                break;
            }

            var resultId = command == "FolderCreate" ? Required(response, FolderHierarchy + "ServerId") : remoteId;
            var mailRows = await LoadMailRowsAsync().ConfigureAwait(false);
            var states = await LoadStatesAsync().ConfigureAwait(false);
            var mailChanged = false;
            if (command == "FolderDelete")
                mailChanged = await RemoveTreeAsync(resultId, mailRows, states).ConfigureAwait(false);
            else
            {
                var type = mailRows.FirstOrDefault(row => row.EasFolderType > 0 && row.RemoteFolderId == resultId)?.EasFolderType
                    ?? states.FirstOrDefault(state => state.ServerId == resultId)?.Type ?? folderType;
                var parent = string.IsNullOrEmpty(parentId) ? "0" : parentId;
                if (IsMailType(type))
                {
                    if (_account.IsMailAccessGranted)
                        mailChanged = await UpsertMailFolderAsync(resultId, parent, displayName, type, mailRows).ConfigureAwait(false);
                }
                else
                    await UpsertCollectionStateAsync(resultId, parent, displayName, type, states).ConfigureAwait(false);
            }
            await SaveHierarchyKeyAsync(Required(response, FolderHierarchy + "SyncKey")).ConfigureAwait(false);
            if (mailChanged)
                _hierarchyChanged();
            return resultId;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The ParentId the folder hierarchy reported for a non-mail collection.</summary>
    internal async Task<string> GetCollectionParentAsync(string serverId)
    {
        var state = await _database.Connection.Table<EasCollectionState>()
            .FirstOrDefaultAsync(row => row.AccountId == _account.Id && row.ServerId == serverId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The EAS collection is not synchronized yet.");
        return string.IsNullOrEmpty(state.ParentServerId) ? throw new InvalidOperationException("The EAS collection has no parent in the folder hierarchy.") : state.ParentServerId;
    }

    internal async Task SendAsync(byte[] mime, string clientId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _client.SendMailAsync(mime, clientId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// ItemOperations reads one item and touches no sync key, so opening a message does not wait for a running
    /// synchronization to finish.
    /// </summary>
    internal Task<XElement> FetchAsync(string folderId, string remoteId, CancellationToken cancellationToken)
        => _client.FetchAsync(folderId, remoteId, cancellationToken);

    /// <summary>Removes the attachments the server's device policy does not allow on this device.</summary>
    internal void ApplyAttachmentPolicy(MimeKit.MimeMessage message) => _client.ApplyAttachmentPolicy(message);

    /// <summary>
    /// Reads the folder hierarchy. Mail collections become MailItemFolder rows, the others EasCollectionState rows.
    /// On a full read (key 0, also after a connection method change) rows another transport stored are re-keyed in
    /// place first, so folder ids and their settings survive, and rows the server no longer has are removed.
    /// </summary>
    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizeFoldersCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _hierarchyPassTicks, DateTime.UtcNow.Ticks);
        }
    }

    private async Task SynchronizeFoldersCoreAsync(CancellationToken cancellationToken)
    {
        var mailRows = await LoadMailRowsAsync().ConfigureAwait(false);
        var states = await LoadStatesAsync().ConfigureAwait(false);
        var hasMailCollections = mailRows.Any(row => row.EasFolderType > 0);
        var key = HierarchyKey();
        if ((!hasMailCollections && states.Count == 0) || (_account.IsMailAccessGranted && !hasMailCollections))
            key = "0";

        XElement response;
        try { response = await _client.FolderSyncAsync(key, cancellationToken).ConfigureAwait(false); }
        catch (EasCommandException exception) when (exception.Status == "9" && key != "0")
        {
            key = "0";
            response = await _client.FolderSyncAsync(key, cancellationToken).ConfigureAwait(false);
        }
        var changes = response.Element(FolderHierarchy + "Changes") ?? throw new InvalidDataException("The EAS folder changes are missing.");
        var entries = changes.Elements()
            .Where(element => element.Name == FolderHierarchy + "Add" || element.Name == FolderHierarchy + "Update" || element.Name == FolderHierarchy + "Delete")
            .ToList();
        var fullRead = key == "0";
        var mailChanged = false;

        var staleRows = new List<MailItemFolder>();
        if (fullRead && _account.IsMailAccessGranted)
            mailChanged |= await RematchFoldersAsync(mailRows, staleRows, entries).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in entries)
        {
            var serverId = change.Element(FolderHierarchy + "ServerId")?.Value;
            if (string.IsNullOrEmpty(serverId))
                continue;
            seen.Add(serverId);
            try
            {
                if (change.Name == FolderHierarchy + "Delete")
                {
                    mailChanged |= await RemoveTreeAsync(serverId, mailRows, states).ConfigureAwait(false);
                    continue;
                }

                var name = Required(change, FolderHierarchy + "DisplayName");
                var parentId = Required(change, FolderHierarchy + "ParentId");
                if (!int.TryParse(Required(change, FolderHierarchy + "Type"), out var type))
                    throw new InvalidDataException("The EAS folder type is invalid.");
                if (IsMailType(type))
                {
                    if (_account.IsMailAccessGranted)
                        mailChanged |= await UpsertMailFolderAsync(serverId, parentId, name, type, mailRows).ConfigureAwait(false);
                }
                else
                    await UpsertCollectionStateAsync(serverId, parentId, name, type, states).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCollectionFailure(exception))
            {
                _collectionFailed(EasCollection.From(new EasCollectionState { ServerId = serverId,
                    DisplayName = change.Element(FolderHierarchy + "DisplayName")?.Value ?? serverId }), exception);
            }
        }

        if (fullRead)
        {
            foreach (var stale in staleRows)
            {
                await RemoveCollectionAsync(EasCollection.From(stale)).ConfigureAwait(false);
                mailChanged = true;
            }
            foreach (var stale in states.Where(state => !seen.Contains(state.ServerId)).ToList())
            {
                if (states.Contains(stale))
                    await RemoveTreeAsync(stale.ServerId, mailRows, states).ConfigureAwait(false);
            }
        }

        foreach (var state in states)
        {
            var entityId = state.EntityId;
            await EnsureCollectionAsync(state).ConfigureAwait(false);
            if (state.EntityId != entityId)
                await _database.Connection.ExecuteAsync("UPDATE EasCollectionState SET EntityId = ? WHERE Id = ?", state.EntityId, state.Id).ConfigureAwait(false);
        }

        await SaveHierarchyKeyAsync(Required(response, FolderHierarchy + "SyncKey")).ConfigureAwait(false);
        if (mailChanged)
            _hierarchyChanged();
    }

    /// <summary>
    /// Matches the stored folder rows to the mail collections of a full hierarchy read. An ActiveSync row keeps its
    /// collection when the read still has its ServerId: ServerIds do not change when the hierarchy is read again from
    /// key 0, and they are opaque, so they are never matched by name. Only rows another transport stored (EWS, MAPI)
    /// go through the matcher every Exchange transport shares, by role and path. A re-keyed row starts its collection
    /// over; rows that match nothing are moved to <paramref name="staleRows"/>.
    /// </summary>
    private async Task<bool> RematchFoldersAsync(List<MailItemFolder> mailRows, List<MailItemFolder> staleRows, List<XElement> entries)
    {
        var incoming = new List<ExchangeFolderCandidate>();
        var types = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var add in entries.Where(entry => entry.Name == FolderHierarchy + "Add"))
        {
            var serverId = add.Element(FolderHierarchy + "ServerId")?.Value;
            if (string.IsNullOrEmpty(serverId) || !int.TryParse(add.Element(FolderHierarchy + "Type")?.Value, out var type) || !IsMailType(type))
                continue;
            types[serverId] = type;
            incoming.Add(new ExchangeFolderCandidate(serverId, add.Element(FolderHierarchy + "ParentId")?.Value ?? "0",
                add.Element(FolderHierarchy + "DisplayName")?.Value ?? string.Empty, ServerRole(type)));
        }

        var matches = ExchangeTransportRekey.MatchFolders(mailRows, incoming, ExchangeTransport.ActiveSync);
        foreach (var row in mailRows.Where(row => row.EasFolderType > 0 && types.ContainsKey(row.RemoteFolderId ?? string.Empty)))
            matches[row.Id] = row.RemoteFolderId;

        var changed = false;
        foreach (var row in mailRows.ToList())
        {
            if (!matches.TryGetValue(row.Id, out var serverId))
            {
                mailRows.Remove(row);
                staleRows.Add(row);
                continue;
            }
            if (row.EasFolderType > 0 && row.RemoteFolderId == serverId)
                continue;

            // Mail ids embed the collection id, so the downloaded mail of a re-keyed row is fetched again. Messages
            // whose id is not an ActiveSync key (local drafts) never reached the server and stay.
            await _database.Connection.ExecuteAsync("DELETE FROM EasItemShadow WHERE CollectionId = ?", row.Id).ConfigureAwait(false);
            await _mail.DeleteMailsAsync(_account.Id, await ServerMailIdsAsync(row.Id).ConfigureAwait(false)).ConfigureAwait(false);
            row.RemoteFolderId = serverId;
            row.EasFolderType = types[serverId];
            row.DeltaToken = "0";
            row.EasSyncGeneration = string.Empty;
            await _database.Connection.ExecuteAsync(
                "UPDATE MailItemFolder SET RemoteFolderId = ?, EasFolderType = ?, DeltaToken = '0', EasSyncGeneration = '' WHERE Id = ?",
                row.RemoteFolderId, row.EasFolderType, row.Id).ConfigureAwait(false);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Inserts or updates a mail folder row. Inbox, Drafts, Deleted Items and Sent Items take their role from the
    /// server type with the flags the EWS synchronizer gives the same roles. ActiveSync has no Junk or Archive role, so
    /// a special type the user assigned to any other folder is kept.
    /// </summary>
    private async Task<bool> UpsertMailFolderAsync(string serverId, string parentId, string name, int type, List<MailItemFolder> mailRows)
    {
        var role = ServerRole(type);
        var row = mailRows.FirstOrDefault(row => row.EasFolderType > 0 && row.RemoteFolderId == serverId);
        if (row == null)
        {
            var isSystem = role != SpecialFolderType.Other;
            row = new MailItemFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = _account.Id,
                RemoteFolderId = serverId,
                ParentRemoteFolderId = parentId,
                FolderName = name,
                EasFolderType = type,
                DeltaToken = "0",
                SpecialFolderType = role,
                IsSystemFolder = isSystem,
                IsSticky = isSystem,
                IsSynchronizationEnabled = true,
                ShowUnreadCount = role != SpecialFolderType.Deleted,
                IsCountedInAccountTotal = role != SpecialFolderType.Deleted,
            };
            await _folders.InsertFolderAsync(row).ConfigureAwait(false);
            mailRows.Add(row);
            return true;
        }

        var changed = row.FolderName != name || row.ParentRemoteFolderId != parentId || row.EasFolderType != type;
        if (role != SpecialFolderType.Other && (row.SpecialFolderType != role || !row.IsSystemFolder))
        {
            row.SpecialFolderType = role;
            row.IsSystemFolder = true;
            row.IsSticky = true;
            changed = true;
        }
        else if (role == SpecialFolderType.Other && ServerRoles.Contains(row.SpecialFolderType))
        {
            // Only the server's own folder holds a role ActiveSync reports, so a second Inbox or Drafts cannot appear.
            row.SpecialFolderType = SpecialFolderType.Other;
            row.IsSystemFolder = false;
            row.IsSticky = false;
            changed = true;
        }
        if (row.SpecialFolderType == SpecialFolderType.Inbox && !row.IsSticky)
        {
            row.IsSticky = true;
            changed = true;
        }
        if (!changed)
            return false;

        row.FolderName = name;
        row.ParentRemoteFolderId = parentId;
        row.EasFolderType = type;
        await _database.Connection.ExecuteAsync(
            "UPDATE MailItemFolder SET FolderName = ?, ParentRemoteFolderId = ?, EasFolderType = ?, SpecialFolderType = ?, IsSystemFolder = ?, IsSticky = ? WHERE Id = ?",
            row.FolderName, row.ParentRemoteFolderId, row.EasFolderType, row.SpecialFolderType, row.IsSystemFolder, row.IsSticky, row.Id).ConfigureAwait(false);
        return true;
    }

    private async Task UpsertCollectionStateAsync(string serverId, string parentId, string name, int type, List<EasCollectionState> states)
    {
        var state = states.FirstOrDefault(state => state.ServerId == serverId);
        if (state == null)
        {
            state = new EasCollectionState { Id = EasDataMapper.StableId(EasDataMapper.Key(_account.Id, serverId, string.Empty)),
                AccountId = _account.Id, ServerId = serverId, SyncKey = "0" };
            states.Add(state);
        }
        state.ParentServerId = parentId;
        state.DisplayName = name;
        state.Type = type;
        await EnsureCollectionAsync(state).ConfigureAwait(false);
        await _database.Connection.InsertOrReplaceAsync(state, typeof(EasCollectionState)).ConfigureAwait(false);
    }

    /// <summary>Removes a collection and everything below it; a parent's delete is not always reported per child.</summary>
    private async Task<bool> RemoveTreeAsync(string serverId, List<MailItemFolder> mailRows, List<EasCollectionState> states)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(serverId);
        var mailChanged = false;
        var order = new List<string>();
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
                continue;
            order.Add(current);
            foreach (var child in mailRows.Where(row => row.EasFolderType > 0 && row.ParentRemoteFolderId == current && !string.IsNullOrEmpty(row.RemoteFolderId)))
                pending.Enqueue(child.RemoteFolderId);
            foreach (var child in states.Where(state => state.ParentServerId == current))
                pending.Enqueue(child.ServerId);
        }

        order.Reverse();
        foreach (var id in order)
        {
            foreach (var row in mailRows.Where(row => row.EasFolderType > 0 && row.RemoteFolderId == id).ToList())
            {
                await RemoveCollectionAsync(EasCollection.From(row)).ConfigureAwait(false);
                mailRows.Remove(row);
                mailChanged = true;
            }
            foreach (var state in states.Where(state => state.ServerId == id).ToList())
            {
                await RemoveCollectionAsync(EasCollection.From(state)).ConfigureAwait(false);
                states.Remove(state);
            }
        }
        return mailChanged;
    }

    private async Task EnsureCollectionAsync(EasCollectionState state)
    {
        var id = state.Id;
        var color = string.IsNullOrWhiteSpace(_account.AccountColorHex) ? ColorPalette.GetColors()[0] : _account.AccountColorHex;
        switch (ItemClass(state.Type))
        {
            case "Calendar" when _account.IsCalendarAccessGranted && _account.CalendarIntegrationSource == AccountIntegrationSource.Provider:
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(id).ConfigureAwait(false);
                if (calendar == null)
                    await _calendars.InsertAccountCalendarAsync(new AccountCalendar { Id = id, AccountId = _account.Id,
                        RemoteCalendarId = state.ServerId, Name = state.DisplayName, IsPrimary = state.Type == 8,
                        BackgroundColorHex = color, MailAccount = _account, TimeZone = "UTC" }).ConfigureAwait(false);
                else if (calendar.Name != state.DisplayName || string.IsNullOrWhiteSpace(calendar.BackgroundColorHex))
                {
                    calendar.MailAccount = _account;
                    calendar.Name = state.DisplayName;
                    if (string.IsNullOrWhiteSpace(calendar.BackgroundColorHex))
                        calendar.BackgroundColorHex = color;
                    await _calendars.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
                }
                state.EntityId = id;
                break;
            case "Contacts" when _account.IsContactAccessGranted && _account.ContactIntegrationSource == AccountIntegrationSource.Provider:
                var bookId = EasDataMapper.ProviderId(state.ServerId);
                var books = await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false);
                if (state.Type == 9 && !books.Any(book => book.SourceKind == ContactSourceKind.Exchange && book.RemoteId == bookId))
                {
                    var carried = books.Where(book => book.SourceKind == ContactSourceKind.Exchange && book.IsDefault &&
                        ExchangeTransportRekey.IsOtherTransport(book.RemoteId, bookId)).ToList();
                    if (carried.Count == 1)
                        await _contacts.RekeyAddressBookAsync(carried[0].Id, bookId, new Dictionary<Guid, string>()).ConfigureAwait(false);
                }
                var book = await _contacts.GetOrCreateProviderAddressBookAsync(_account.Id, ContactSourceKind.Exchange, bookId, state.DisplayName, state.Type == 9).ConfigureAwait(false);
                state.EntityId = book?.Id;
                break;
            case "Tasks" when _account.IsTaskAccessGranted && _account.TaskIntegrationSource == AccountIntegrationSource.Provider:
                var listId = EasDataMapper.ProviderId(state.ServerId);
                var lists = await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false);
                var list = lists.FirstOrDefault(list => list.RemoteId == listId && list.SourceKind == TaskSourceKind.Exchange);
                if (list == null && state.Type == 7)
                {
                    var carried = lists.Where(item => item.SourceKind == TaskSourceKind.Exchange && item.IsDefault &&
                        ExchangeTransportRekey.IsOtherTransport(item.RemoteId, listId)).ToList();
                    if (carried.Count == 1)
                    {
                        list = carried[0];
                        await _tasks.RekeyTaskListAsync(list.Id, listId, new Dictionary<Guid, string>()).ConfigureAwait(false);
                        list.RemoteId = listId;
                        list.RemoteVersion = null;
                        list.DeltaLink = list.ListDeltaLink = list.TaskDeltaLink = null;
                    }
                }
                list ??= new AccountTaskList { Id = id, MailAccountId = _account.Id, SourceKind = TaskSourceKind.Exchange, RemoteId = listId,
                    IsDefault = state.Type == 7, ColorHex = color };
                list.Title = state.DisplayName;
                await _tasks.UpsertRemoteTaskListAsync(list).ConfigureAwait(false);
                state.EntityId = list.Id;
                break;
        }
    }

    private string HierarchyKey() => string.IsNullOrEmpty(_account.SynchronizationDeltaIdentifier) ? "0" : _account.SynchronizationDeltaIdentifier;

    private async Task SaveHierarchyKeyAsync(string key)
    {
        _account.SynchronizationDeltaIdentifier = key;
        await _database.Connection.ExecuteAsync("UPDATE MailAccount SET SynchronizationDeltaIdentifier = ? WHERE Id = ?", key, _account.Id).ConfigureAwait(false);
    }

    private Task<List<MailItemFolder>> LoadMailRowsAsync()
        => _database.Connection.QueryAsync<MailItemFolder>("SELECT * FROM MailItemFolder WHERE MailAccountId = ?", _account.Id);

    private Task<List<EasCollectionState>> LoadStatesAsync()
        => _database.Connection.Table<EasCollectionState>().Where(state => state.AccountId == _account.Id).ToListAsync();

    private Task<int> CountMailCollectionsAsync()
        => _database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MailItemFolder WHERE MailAccountId = ? AND EasFolderType > 0", _account.Id);

    private static bool IsMailType(int type) => ItemClass(type) == "Email";

    private static readonly SpecialFolderType[] ServerRoles =
        [SpecialFolderType.Inbox, SpecialFolderType.Draft, SpecialFolderType.Deleted, SpecialFolderType.Sent];

    private static SpecialFolderType ServerRole(int type) => type switch
    {
        2 => SpecialFolderType.Inbox,
        3 => SpecialFolderType.Draft,
        4 => SpecialFolderType.Deleted,
        5 => SpecialFolderType.Sent,
        _ => SpecialFolderType.Other
    };

    /// <summary>
    /// Requires an item answer of status 1. Status 7 (conflict, server wins) is reported to the user as such: the item
    /// is not sent again and the server's version arrives with the next synchronization.
    /// </summary>
    internal static void RequireItemSuccess(XElement response)
    {
        if (StatusOf(response) == "7")
            throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncChangeConflict);
        EasClient.RequireSuccess(response);
    }

    private static string? StatusOf(XElement response)
        => response.Elements().FirstOrDefault(element => element.Name.LocalName == "Status")?.Value;

    private async Task SynchronizeCollectionAsync(EasCollection state, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        try
        {
            await SynchronizeCollectionCoreAsync(state, downloaded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _collectionPassesUtc[state.ServerId] = DateTime.UtcNow;
        }
    }

    private async Task SynchronizeCollectionCoreAsync(EasCollection state, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var sync = EasDataMapper.Sync;
        var recovered = false;
        if (ItemClass(state.Type) == "Calendar" && state.SyncKey != "0" && string.IsNullOrEmpty(state.Generation))
        {
            var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
            var duplicateClientIds = shadows.Select(shadow => EasDataMapper.ClientEventId(Parse(shadow.ApplicationData)))
                .OfType<Guid>().GroupBy(id => id).Where(group => group.Skip(1).Any()).Select(group => group.Key);
            foreach (var clientId in duplicateClientIds)
            {
                var local = await _database.Connection.FindAsync<CalendarItem>(clientId).ConfigureAwait(false);
                if (local?.CalendarId == state.Id)
                {
                    state.SyncKey = "0";
                    break;
                }
            }
        }
        if (state.SyncKey == "0")
        {
            state.Generation = Guid.NewGuid().ToString("N");
            var handshake = await SyncWithRecoveryAsync(state, "0", null, cancellationToken).ConfigureAwait(false);
            if (handshake == null)
                return;
            await SaveKeyAsync(state, handshake).ConfigureAwait(false);
        }

        var windowSize = DefaultWindowSize;
        var emptyPages = 0;
        for (var page = 0; page < 10000; page++)
        {
            XElement? response;
            try { response = await SyncWithRecoveryAsync(state, state.SyncKey, null, cancellationToken, windowSize).ConfigureAwait(false); }
            catch (EasCommandException exception) when (exception.Status == "3" && !recovered)
            {
                recovered = true;
                state.Generation = Guid.NewGuid().ToString("N");
                var handshake = await SyncWithRecoveryAsync(state, "0", null, cancellationToken).ConfigureAwait(false);
                if (handshake == null)
                    return;
                await SaveKeyAsync(state, handshake).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (windowSize > 1 && IsOversizedPage(exception, cancellationToken))
            {
                // The same key is sent again with a smaller window; the server keeps the previous key valid for that.
                windowSize /= 2;
                Serilog.Log.Warning(exception, "EAS collection {Collection} for {Account}: a page was too large or too slow; retrying with {WindowSize} items.",
                    state.Name, _account.Address, windowSize);
                continue;
            }
            if (response == null)
                return;
            await ApplyPageAsync(state, response, downloaded, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, response).ConfigureAwait(false);
            if (response.Element(sync + "MoreAvailable") != null)
            {
                emptyPages = response.Element(sync + "Commands")?.HasElements == true ? 0 : emptyPages + 1;
                if (emptyPages < 3)
                    continue;
                Serilog.Log.Warning("EAS collection {Collection} for {Account} reports more changes but sent none in three pages; continuing next time.",
                    state.Name, _account.Address);
                return;
            }
            if (!string.IsNullOrEmpty(state.Generation))
            {
                var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
                await ReconcileResetAsync(state, shadows.Where(shadow => shadow.Generation == state.Generation).ToList(), downloaded, cancellationToken).ConfigureAwait(false);
                foreach (var shadow in shadows.Where(shadow => shadow.Generation != state.Generation))
                    await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);
                state.Generation = string.Empty;
                await SaveSyncStateAsync(state).ConfigureAwait(false);
            }
            return;
        }
        throw new InvalidDataException("The EAS server exceeded the synchronization page limit.");
    }

    private const int DefaultWindowSize = 100;

    /// <summary>
    /// One Sync request for a collection with the recoveries MS-ASCMD asks for: status 12 (the folder hierarchy
    /// changed) and 8 (the collection is not found) read the hierarchy and retry once; 5 and 16 (transient server
    /// errors) retry with a growing delay. Null means the collection is gone after the hierarchy was read.
    /// </summary>
    private async Task<XElement?> SyncWithRecoveryAsync(EasCollection state, string syncKey, XElement? commands, CancellationToken cancellationToken,
        int windowSize = DefaultWindowSize, bool getChanges = true, bool deletesAsMoves = true)
    {
        var hierarchyRead = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.SyncAsync(state.ServerId, syncKey, ItemClass(state.Type), commands, cancellationToken, windowSize, getChanges, deletesAsMoves).ConfigureAwait(false);
            }
            catch (EasCommandException exception) when (exception.Status is "12" or "8" && !hierarchyRead)
            {
                hierarchyRead = true;
                Serilog.Log.Information("EAS collection {Collection} for {Account} returned status {Status}; reading the folder hierarchy again.",
                    state.Name, _account.Address, exception.Status);
                await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
                if (!await IsCollectionKnownAsync(state).ConfigureAwait(false))
                    return null;
            }
            catch (EasCommandException exception) when (exception.Status is "5" or "16" && attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(2 << attempt);
                Serilog.Log.Warning("EAS collection {Collection} for {Account} returned transient status {Status}; retrying in {Delay}.",
                    state.Name, _account.Address, exception.Status, delay);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> IsCollectionKnownAsync(EasCollection state) => state.IsMail
        ? await _database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MailItemFolder WHERE Id = ? AND RemoteFolderId = ? AND EasFolderType > 0", state.Id, state.ServerId).ConfigureAwait(false) > 0
        : await _database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM EasCollectionState WHERE Id = ? AND ServerId = ?", state.Id, state.ServerId).ConfigureAwait(false) > 0;

    /// <summary>A page over the response size limit, or one the HTTP timeout cut off; a smaller window can still succeed.</summary>
    private static bool IsOversizedPage(Exception exception, CancellationToken cancellationToken)
        => exception is EasResponseTooLargeException || (exception is TaskCanceledException { InnerException: TimeoutException } && !cancellationToken.IsCancellationRequested);

    private async Task ReconcileResetAsync(EasCollection state, List<EasItemShadow> snapshot, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var itemClass = ItemClass(state.Type);
        if (itemClass is not ("Contacts" or "Tasks"))
            return;

        var folderId = EasDataMapper.ProviderId(state.ServerId);
        var remoteIds = snapshot.Select(shadow => EasDataMapper.ProviderId(shadow.RemoteId)).ToHashSet(StringComparer.Ordinal);
        if (itemClass == "Contacts")
        {
            var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false))
                .First(item => item.SourceKind == ContactSourceKind.Exchange && item.RemoteId == folderId);
            var current = await _contacts.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false);
            var incoming = snapshot.Select(shadow => EasDataMapper.ReadContact(Parse(shadow.ApplicationData), _account.Id, book.Id,
                EasDataMapper.ProviderId(shadow.RemoteId))).ToList();
            var matches = ExchangeTransportRekey.MatchContacts(current, incoming);
            if (matches.Count > 0)
                await _contacts.RekeyAddressBookAsync(book.Id, book.RemoteId, matches).ConfigureAwait(false);
            foreach (var shadow in snapshot)
                await ProjectIsolatedAsync(state, shadow, downloaded, cancellationToken).ConfigureAwait(false);
            var deleted = (await _contacts.GetContactsByAddressBookAsync(book.Id).ConfigureAwait(false))
                .Where(item => item.PendingMutation == ContactPendingMutation.None && !string.IsNullOrEmpty(item.RemoteId) && !remoteIds.Contains(item.RemoteId))
                .Select(item => item.RemoteId).ToArray();
            await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(Array.Empty<AccountContact>(), deleted, state.SyncKey), false).ConfigureAwait(false);
        }
        else
        {
            var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false))
                .First(item => item.SourceKind == TaskSourceKind.Exchange && item.RemoteId == folderId);
            var current = await _database.Connection.Table<AccountTask>().Where(item => item.TaskListId == list.Id).ToListAsync().ConfigureAwait(false);
            var incoming = snapshot.Select(shadow => EasDataMapper.ReadTask(Parse(shadow.ApplicationData), list,
                EasDataMapper.ProviderId(shadow.RemoteId))).ToList();
            var matches = ExchangeTransportRekey.MatchTasks(current, incoming);
            if (matches.Count > 0)
                await _tasks.RekeyTaskListAsync(list.Id, list.RemoteId, matches).ConfigureAwait(false);
            foreach (var shadow in snapshot)
                await ProjectIsolatedAsync(state, shadow, downloaded, cancellationToken).ConfigureAwait(false);
            var deleted = (await _database.Connection.Table<AccountTask>().Where(item => item.TaskListId == list.Id).ToListAsync().ConfigureAwait(false))
                .Where(item => item.PendingMutation == TaskPendingMutation.None && !string.IsNullOrEmpty(item.RemoteId) && !remoteIds.Contains(item.RemoteId))
                .Select(item => item.RemoteId).ToArray();
            await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id, DeletedTaskRemoteIds = deleted,
                TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies one Sync response. Each item is applied on its own: an item whose data cannot be read is reported and
    /// skipped, its shadow is still written and the sync key still advances, so one malformed message never stalls a
    /// folder. Any other failure leaves the page unapplied and the key unsaved.
    /// </summary>
    private async Task ApplyPageAsync(EasCollection state, XElement response, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var sync = EasDataMapper.Sync;
        foreach (var command in response.Element(sync + "Commands")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteId = command.Element(sync + "ServerId")?.Value;
            if (string.IsNullOrEmpty(remoteId))
            {
                Serilog.Log.Warning("EAS collection {Collection} for {Account} returned a {Command} without an item id; skipped.", state.Name, _account.Address, command.Name.LocalName);
                continue;
            }

            XElement? data = null;
            try
            {
                if (command.Name.LocalName is "Delete" or "SoftDelete")
                {
                    await DeleteProjectionAsync(state, remoteId).ConfigureAwait(false);
                    continue;
                }
                if (command.Name.LocalName is not ("Add" or "Change"))
                {
                    Serilog.Log.Warning("EAS collection {Collection} for {Account} returned an unknown {Command}; skipped.", state.Name, _account.Address, command.Name.LocalName);
                    continue;
                }

                var incoming = command.Element(sync + "ApplicationData") ?? new XElement(sync + "ApplicationData");
                data = await MergeWithShadowAsync(state, remoteId, incoming).ConfigureAwait(false);
                // Match identities across transports only after the complete snapshot establishes unique candidates.
                if (string.IsNullOrEmpty(state.Generation) || ItemClass(state.Type) is not ("Contacts" or "Tasks"))
                    await ProjectAsync(state, remoteId, data, downloaded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsItemFailure(exception, cancellationToken))
            {
                _itemFailed(state, remoteId, exception);
            }

            if (data != null)
                await WriteShadowAsync(state, remoteId, data).ConfigureAwait(false);
        }
    }

    private async Task<XElement> MergeWithShadowAsync(EasCollection state, string remoteId, XElement incoming)
    {
        if (ItemClass(state.Type) == "Email")
            return new XElement(incoming);
        var shadow = await _database.Connection.FindAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, state.ServerId, remoteId)).ConfigureAwait(false);
        var previous = shadow?.ApplicationData;
        if (!string.IsNullOrEmpty(previous))
        {
            var retained = Parse(previous);
            retained.Elements().Where(element => element.Name != EasDataMapper.Body + "Body" && element.Name != EasDataMapper.Contact + "Picture").Remove();
            previous = retained.ToString(SaveOptions.DisableFormatting);
        }
        return Merge(string.IsNullOrEmpty(previous) ? null : previous, incoming);
    }

    /// <summary>
    /// Mail shadows only record that the item exists in this collection generation; the message row holds its state,
    /// so its application data is not kept.
    /// </summary>
    private Task WriteShadowAsync(EasCollection state, string remoteId, XElement data)
        => _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = EasDataMapper.Key(_account.Id, state.ServerId, remoteId), AccountId = _account.Id,
            CollectionId = state.Id, RemoteId = remoteId,
            ApplicationData = ItemClass(state.Type) == "Email" ? string.Empty : data.ToString(SaveOptions.DisableFormatting),
            Generation = state.Generation }, typeof(EasItemShadow));

    /// <summary>
    /// Whether a failure is the item's own malformed data, which skips only that item. Every other failure (storage,
    /// connection, account, cancellation, or a defect) aborts the page before its key is saved, so the page is read again.
    /// </summary>
    private static bool IsItemFailure(Exception exception, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && exception is InvalidDataException or FormatException or XmlException or ArgumentException;

    private async Task ProjectIsolatedAsync(EasCollection state, EasItemShadow shadow, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        try { await ProjectAsync(state, shadow.RemoteId, Parse(shadow.ApplicationData), downloaded, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsItemFailure(exception, cancellationToken))
        {
            _itemFailed(state, shadow.RemoteId, exception);
        }
    }

    private async Task ProjectAsync(EasCollection state, string remoteId, XElement data, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var id = state.Id;
        switch (ItemClass(state.Type))
        {
            case "Email":
                await ProjectMailAsync(state, remoteId, data, downloaded).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).First(item => item.RemoteId == EasDataMapper.ProviderId(state.ServerId) && item.SourceKind == ContactSourceKind.Exchange);
                var providerId = EasDataMapper.ProviderId(remoteId);
                var contact = EasDataMapper.ReadContact(data, _account.Id, book.Id, providerId);
                if (data.Element(EasDataMapper.Contact + "Picture") is XElement photo)
                {
                    var current = await _database.Connection.Table<AccountContact>().FirstOrDefaultAsync(contact => contact.AddressBookId == book.Id && contact.RemoteId == providerId).ConfigureAwait(false);
                    var bytes = new byte[photo.Value.Length * 3 / 4 + 3];
                    if (string.IsNullOrEmpty(photo.Value) || !Convert.TryFromBase64String(photo.Value, bytes, out var length))
                        contact.RemotePhotoKey = string.Empty;
                    else
                    {
                        bytes = bytes[..length];
                        contact.RemotePhotoKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                        contact.ContactPictureFileId = current != null && current.RemotePhotoKey == contact.RemotePhotoKey ? current.ContactPictureFileId
                            : await _pictures.SavePictureAsync(PictureKind.Contact, bytes).ConfigureAwait(false);
                    }
                }
                await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(new[] { contact }, Array.Empty<string>(), state.SyncKey), false).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).First(list => list.RemoteId == EasDataMapper.ProviderId(state.ServerId) && list.SourceKind == TaskSourceKind.Exchange);
                await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id,
                    Tasks = new[] { EasDataMapper.ReadTask(data, list, EasDataMapper.ProviderId(remoteId)) }, TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
                break;
            case "Calendar":
                await ProjectEventAsync(state, remoteId, data).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Stores a message from its Sync fields; a known message takes only the fields the item carries, so local state
    /// such as pinning is kept. A new message received before the account's range cutoff is not stored: no email
    /// FilterType expresses a Wino range (see <see cref="EasClient"/>), so mail is synchronized without one and the
    /// fixed cutoff the other synchronizers apply is applied here.
    /// </summary>
    private async Task ProjectMailAsync(EasCollection state, string remoteId, XElement data, List<MailCopy> downloaded)
    {
        var key = EasDataMapper.Key(_account.Id, state.ServerId, remoteId);
        var existing = await _mail.GetSingleMailItemAsync(key, state.ServerId).ConfigureAwait(false);
        if (existing != null)
        {
            var wasRead = existing.IsRead;
            var wasFlagged = existing.IsFlagged;
            EasDataMapper.ApplyMailFields(existing, data);
            if (data.Elements().Any(element => element.Name != EasDataMapper.Email + "Read" && element.Name != EasDataMapper.Email + "Flag"))
                await _mail.UpdateMailAsync(existing).ConfigureAwait(false);
            else
            {
                if (existing.IsRead != wasRead)
                    await _mail.ChangeReadStatusAsync(existing.Id, existing.IsRead).ConfigureAwait(false);
                if (existing.IsFlagged != wasFlagged)
                    await _mail.ChangeFlagStatusAsync(existing.Id, existing.IsFlagged).ConfigureAwait(false);
            }
            return;
        }

        var folder = await _folders.GetFolderAsync(state.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS mail folder is missing.");
        var package = EasDataMapper.Mail(folder, remoteId, data);
        var cutoff = _account.InitialSynchronizationRange.ToCutoffDateUtc(_account.CreatedAt ?? DateTime.UtcNow);
        if (cutoff.HasValue && package.Copy.CreationDate < cutoff.Value)
        {
            Serilog.Log.Debug("EAS message {RemoteId} in {Collection} was received before the range cutoff {Cutoff:u}; not stored.", remoteId, state.Name, cutoff.Value);
            return;
        }
        if (await _mail.CreateMailAsync(_account.Id, package).ConfigureAwait(false))
            downloaded.Add(package.Copy);
    }

    private async Task DeleteProjectionAsync(EasCollection state, string remoteId)
    {
        var id = state.Id;
        switch (ItemClass(state.Type))
        {
            case "Email":
                await _mail.DeleteMailAsync(_account.Id, EasDataMapper.Key(_account.Id, state.ServerId, remoteId)).ConfigureAwait(false);
                break;
            case "Calendar":
                await _calendars.DeleteCalendarItemAsync(remoteId, id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == EasDataMapper.ProviderId(state.ServerId) && item.SourceKind == ContactSourceKind.Exchange);
                if (book != null)
                    await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(Array.Empty<AccountContact>(), new[] { EasDataMapper.ProviderId(remoteId) }, state.SyncKey), false).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == EasDataMapper.ProviderId(state.ServerId) && list.SourceKind == TaskSourceKind.Exchange);
                if (list != null)
                    await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id, DeletedTaskRemoteIds = new[] { EasDataMapper.ProviderId(remoteId) }, TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, state.ServerId, remoteId)).ConfigureAwait(false);
    }

    private async Task RemoveCollectionAsync(EasCollection state)
    {
        var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
        foreach (var shadow in shadows)
            await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);

        if (state.IsMail)
        {
            // The folder is gone from the server, so everything left in it goes too, local drafts included, through the
            // mail service so the lists update and the MIME files are removed.
            var remaining = await _database.Connection.QueryScalarsAsync<string>("SELECT Id FROM MailCopy WHERE FolderId = ?", state.Id).ConfigureAwait(false);
            var local = remaining.Count(id => !IsServerMailId(id));
            if (local > 0)
                Serilog.Log.Warning("EAS folder {Collection} of {Account} was removed on the server; {Count} local draft(s) in it are removed too.", state.Name, _account.Address, local);
            await _mail.DeleteMailsAsync(_account.Id, remaining).ConfigureAwait(false);
            await _database.Connection.DeleteAsync<MailItemFolder>(state.Id).ConfigureAwait(false);
            return;
        }

        switch (ItemClass(state.Type))
        {
            case "Calendar":
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(state.Id).ConfigureAwait(false);
                if (calendar != null)
                {
                    calendar.MailAccount = _account;
                    await _calendars.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                }
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == EasDataMapper.ProviderId(state.ServerId) && list.SourceKind == TaskSourceKind.Exchange);
                if (list != null) await _tasks.RemoveTaskListAsync(list.Id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == EasDataMapper.ProviderId(state.ServerId) && item.SourceKind == ContactSourceKind.Exchange);
                if (book != null) await _contacts.DeleteAddressBookAsync(book.Id).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<EasCollectionState>(state.Id).ConfigureAwait(false);
    }

    /// <summary>Whether a message id is an ActiveSync item key of this account; local drafts carry other ids.</summary>
    private bool IsServerMailId(string id) => id.StartsWith(_account.Id.ToString("N") + "/", StringComparison.Ordinal);

    /// <summary>The ids of the messages in a folder that came from the server, leaving local drafts out.</summary>
    private async Task<List<string>> ServerMailIdsAsync(Guid folderId)
        => (await _database.Connection.QueryScalarsAsync<string>("SELECT Id FROM MailCopy WHERE FolderId = ?", folderId).ConfigureAwait(false))
            .Where(IsServerMailId).ToList();

    private async Task<EasCollection> GetCollectionAsync(string serverId)
    {
        var folder = await _database.Connection.Table<MailItemFolder>()
            .FirstOrDefaultAsync(row => row.MailAccountId == _account.Id && row.RemoteFolderId == serverId && row.EasFolderType > 0).ConfigureAwait(false);
        if (folder != null)
            return EasCollection.From(folder);
        var state = await _database.Connection.Table<EasCollectionState>()
            .FirstOrDefaultAsync(row => row.AccountId == _account.Id && row.ServerId == serverId).ConfigureAwait(false);
        return state != null ? EasCollection.From(state) : throw new InvalidOperationException("The EAS collection is not synchronized yet.");
    }

    private async Task SaveKeyAsync(EasCollection state, XElement response)
    {
        state.SyncKey = Required(response, EasDataMapper.Sync + "SyncKey");
        if (state.SyncKey == "0")
            throw new InvalidDataException("The EAS server returned an invalid synchronization key.");
        await SaveSyncStateAsync(state).ConfigureAwait(false);
    }

    private Task SaveSyncStateAsync(EasCollection state) => state.IsMail
        ? _database.Connection.ExecuteAsync("UPDATE MailItemFolder SET DeltaToken = ?, EasSyncGeneration = ? WHERE Id = ?", state.SyncKey, state.Generation, state.Id)
        : _database.Connection.ExecuteAsync("UPDATE EasCollectionState SET SyncKey = ?, Generation = ? WHERE Id = ?", state.SyncKey, state.Generation, state.Id);

    internal static XElement Merge(string? previous, XElement incoming)
    {
        var result = previous == null ? new XElement(EasDataMapper.Sync + "ApplicationData") : Parse(previous);
        foreach (var group in incoming.Elements().GroupBy(element => element.Name))
        {
            result.Elements(group.Key).Remove();
            foreach (var element in group)
                result.Add(new XElement(element));
        }
        return result;
    }

    private static XElement Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XElement.Load(reader);
    }

    internal static string ItemClass(int folderType) => folderType switch
    {
        1 or 2 or 3 or 4 or 5 or 6 or 12 => "Email", 7 or 15 => "Tasks", 8 or 13 => "Calendar", 9 or 14 => "Contacts", _ => string.Empty
    };
    private static string Required(XElement element, XName name) => !string.IsNullOrEmpty(element.Element(name)?.Value)
        ? element.Element(name)!.Value : throw new InvalidDataException("The EAS response is missing " + name.LocalName + ".");
    /// <summary>When a FolderSync pass last ended, successfully or not; null when none ran in this session.</summary>
    internal DateTime? LastHierarchyPassUtc
        => Interlocked.Read(ref _hierarchyPassTicks) is var ticks and > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>When a Sync pass over the collection last ended, successfully or not; null when none ran in this session.</summary>
    internal DateTime? LastCollectionPassUtc(string serverId)
        => _collectionPassesUtc.TryGetValue(serverId, out var value) ? value : null;

    /// <summary>
    /// The collections Ping watches: the enabled mail folders and the calendar, contacts and tasks collections the account
    /// takes from the provider. Only collections synchronized at least once qualify; Ping refuses a collection without
    /// a sync key.
    /// </summary>
    internal async Task<List<EasPingFolder>> GetPingFoldersAsync()
    {
        var folders = new List<EasPingFolder>();
        var syncsDrafts = await SupportsServerDraftsAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var folder in await LoadMailRowsAsync().ConfigureAwait(false))
        {
            if (folder.EasFolderType > 0 && folder.IsSynchronizationEnabled && !string.IsNullOrEmpty(folder.RemoteFolderId) &&
                (syncsDrafts || folder.EasFolderType != DraftsFolderType) &&
                folder.DeltaToken is { Length: > 0 } key && key != "0")
                folders.Add(new EasPingFolder(folder.RemoteFolderId, "Email", folder.EasFolderType, folder.Id));
        }

        foreach (var state in await LoadStatesAsync().ConfigureAwait(false))
        {
            var itemClass = ItemClass(state.Type);
            var source = itemClass switch
            {
                "Calendar" => _account.CalendarIntegrationSource,
                "Contacts" => _account.ContactIntegrationSource,
                "Tasks" => _account.TaskIntegrationSource,
                _ => (AccountIntegrationSource?)null
            };
            if (source == AccountIntegrationSource.Provider && !string.IsNullOrEmpty(state.SyncKey) && state.SyncKey != "0")
                folders.Add(new EasPingFolder(state.ServerId, itemClass, state.Type, state.EntityId));
        }

        return folders;
    }

    internal Task<XElement> PingAsync(XElement request, TimeSpan timeout, CancellationToken cancellationToken)
        => _client.PingAsync(request, timeout, cancellationToken);

    internal Task<XElement> GetUserInformationAsync(CancellationToken cancellationToken) => _client.GetUserInformationAsync(cancellationToken);

    internal Task<XElement?> ResolveRecipientPictureAsync(string address, int maxSize, CancellationToken cancellationToken)
        => _client.ResolveRecipientPictureAsync(address, maxSize, cancellationToken);

    public void Dispose() => _client.Dispose();
}
