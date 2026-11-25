using System;
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
using Wino.Core.Domain.Models.Tasks;
using Wino.Services;
using Wino.Services.Interfaces;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed class EasSynchronizationEngine : IDisposable
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal EasSynchronizationEngine(MailAccount account, IDatabaseService database, IAccountService accounts,
        IFolderService folders, IMailService mail, ICalendarService calendars, IContactService contacts, ITaskService tasks, IPictureStorageService pictures,
        Action hierarchyChanged, EasClient? client = null)
    {
        _account = account; _database = database; _accounts = accounts; _folders = folders;
        _mail = mail; _calendars = calendars; _contacts = contacts; _tasks = tasks;
        _pictures = pictures;
        _hierarchyChanged = hierarchyChanged;
        var server = account.ServerInformation ?? throw new InvalidOperationException("EAS server settings are missing.");
        if (server.UseOAuthAuthentication)
            throw new NotSupportedException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncPasswordRequired);
        // Automatic detection keeps the EWS URL it started from; Normalize maps it to the ActiveSync path on the same host.
        var endpoint = ExchangeServerEndpoint.Normalize(server.IncomingServer, ExchangeTransport.ActiveSync)
            ?? throw new InvalidOperationException("The EAS server address is invalid.");
        _client = client ?? new EasClient(account, endpoint.AbsoluteUri, server.IncomingServerUsername,
            server.IncomingServerPassword ?? throw new InvalidOperationException("The EAS password is missing."), new EasAccountStateStore(database));
    }

    internal async Task<bool> IsProtocol16Async(CancellationToken cancellationToken)
        => Version.Parse(await GetProtocolVersionAsync(cancellationToken).ConfigureAwait(false)) >= new Version(16, 0);

    /// <summary>The negotiated MS-ASProtocolVersion; connects first when no request has been made yet.</summary>
    internal async Task<string> GetProtocolVersionAsync(CancellationToken cancellationToken)
    {
        if (_client.ProtocolVersion == null)
            await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _client.ProtocolVersion ?? throw new InvalidDataException("The EAS protocol version was not negotiated.");
    }

    internal async Task<List<MailCopy>> SynchronizeMailAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            var folders = (await LoadMailRowsAsync().ConfigureAwait(false))
                .Where(folder => folder.EasFolderType > 0 && folder.IsSynchronizationEnabled)
                .ToList();
            var downloaded = new List<MailCopy>();
            foreach (var folder in folders)
                await SynchronizeCollectionAsync(EasCollection.From(folder), downloaded, cancellationToken).ConfigureAwait(false);
            return downloaded;
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
                await SynchronizeCollectionAsync(EasCollection.From(state), new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
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

    internal Task<string> MutateAsync(string folderId, string operation, string remoteId, XElement? data, string clientId,
        CancellationToken cancellationToken, Func<string, Task>? persistMutation = null)
        => MutateCoreAsync(folderId, operation, remoteId, data, clientId, persistMutation, cancellationToken);

    internal Task<string> CreateCalendarEventAsync(string folderId, XElement data, string clientId,
        Func<string, Task> persistCreatedItem, CancellationToken cancellationToken)
        => MutateCoreAsync(folderId, "Add", string.Empty, data, clientId, persistCreatedItem, cancellationToken);

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
                command.Add(itemClass is "Email" or "Contacts" ? data
                    : itemClass == "Calendar" ? EasDataMapper.ForEventChange(Merge(shadow?.ApplicationData, data), _client.IsAtLeast("16.0"))
                    : Merge(shadow?.ApplicationData, data));
            }
            var result = await _client.SyncAsync(folderId, state.SyncKey, ItemClass(state.Type), new XElement(sync + "Commands", command), cancellationToken).ConfigureAwait(false);
            var response = result.Element(sync + "Responses")?.Element(sync + operation);
            if (response != null)
                EasClient.RequireSuccess(response);
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
            await SaveKeyAsync(state, result).ConfigureAwait(false);
            return resultId;
        }
        finally { _gate.Release(); }
    }

    internal async Task MoveAsync(string folderId, string remoteId, string destinationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            XNamespace move = "Move:";
            var result = await _client.CommandAsync("MoveItems", new XElement(move + "MoveItems", new XElement(move + "Move",
                new XElement(move + "SrcMsgId", remoteId), new XElement(move + "SrcFldId", folderId), new XElement(move + "DstFldId", destinationId))), cancellationToken).ConfigureAwait(false);
            var response = result?.Element(move + "Response") ?? throw new InvalidDataException("The EAS move response is missing.");
            if (response.Element(move + "Status")?.Value != "3")
                throw new InvalidDataException("The EAS server rejected moving the message.");

            await DeleteProjectionAsync(await GetCollectionAsync(folderId).ConfigureAwait(false), remoteId).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// FolderCreate, FolderUpdate or FolderDelete. The response carries the next hierarchy key, so the change is applied
    /// locally here: the following FolderSync does not report it again.
    /// </summary>
    internal async Task<string> ChangeFolderAsync(string command, string remoteId, string parentId, string displayName, int folderType, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            var request = new XElement(FolderHierarchy + command, new XElement(FolderHierarchy + "SyncKey", HierarchyKey()));
            if (command != "FolderCreate")
                request.Add(new XElement(FolderHierarchy + "ServerId", remoteId));
            if (command != "FolderDelete")
                request.Add(new XElement(FolderHierarchy + "ParentId", string.IsNullOrEmpty(parentId) ? "0" : parentId), new XElement(FolderHierarchy + "DisplayName", displayName));
            if (command == "FolderCreate")
                request.Add(new XElement(FolderHierarchy + "Type", folderType));
            var response = await _client.CommandAsync(command, request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The EAS folder operation response is empty.");
            EasClient.RequireSuccess(response);

            var resultId = command == "FolderCreate" ? Required(response, FolderHierarchy + "ServerId") : remoteId;
            var mailRows = await LoadMailRowsAsync().ConfigureAwait(false);
            var states = await LoadStatesAsync().ConfigureAwait(false);
            var mailChanged = false;
            if (command == "FolderDelete")
                mailChanged = await RemoveServerIdAsync(resultId, mailRows, states).ConfigureAwait(false);
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

    internal async Task SendAsync(byte[] mime, string clientId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _client.SendMailAsync(mime, clientId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<System.Xml.Linq.XElement> FetchAsync(string folderId, string remoteId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _client.FetchAsync(folderId, remoteId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task RespondToMeetingAsync(string folderId, string remoteId, int responseCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            XNamespace meeting = "MeetingResponse:";
            var response = await _client.CommandAsync("MeetingResponse", new XElement(meeting + "MeetingResponse",
                new XElement(meeting + "Request", new XElement(meeting + "UserResponse", responseCode),
                    new XElement(meeting + "CollectionId", folderId), new XElement(meeting + "RequestId", remoteId))), cancellationToken).ConfigureAwait(false);
            EasClient.RequireSuccess(response?.Element(meeting + "Result") ?? throw new InvalidDataException("The EAS meeting response is missing."));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads the folder hierarchy. Mail collections become MailItemFolder rows, the others EasCollectionState rows.
    /// On a full read (key 0, also after a connection method change) rows another transport stored are re-keyed in
    /// place first, so folder ids and their settings survive, and rows the server no longer has are removed.
    /// </summary>
    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken)
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
            if (change.Name == FolderHierarchy + "Delete")
            {
                mailChanged |= await RemoveServerIdAsync(serverId, mailRows, states).ConfigureAwait(false);
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
                    await RemoveServerIdAsync(stale.ServerId, mailRows, states).ConfigureAwait(false);
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
    /// Matches every stored folder row to the mail collections of a full hierarchy read, through the matcher every
    /// Exchange transport shares: rows another transport stored (EWS, MAPI, or a local Drafts folder) and ActiveSync
    /// rows alike, because the server numbers its folders again after a FolderSync from key 0. A row whose id changes
    /// starts its collection over; rows that match nothing are moved to <paramref name="staleRows"/>.
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

        var matches = ExchangeTransportRekey.MatchFolders(mailRows, incoming, ExchangeTransport.ActiveSync, rematchAll: true);
        var claimed = matches.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var row in mailRows.Where(row => !matches.ContainsKey(row.Id) && row.EasFolderType > 0 &&
            types.ContainsKey(row.RemoteFolderId ?? string.Empty) && !claimed.Contains(row.RemoteFolderId ?? string.Empty)))
        {
            matches[row.Id] = row.RemoteFolderId;
            claimed.Add(row.RemoteFolderId);
        }

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

            // Mail ids embed the collection id, so the downloaded mail of a re-keyed row is fetched again. Local
            // drafts never reached the server and stay.
            await _database.Connection.ExecuteAsync("DELETE FROM EasItemShadow WHERE CollectionId = ?", row.Id).ConfigureAwait(false);
            await _database.Connection.ExecuteAsync("DELETE FROM MailCopy WHERE FolderId = ? AND (DraftId IS NULL OR DraftId NOT LIKE ?)",
                row.Id, Wino.Core.Domain.Constants.LocalDraftStartPrefix + "%").ConfigureAwait(false);
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

    private async Task<bool> RemoveServerIdAsync(string serverId, List<MailItemFolder> mailRows, List<EasCollectionState> states)
    {
        var mailChanged = false;
        foreach (var row in mailRows.Where(row => row.EasFolderType > 0 && row.RemoteFolderId == serverId).ToList())
        {
            await RemoveCollectionAsync(EasCollection.From(row)).ConfigureAwait(false);
            mailRows.Remove(row);
            mailChanged = true;
        }
        foreach (var state in states.Where(state => state.ServerId == serverId).ToList())
        {
            await RemoveCollectionAsync(EasCollection.From(state)).ConfigureAwait(false);
            states.Remove(state);
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

    private async Task SynchronizeCollectionAsync(EasCollection state, List<MailCopy> downloaded, CancellationToken cancellationToken)
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
            var handshake = await _client.SyncAsync(state.ServerId, "0", ItemClass(state.Type), null, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, handshake).ConfigureAwait(false);
        }

        for (var page = 0; page < 10000; page++)
        {
            XElement response;
            try { response = await _client.SyncAsync(state.ServerId, state.SyncKey, ItemClass(state.Type), null, cancellationToken).ConfigureAwait(false); }
            catch (EasCommandException exception) when (exception.Status == "3" && !recovered)
            {
                recovered = true;
                state.Generation = Guid.NewGuid().ToString("N");
                var handshake = await _client.SyncAsync(state.ServerId, "0", ItemClass(state.Type), null, cancellationToken).ConfigureAwait(false);
                await SaveKeyAsync(state, handshake).ConfigureAwait(false);
                continue;
            }
            await ApplyPageAsync(state, response, downloaded, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, response).ConfigureAwait(false);
            if (response.Element(sync + "MoreAvailable") == null)
            {
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
        }
        throw new InvalidDataException("The EAS server exceeded the synchronization page limit.");
    }

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
                await ProjectAsync(state, shadow.RemoteId, Parse(shadow.ApplicationData), downloaded, cancellationToken).ConfigureAwait(false);
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
                await ProjectAsync(state, shadow.RemoteId, Parse(shadow.ApplicationData), downloaded, cancellationToken).ConfigureAwait(false);
            var deleted = (await _database.Connection.Table<AccountTask>().Where(item => item.TaskListId == list.Id).ToListAsync().ConfigureAwait(false))
                .Where(item => item.PendingMutation == TaskPendingMutation.None && !string.IsNullOrEmpty(item.RemoteId) && !remoteIds.Contains(item.RemoteId))
                .Select(item => item.RemoteId).ToArray();
            await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id, DeletedTaskRemoteIds = deleted,
                TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
        }
    }

    private async Task ApplyPageAsync(EasCollection state, XElement response, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var sync = EasDataMapper.Sync;
        foreach (var command in response.Element(sync + "Commands")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteId = Required(command, sync + "ServerId");
            if (command.Name.LocalName is "Delete" or "SoftDelete")
            {
                await DeleteProjectionAsync(state, remoteId).ConfigureAwait(false);
                continue;
            }
            if (command.Name.LocalName is not ("Add" or "Change"))
                throw new InvalidDataException("The EAS server returned an unknown synchronization operation.");

            var key = EasDataMapper.Key(_account.Id, state.ServerId, remoteId);
            var shadow = await _database.Connection.FindAsync<EasItemShadow>(key).ConfigureAwait(false);
            var incoming = command.Element(sync + "ApplicationData") ?? throw new InvalidDataException("EAS application data is missing.");
            var previous = shadow?.ApplicationData;
            if (previous != null && ItemClass(state.Type) != "Email")
            {
                var retained = Parse(previous);
                retained.Elements().Where(element => element.Name != EasDataMapper.Body + "Body" && element.Name != EasDataMapper.Contact + "Picture").Remove();
                previous = retained.ToString(SaveOptions.DisableFormatting);
            }
            var data = Merge(previous, incoming);
            // Match identities across transports only after the complete snapshot establishes unique candidates.
            if (string.IsNullOrEmpty(state.Generation) || ItemClass(state.Type) is not ("Contacts" or "Tasks"))
                await ProjectAsync(state, remoteId, data, downloaded, cancellationToken).ConfigureAwait(false);
            if (ItemClass(state.Type) == "Email")
                data.Element(EasDataMapper.Body + "Body")?.Remove();
            await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                CollectionId = state.Id, RemoteId = remoteId, ApplicationData = data.ToString(SaveOptions.DisableFormatting),
                Generation = state.Generation }, typeof(EasItemShadow)).ConfigureAwait(false);
        }
    }

    private async Task ProjectAsync(EasCollection state, string remoteId, XElement data, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var id = state.Id;
        switch (ItemClass(state.Type))
        {
            case "Email":
                var folder = await _folders.GetFolderAsync(id).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS mail folder is missing.");
                var existing = await _mail.GetSingleMailItemAsync(EasDataMapper.Key(_account.Id, state.ServerId, remoteId), state.ServerId).ConfigureAwait(false);
                var cutoff = _account.InitialSynchronizationRange.ToCutoffDateUtc(_account.CreatedAt ?? DateTime.UtcNow);
                XNamespace emailFields = "Email:";
                var received = EasDataMapper.Date(EasDataMapper.Value(data, emailFields, "DateReceived"));
                if (existing == null && cutoff.HasValue && received.HasValue && received.Value < cutoff.Value)
                    break;
                var body = data.Element(EasDataMapper.Body + "Body");
                if (existing != null && body == null)
                {
                    XNamespace email = "Email:";
                    if (data.Element(email + "Read") != null)
                        await _mail.ChangeReadStatusAsync(existing.Id, EasDataMapper.Value(data, email, "Read") == "1").ConfigureAwait(false);
                    if (data.Element(email + "Flag") is XElement flag)
                        await _mail.ChangeFlagStatusAsync(existing.Id, EasDataMapper.Value(flag, email, "Status") == "2").ConfigureAwait(false);
                    break;
                }
                if (body == null || EasDataMapper.Value(body, EasDataMapper.Body, "Type") != "4" || EasDataMapper.Value(body, EasDataMapper.Body, "Truncated") == "1")
                {
                    var fetched = await _client.FetchAsync(state.ServerId, remoteId, cancellationToken).ConfigureAwait(false);
                    data = Merge(data.ToString(SaveOptions.DisableFormatting), fetched);
                }
                var package = EasDataMapper.Mail(EasDataMapper.ReadMime(data), folder, remoteId, data);
                if (existing == null && cutoff.HasValue && package.Copy.CreationDate < cutoff.Value)
                    break;
                if (existing == null)
                {
                    if (!await _mail.CreateMailAsync(_account.Id, package).ConfigureAwait(false))
                        throw new IOException("The EAS message could not be persisted.");
                    downloaded.Add(package.Copy);
                }
                else
                {
                    package.Copy.UniqueId = existing.UniqueId; package.Copy.FileId = existing.FileId;
                    await _mail.UpdateMailAsync(package.Copy).ConfigureAwait(false);
                }
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).First(item => item.RemoteId == EasDataMapper.ProviderId(state.ServerId) && item.SourceKind == ContactSourceKind.Exchange);
                var providerId = EasDataMapper.ProviderId(remoteId);
                var contact = EasDataMapper.ReadContact(data, _account.Id, book.Id, providerId);
                if (data.Element(EasDataMapper.Contact + "Picture") is XElement photo)
                {
                    var current = await _database.Connection.Table<AccountContact>().FirstOrDefaultAsync(contact => contact.AddressBookId == book.Id && contact.RemoteId == providerId).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(photo.Value))
                        contact.RemotePhotoKey = string.Empty;
                    else
                    {
                        var bytes = Convert.FromBase64String(photo.Value);
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
                var calendar = await _calendars.GetAccountCalendarAsync(id).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS calendar is missing.");
                var item = EasDataMapper.ReadEvent(data, calendar, remoteId);
                var storedEvent = await _calendars.GetCalendarItemAsync(calendar.Id, remoteId).ConfigureAwait(false);
                var trackingId = storedEvent?.RemoteEventId.GetClientTrackingId();
                if (EasDataMapper.ClientEventId(data) is Guid clientId)
                {
                    var local = await _database.Connection.FindAsync<CalendarItem>(clientId).ConfigureAwait(false);
                    if (local?.CalendarId == calendar.Id)
                    {
                        if (storedEvent != null && storedEvent.Id != local.Id)
                            await _calendars.DeleteCalendarItemAsync(storedEvent.Id).ConfigureAwait(false);
                        storedEvent = local;
                        trackingId = clientId;
                    }
                }
                if (storedEvent != null)
                    item.Id = storedEvent.Id;
                item.RemoteEventId = remoteId.WithClientTrackingId(trackingId);
                var attendees = EasDataMapper.ReadAttendees(data, item.Id);
                if (storedEvent == null)
                    await _calendars.CreateNewCalendarItemAsync(item, attendees).ConfigureAwait(false);
                else
                    await _calendars.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
                var reminder = EasDataMapper.Value(data, EasDataMapper.Calendar, "Reminder");
                var reminders = new List<Reminder>();
                if (int.TryParse(reminder, out var minutes) && minutes >= 0)
                    reminders.Add(new Reminder { Id = EasDataMapper.StableId(item.Id.ToString("N") + "/reminder"), CalendarItemId = item.Id,
                        DurationInSeconds = checked(minutes * 60) });
                await _calendars.SaveRemindersAsync(item.Id, reminders).ConfigureAwait(false);
                var exceptionIds = new HashSet<Guid>();
                foreach (var exception in data.Element(EasDataMapper.Calendar + "Exceptions")?.Elements(EasDataMapper.Calendar + "Exception") ?? Enumerable.Empty<XElement>())
                {
                    if (EasDataMapper.Value(exception, EasDataMapper.Calendar, "Deleted") == "1")
                        continue;
                    var exceptionStart = Required(exception, EasDataMapper.Calendar + "ExceptionStartTime");
                    var exceptionData = Merge(data.ToString(SaveOptions.DisableFormatting), exception);
                    var startElement = EasDataMapper.Calendar + "StartTime";
                    var endElement = EasDataMapper.Calendar + "EndTime";
                    if (exception.Element(startElement) == null)
                        exceptionData.SetElementValue(startElement, exceptionStart);
                    if (exception.Element(endElement) == null)
                    {
                        var utcStart = EasDataMapper.Date(exceptionStart) ?? throw new InvalidDataException("The EAS exception start is missing.");
                        var zone = TimeZoneInfo.FindSystemTimeZoneById(item.StartTimeZone);
                        var endLocal = TimeZoneInfo.ConvertTimeFromUtc(utcStart, zone).AddSeconds(item.DurationInSeconds);
                        var endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified), zone);
                        exceptionData.SetElementValue(endElement, endUtc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    exceptionData.Element(EasDataMapper.Calendar + "Recurrence")?.Remove();
                    exceptionData.Element(EasDataMapper.Calendar + "Exceptions")?.Remove();
                    var child = EasDataMapper.ReadEvent(exceptionData, calendar, remoteId + "/exception/" + exceptionStart);
                    child.RecurringCalendarItemId = item.Id;
                    exceptionIds.Add(child.Id);
                    var childAttendees = EasDataMapper.ReadAttendees(exceptionData, child.Id);
                    if (await _calendars.GetCalendarItemAsync(child.Id).ConfigureAwait(false) == null)
                        await _calendars.CreateNewCalendarItemAsync(child, childAttendees).ConfigureAwait(false);
                    else
                        await _calendars.UpdateCalendarItemAsync(child, childAttendees).ConfigureAwait(false);
                }
                foreach (var stale in (await _calendars.GetRecurringChildrenAsync(item.Id).ConfigureAwait(false)).Where(child => !exceptionIds.Contains(child.Id)))
                    await _calendars.DeleteCalendarItemAsync(stale.Id).ConfigureAwait(false);
                break;
        }
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
            await _database.Connection.ExecuteAsync("DELETE FROM MailCopy WHERE FolderId = ?", state.Id).ConfigureAwait(false);
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
    public void Dispose() => _client.Dispose();
}
