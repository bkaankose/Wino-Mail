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
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Tasks;
using Wino.Services;

namespace Wino.Core.Synchronizers.Exchange.ActiveSync;

internal sealed class EasSynchronizationEngine : IDisposable
{
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal EasSynchronizationEngine(MailAccount account, IDatabaseService database, IAccountService accounts,
        IFolderService folders, IMailService mail, ICalendarService calendars, IContactService contacts, ITaskService tasks, IPictureStorageService pictures,
        EasClient? client = null)
    {
        _account = account; _database = database; _accounts = accounts; _folders = folders;
        _mail = mail; _calendars = calendars; _contacts = contacts; _tasks = tasks;
        _pictures = pictures;
        var server = account.ServerInformation ?? throw new InvalidOperationException("EAS server settings are missing.");
        if (server.UseOAuthAuthentication)
            throw new NotSupportedException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncPasswordRequired);
        _client = client ?? new EasClient(server.IncomingServer, server.IncomingServerUsername,
            server.IncomingServerPassword ?? throw new InvalidOperationException("The EAS password is missing."), account.Id);
    }

    internal async Task<List<MailCopy>> SynchronizeAsync(string itemClass, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            var states = await _database.Connection.Table<MailItemFolder>().Where(state => state.MailAccountId == _account.Id && state.EasFolderType > 0).ToListAsync().ConfigureAwait(false);
            if (!states.Any(state => ItemClass(state.EasFolderType) == itemClass))
                throw new NotSupportedException("The EAS server did not expose a " + itemClass + " collection for this account. Check synchronization availability with your mail provider.");
            var downloaded = new List<MailCopy>();
            foreach (var state in states.Where(state => state.EasFolderType != 3 && ItemClass(state.EasFolderType) == itemClass))
            {
                if (itemClass == "Email")
                {
                    var folder = await _folders.GetFolderAsync(_account.Id, state.RemoteFolderId).ConfigureAwait(false);
                    if (folder?.IsSynchronizationEnabled != true)
                        continue;
                }
                await SynchronizeCollectionAsync(state, downloaded, cancellationToken).ConfigureAwait(false);
            }
            if (itemClass == "Calendar" && _account.CalendarIntegrationSource == AccountIntegrationSource.Provider)
            {
                var activeCalendars = states.Where(state => ItemClass(state.EasFolderType) == "Calendar").Select(state => state.Id).ToHashSet();
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
            return downloaded;
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
            var state = await GetStateAsync(folderId).ConfigureAwait(false);
            await SynchronizeCollectionAsync(state, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            var sync = EasDataMapper.Sync;
            var command = new XElement(sync + operation,
                new XElement(sync + (operation == "Add" ? "ClientId" : "ServerId"), operation == "Add" ? clientId : remoteId));
            if (data != null)
            {
                var shadow = operation == "Add" ? null : await _database.Connection.FindAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, folderId, remoteId)).ConfigureAwait(false);
                command.Add(ItemClass(state.EasFolderType) is "Email" or "Contacts" ? data : Merge(shadow?.ApplicationData, data));
            }
            var result = await _client.SyncAsync(folderId, state.DeltaToken, ItemClass(state.EasFolderType), new XElement(sync + "Commands", command), cancellationToken).ConfigureAwait(false);
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
                    Generation = state.EasSyncGeneration }, typeof(EasItemShadow)).ConfigureAwait(false);
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

            await DeleteProjectionAsync(await GetStateAsync(folderId).ConfigureAwait(false), remoteId).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> ChangeFolderAsync(string command, string remoteId, string parentId, string displayName, int folderType, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            XNamespace folder = "FolderHierarchy:";
            var request = new XElement(folder + command, new XElement(folder + "SyncKey", _account.SynchronizationDeltaIdentifier));
            if (command != "FolderCreate")
                request.Add(new XElement(folder + "ServerId", remoteId));
            if (command != "FolderDelete")
                request.Add(new XElement(folder + "ParentId", string.IsNullOrEmpty(parentId) ? "0" : parentId), new XElement(folder + "DisplayName", displayName));
            if (command == "FolderCreate")
                request.Add(new XElement(folder + "Type", folderType));
            var response = await _client.CommandAsync(command, request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The EAS folder operation response is empty.");
            EasClient.RequireSuccess(response);
            var resultId = command == "FolderCreate" ? Required(response, folder + "ServerId") : remoteId;
            var key = EasDataMapper.StableId(EasDataMapper.Key(_account.Id, resultId, string.Empty));
            var state = await _database.Connection.FindAsync<MailItemFolder>(key).ConfigureAwait(false);
            if (command == "FolderDelete")
            {
                if (state != null)
                    await RemoveCollectionAsync(state).ConfigureAwait(false);
            }
            else
            {
                state ??= new MailItemFolder { Id = key, MailAccountId = _account.Id, RemoteFolderId = resultId, EasFolderType = folderType,
                    SpecialFolderType = SpecialFolderType.Other, DeltaToken = "0" };
                state.ParentRemoteFolderId = string.IsNullOrEmpty(parentId) ? "0" : parentId;
                state.FolderName = displayName;
                if (ItemClass(folderType) == "Email")
                    await EnsureCollectionAsync(state).ConfigureAwait(false);
                await _database.Connection.InsertOrReplaceAsync(state, typeof(MailItemFolder)).ConfigureAwait(false);
            }
            _account.SynchronizationDeltaIdentifier = Required(response, folder + "SyncKey");
            await _accounts.UpdateAccountAsync(_account).ConfigureAwait(false);
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

    private async Task SynchronizeFoldersAsync(CancellationToken cancellationToken)
    {
        if (_account.IsMailAccessGranted && await _folders.GetSpecialFolderByAccountIdAsync(_account.Id, SpecialFolderType.Draft).ConfigureAwait(false) == null)
        {
            var id = EasDataMapper.StableId(EasDataMapper.Key(_account.Id, "eas-local-drafts", string.Empty));
            await _folders.InsertFolderAsync(new MailItemFolder { Id = id, MailAccountId = _account.Id, RemoteFolderId = "eas-local-drafts",
                FolderName = Wino.Core.Domain.Translator.POP3Folder_Drafts, SpecialFolderType = SpecialFolderType.Draft,
                IsSticky = true, IsSystemFolder = true, IsSynchronizationEnabled = false }).ConfigureAwait(false);
        }
        XNamespace folder = "FolderHierarchy:";
        var key = _account.SynchronizationDeltaIdentifier ?? "0";
        if (await _database.Connection.Table<MailItemFolder>().Where(state => state.MailAccountId == _account.Id && state.EasFolderType > 0).CountAsync().ConfigureAwait(false) == 0)
            key = "0";
        XElement response;
        try { response = await _client.FolderSyncAsync(key, cancellationToken).ConfigureAwait(false); }
        catch (EasCommandException exception) when (exception.Status == "9" && key != "0")
        {
            key = "0";
            response = await _client.FolderSyncAsync(key, cancellationToken).ConfigureAwait(false);
        }
        var changes = response.Element(folder + "Changes") ?? throw new InvalidDataException("The EAS folder changes are missing.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes.Elements().Where(element =>
            element.Name == folder + "Add" || element.Name == folder + "Update" || element.Name == folder + "Delete"))
        {
            var remoteId = Required(change, folder + "ServerId");
            seen.Add(remoteId);
            var id = EasDataMapper.StableId(EasDataMapper.Key(_account.Id, remoteId, string.Empty));
            var state = await _database.Connection.FindAsync<MailItemFolder>(id).ConfigureAwait(false);
            if (change.Name == folder + "Delete")
            {
                if (state != null)
                    await RemoveCollectionAsync(state).ConfigureAwait(false);
                continue;
            }

            state ??= new MailItemFolder { Id = id, MailAccountId = _account.Id, RemoteFolderId = remoteId,
                SpecialFolderType = SpecialFolderType.Other, DeltaToken = "0" };
            state.FolderName = Required(change, folder + "DisplayName");
            state.ParentRemoteFolderId = Required(change, folder + "ParentId");
            if (!int.TryParse(Required(change, folder + "Type"), out var type))
                throw new InvalidDataException("The EAS folder type is invalid.");
            state.EasFolderType = type;
            await EnsureCollectionAsync(state).ConfigureAwait(false);
            await _database.Connection.InsertOrReplaceAsync(state, typeof(MailItemFolder)).ConfigureAwait(false);
        }

        if (key == "0")
        {
            var previousMailFolders = await _folders.GetFoldersAsync(_account.Id).ConfigureAwait(false);
            foreach (var previous in previousMailFolders.Where(item => item.EasFolderType == 0 &&
                !string.IsNullOrEmpty(item.RemoteFolderId) && item.RemoteFolderId != "eas-local-drafts"))
                await _folders.DeleteFolderAsync(_account.Id, previous.RemoteFolderId).ConfigureAwait(false);
            var states = await _database.Connection.Table<MailItemFolder>().Where(state => state.MailAccountId == _account.Id && state.EasFolderType > 0).ToListAsync().ConfigureAwait(false);
            foreach (var state in states.Where(state => !seen.Contains(state.RemoteFolderId)))
                await RemoveCollectionAsync(state).ConfigureAwait(false);
        }

        var activeStates = await _database.Connection.Table<MailItemFolder>().Where(state => state.MailAccountId == _account.Id && state.EasFolderType > 0).ToListAsync().ConfigureAwait(false);
        foreach (var state in activeStates)
            await EnsureCollectionAsync(state).ConfigureAwait(false);

        _account.SynchronizationDeltaIdentifier = Required(response, folder + "SyncKey");
        await _accounts.UpdateAccountAsync(_account).ConfigureAwait(false);
    }

    private async Task EnsureCollectionAsync(MailItemFolder state)
    {
        var id = state.Id;
        var color = string.IsNullOrWhiteSpace(_account.AccountColorHex) ? ColorPalette.GetColors()[0] : _account.AccountColorHex;
        switch (ItemClass(state.EasFolderType))
        {
            case "Email" when _account.IsMailAccessGranted && state.EasFolderType != 3:
                var folder = await _folders.GetFolderAsync(_account.Id, state.RemoteFolderId).ConfigureAwait(false);
                if (folder == null)
                {
                    var special = state.EasFolderType switch { 2 => SpecialFolderType.Inbox, 3 => SpecialFolderType.Draft,
                        4 => SpecialFolderType.Deleted, 5 => SpecialFolderType.Sent, _ => SpecialFolderType.Other };
                    state.SpecialFolderType = special;
                    state.IsSynchronizationEnabled = true;
                    state.IsSticky = special != SpecialFolderType.Other;
                    state.ShowUnreadCount = true;
                    await _folders.InsertFolderAsync(state).ConfigureAwait(false);
                }
                else
                {
                    await _folders.UpdateFolderAsync(state).ConfigureAwait(false);
                }
                break;
            case "Calendar" when _account.IsCalendarAccessGranted && _account.CalendarIntegrationSource == AccountIntegrationSource.Provider:
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(id).ConfigureAwait(false);
                if (calendar == null)
                    await _calendars.InsertAccountCalendarAsync(new AccountCalendar { Id = id, AccountId = _account.Id,
                        RemoteCalendarId = state.RemoteFolderId, Name = state.FolderName, IsPrimary = state.EasFolderType == 8,
                        BackgroundColorHex = color, MailAccount = _account, TimeZone = "UTC" }).ConfigureAwait(false);
                else
                {
                    calendar.MailAccount = _account;
                    calendar.Name = state.FolderName;
                    if (string.IsNullOrWhiteSpace(calendar.BackgroundColorHex))
                        calendar.BackgroundColorHex = color;
                    await _calendars.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
                }
                break;
            case "Contacts" when _account.IsContactAccessGranted && _account.ContactIntegrationSource == AccountIntegrationSource.Provider:
                var bookId = EasDataMapper.ProviderId(state.RemoteFolderId);
                var books = await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false);
                if (state.EasFolderType == 9 && !books.Any(book => book.SourceKind == ContactSourceKind.Exchange && book.RemoteId == bookId))
                {
                    var carried = books.Where(book => book.SourceKind == ContactSourceKind.Exchange && book.IsDefault &&
                        ExchangeTransportRekey.IsOtherTransport(book.RemoteId, bookId)).ToList();
                    if (carried.Count == 1)
                        await _contacts.RekeyAddressBookAsync(carried[0].Id, bookId, new Dictionary<Guid, string>()).ConfigureAwait(false);
                }
                await _contacts.GetOrCreateProviderAddressBookAsync(_account.Id, ContactSourceKind.Exchange, bookId, state.FolderName, state.EasFolderType == 9).ConfigureAwait(false);
                break;
            case "Tasks" when _account.IsTaskAccessGranted && _account.TaskIntegrationSource == AccountIntegrationSource.Provider:
                var listId = EasDataMapper.ProviderId(state.RemoteFolderId);
                var lists = await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false);
                var list = lists.FirstOrDefault(list => list.RemoteId == listId && list.SourceKind == TaskSourceKind.Exchange);
                if (list == null && state.EasFolderType == 7)
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
                    IsDefault = state.EasFolderType == 7, ColorHex = color };
                list.Title = state.FolderName;
                await _tasks.UpsertRemoteTaskListAsync(list).ConfigureAwait(false);
                break;
        }
    }

    private async Task SynchronizeCollectionAsync(MailItemFolder state, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var sync = EasDataMapper.Sync;
        var recovered = false;
        if (ItemClass(state.EasFolderType) == "Calendar" && state.DeltaToken != "0" && string.IsNullOrEmpty(state.EasSyncGeneration))
        {
            var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
            var duplicateClientIds = shadows.Select(shadow => EasDataMapper.ClientEventId(Parse(shadow.ApplicationData)))
                .OfType<Guid>().GroupBy(id => id).Where(group => group.Skip(1).Any()).Select(group => group.Key);
            foreach (var clientId in duplicateClientIds)
            {
                var local = await _database.Connection.FindAsync<CalendarItem>(clientId).ConfigureAwait(false);
                if (local?.CalendarId == state.Id)
                {
                    state.DeltaToken = "0";
                    break;
                }
            }
        }
        if (state.DeltaToken == "0")
        {
            state.EasSyncGeneration = Guid.NewGuid().ToString("N");
            var handshake = await _client.SyncAsync(state.RemoteFolderId, "0", ItemClass(state.EasFolderType), null, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, handshake).ConfigureAwait(false);
        }

        for (var page = 0; page < 10000; page++)
        {
            XElement response;
            try { response = await _client.SyncAsync(state.RemoteFolderId, state.DeltaToken, ItemClass(state.EasFolderType), null, cancellationToken).ConfigureAwait(false); }
            catch (EasCommandException exception) when (exception.Status == "3" && !recovered)
            {
                recovered = true;
                state.EasSyncGeneration = Guid.NewGuid().ToString("N");
                var handshake = await _client.SyncAsync(state.RemoteFolderId, "0", ItemClass(state.EasFolderType), null, cancellationToken).ConfigureAwait(false);
                await SaveKeyAsync(state, handshake).ConfigureAwait(false);
                continue;
            }
            await ApplyPageAsync(state, response, downloaded, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, response).ConfigureAwait(false);
            if (response.Element(sync + "MoreAvailable") == null)
            {
                if (!string.IsNullOrEmpty(state.EasSyncGeneration))
                {
                    var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
                    await ReconcileResetAsync(state, shadows.Where(shadow => shadow.Generation == state.EasSyncGeneration).ToList(), downloaded, cancellationToken).ConfigureAwait(false);
                    foreach (var shadow in shadows.Where(shadow => shadow.Generation != state.EasSyncGeneration))
                        await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);
                    state.EasSyncGeneration = string.Empty;
                    await _database.Connection.UpdateAsync(state, typeof(MailItemFolder)).ConfigureAwait(false);
                }
                return;
            }
        }
        throw new InvalidDataException("The EAS server exceeded the synchronization page limit.");
    }

    private async Task ReconcileResetAsync(MailItemFolder state, List<EasItemShadow> snapshot, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var itemClass = ItemClass(state.EasFolderType);
        if (itemClass is not ("Contacts" or "Tasks"))
            return;

        var folderId = EasDataMapper.ProviderId(state.RemoteFolderId);
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
            await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(Array.Empty<AccountContact>(), deleted, state.DeltaToken), false).ConfigureAwait(false);
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
                TaskDeltaLink = state.DeltaToken }).ConfigureAwait(false);
        }
    }

    private async Task ApplyPageAsync(MailItemFolder state, XElement response, List<MailCopy> downloaded, CancellationToken cancellationToken)
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

            var key = EasDataMapper.Key(_account.Id, state.RemoteFolderId, remoteId);
            var shadow = await _database.Connection.FindAsync<EasItemShadow>(key).ConfigureAwait(false);
            var incoming = command.Element(sync + "ApplicationData") ?? throw new InvalidDataException("EAS application data is missing.");
            var previous = shadow?.ApplicationData;
            if (previous != null && ItemClass(state.EasFolderType) != "Email")
            {
                var retained = Parse(previous);
                retained.Elements().Where(element => element.Name != EasDataMapper.Body + "Body" && element.Name != EasDataMapper.Contact + "Picture").Remove();
                previous = retained.ToString(SaveOptions.DisableFormatting);
            }
            var data = Merge(previous, incoming);
            // Match identities across transports only after the complete snapshot establishes unique candidates.
            if (string.IsNullOrEmpty(state.EasSyncGeneration) || ItemClass(state.EasFolderType) is not ("Contacts" or "Tasks"))
                await ProjectAsync(state, remoteId, data, downloaded, cancellationToken).ConfigureAwait(false);
            if (ItemClass(state.EasFolderType) == "Email")
                data.Element(EasDataMapper.Body + "Body")?.Remove();
            await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                CollectionId = state.Id, RemoteId = remoteId, ApplicationData = data.ToString(SaveOptions.DisableFormatting),
                Generation = state.EasSyncGeneration }, typeof(EasItemShadow)).ConfigureAwait(false);
        }
    }

    private async Task ProjectAsync(MailItemFolder state, string remoteId, XElement data, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var id = state.Id;
        switch (ItemClass(state.EasFolderType))
        {
            case "Email":
                var folder = await _folders.GetFolderAsync(id).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS mail folder is missing.");
                var existing = await _mail.GetSingleMailItemAsync(EasDataMapper.Key(_account.Id, state.RemoteFolderId, remoteId), state.RemoteFolderId).ConfigureAwait(false);
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
                    var fetched = await _client.FetchAsync(state.RemoteFolderId, remoteId, cancellationToken).ConfigureAwait(false);
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
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).First(item => item.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && item.SourceKind == ContactSourceKind.Exchange);
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
                await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(new[] { contact }, Array.Empty<string>(), state.DeltaToken), false).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).First(list => list.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && list.SourceKind == TaskSourceKind.Exchange);
                await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id,
                    Tasks = new[] { EasDataMapper.ReadTask(data, list, EasDataMapper.ProviderId(remoteId)) }, TaskDeltaLink = state.DeltaToken }).ConfigureAwait(false);
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

    private async Task DeleteProjectionAsync(MailItemFolder state, string remoteId)
    {
        var id = state.Id;
        switch (ItemClass(state.EasFolderType))
        {
            case "Email":
                await _mail.DeleteMailAsync(_account.Id, EasDataMapper.Key(_account.Id, state.RemoteFolderId, remoteId)).ConfigureAwait(false);
                break;
            case "Calendar":
                await _calendars.DeleteCalendarItemAsync(remoteId, id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && item.SourceKind == ContactSourceKind.Exchange);
                if (book != null)
                    await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(Array.Empty<AccountContact>(), new[] { EasDataMapper.ProviderId(remoteId) }, state.DeltaToken), false).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && list.SourceKind == TaskSourceKind.Exchange);
                if (list != null)
                    await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id, DeletedTaskRemoteIds = new[] { EasDataMapper.ProviderId(remoteId) }, TaskDeltaLink = state.DeltaToken }).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, state.RemoteFolderId, remoteId)).ConfigureAwait(false);
    }

    private async Task RemoveCollectionAsync(MailItemFolder state)
    {
        var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
        foreach (var shadow in shadows)
            await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);

        switch (ItemClass(state.EasFolderType))
        {
            case "Email": await _folders.DeleteFolderAsync(_account.Id, state.RemoteFolderId).ConfigureAwait(false); break;
            case "Calendar":
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(state.Id).ConfigureAwait(false);
                if (calendar != null) calendar.MailAccount = _account;
                if (calendar != null) await _calendars.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && list.SourceKind == TaskSourceKind.Exchange);
                if (list != null) await _tasks.RemoveTaskListAsync(list.Id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == EasDataMapper.ProviderId(state.RemoteFolderId) && item.SourceKind == ContactSourceKind.Exchange);
                if (book != null) await _contacts.DeleteAddressBookAsync(book.Id).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<MailItemFolder>(state.Id).ConfigureAwait(false);
    }

    private Task<MailItemFolder> GetStateAsync(string folderId) => _database.Connection.GetAsync<MailItemFolder>(EasDataMapper.StableId(EasDataMapper.Key(_account.Id, folderId, string.Empty)));

    private async Task SaveKeyAsync(MailItemFolder state, XElement response)
    {
        state.DeltaToken = Required(response, EasDataMapper.Sync + "SyncKey");
        if (state.DeltaToken == "0")
            throw new InvalidDataException("The EAS server returned an invalid synchronization key.");
        await _database.Connection.UpdateAsync(state, typeof(MailItemFolder)).ConfigureAwait(false);
    }

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
