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

namespace Wino.Core.Synchronizers.Eas;

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
        _client = client ?? new EasClient(server.IncomingServer, server.IncomingServerUsername,
            server.IncomingServerPassword ?? throw new InvalidOperationException("The EAS password is missing."), account.Id);
    }

    internal async Task<List<MailCopy>> SynchronizeAsync(string itemClass, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SynchronizeFoldersAsync(cancellationToken).ConfigureAwait(false);
            var states = await _database.Connection.Table<EasCollectionState>().Where(state => state.AccountId == _account.Id).ToListAsync().ConfigureAwait(false);
            if (itemClass == "Tasks" && !states.Any(state => ItemClass(state.FolderType) == "Tasks"))
                throw new NotSupportedException("The EAS server did not expose a Tasks collection for this account. Check task synchronization availability with your mail provider.");
            var downloaded = new List<MailCopy>();
            foreach (var state in states.Where(state => state.FolderType != 3 && ItemClass(state.FolderType) == itemClass))
            {
                if (itemClass == "Email")
                {
                    var folder = await _folders.GetFolderAsync(_account.Id, state.RemoteId).ConfigureAwait(false);
                    if (folder?.IsSynchronizationEnabled != true)
                        continue;
                }
                await SynchronizeCollectionAsync(state, downloaded, cancellationToken).ConfigureAwait(false);
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

    internal Task<string> MutateAsync(string folderId, string operation, string remoteId, XElement? data, string clientId, CancellationToken cancellationToken)
        => MutateCoreAsync(folderId, operation, remoteId, data, clientId, null, cancellationToken);

    internal Task<string> CreateCalendarEventAsync(string folderId, XElement data, string clientId,
        Func<string, Task> persistCreatedItem, CancellationToken cancellationToken)
        => MutateCoreAsync(folderId, "Add", string.Empty, data, clientId, persistCreatedItem, cancellationToken);

    private async Task<string> MutateCoreAsync(string folderId, string operation, string remoteId, XElement? data, string clientId,
        Func<string, Task>? persistCreatedItem, CancellationToken cancellationToken)
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
                command.Add(ItemClass(state.FolderType) is "Email" or "Contacts" ? data : Merge(shadow?.ApplicationData, data));
            }
            var result = await _client.SyncAsync(folderId, state.SyncKey, ItemClass(state.FolderType), new XElement(sync + "Commands", command), cancellationToken).ConfigureAwait(false);
            var response = result.Element(sync + "Responses")?.Element(sync + operation);
            if (response != null)
                EasClient.RequireSuccess(response);
            else if (operation == "Add")
                throw new InvalidDataException("The EAS addition response is missing.");
            var resultId = response?.Element(sync + "ServerId")?.Value ?? remoteId;
            if (string.IsNullOrEmpty(resultId))
                throw new InvalidDataException("The EAS mutation response has no item identifier.");

            // Commit the client identity before a server echo can be projected, while the gate is held.
            if (persistCreatedItem != null)
                await persistCreatedItem(resultId).ConfigureAwait(false);
            await ApplyPageAsync(state, result, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            if (operation == "Delete")
                await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, folderId, resultId)).ConfigureAwait(false);
            else if (data != null)
            {
                var key = EasDataMapper.Key(_account.Id, folderId, resultId);
                var shadow = await _database.Connection.FindAsync<EasItemShadow>(key).ConfigureAwait(false);
                await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                    CollectionId = state.Id, RemoteId = resultId, ApplicationData = Merge(shadow?.ApplicationData, data).ToString(SaveOptions.DisableFormatting),
                    Generation = state.ResetGeneration }, typeof(EasItemShadow)).ConfigureAwait(false);
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
            var key = EasDataMapper.Key(_account.Id, resultId, string.Empty);
            var state = await _database.Connection.FindAsync<EasCollectionState>(key).ConfigureAwait(false);
            if (command == "FolderDelete")
            {
                if (state != null)
                    await RemoveCollectionAsync(state).ConfigureAwait(false);
            }
            else
            {
                state ??= new EasCollectionState { Id = key, AccountId = _account.Id, RemoteId = resultId, FolderType = folderType };
                state.ParentId = string.IsNullOrEmpty(parentId) ? "0" : parentId;
                state.DisplayName = displayName;
                await _database.Connection.InsertOrReplaceAsync(state, typeof(EasCollectionState)).ConfigureAwait(false);
                if (ItemClass(folderType) == "Email")
                    await EnsureCollectionAsync(state).ConfigureAwait(false);
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
        if (await _database.Connection.Table<EasCollectionState>().Where(state => state.AccountId == _account.Id).CountAsync().ConfigureAwait(false) == 0)
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
            var id = EasDataMapper.Key(_account.Id, remoteId, string.Empty);
            var state = await _database.Connection.FindAsync<EasCollectionState>(id).ConfigureAwait(false);
            if (change.Name == folder + "Delete")
            {
                if (state != null)
                    await RemoveCollectionAsync(state).ConfigureAwait(false);
                continue;
            }

            state ??= new EasCollectionState { Id = id, AccountId = _account.Id, RemoteId = remoteId };
            state.DisplayName = Required(change, folder + "DisplayName");
            state.ParentId = Required(change, folder + "ParentId");
            if (!int.TryParse(Required(change, folder + "Type"), out var type))
                throw new InvalidDataException("The EAS folder type is invalid.");
            state.FolderType = type;
            await EnsureCollectionAsync(state).ConfigureAwait(false);
            await _database.Connection.InsertOrReplaceAsync(state, typeof(EasCollectionState)).ConfigureAwait(false);
        }

        if (key == "0")
        {
            var states = await _database.Connection.Table<EasCollectionState>().Where(state => state.AccountId == _account.Id).ToListAsync().ConfigureAwait(false);
            foreach (var state in states.Where(state => !seen.Contains(state.RemoteId)))
                await RemoveCollectionAsync(state).ConfigureAwait(false);
        }

        var activeStates = await _database.Connection.Table<EasCollectionState>().Where(state => state.AccountId == _account.Id).ToListAsync().ConfigureAwait(false);
        foreach (var state in activeStates)
            await EnsureCollectionAsync(state).ConfigureAwait(false);

        _account.SynchronizationDeltaIdentifier = Required(response, folder + "SyncKey");
        await _accounts.UpdateAccountAsync(_account).ConfigureAwait(false);
    }

    private async Task EnsureCollectionAsync(EasCollectionState state)
    {
        var id = EasDataMapper.StableId(state.Id);
        var color = string.IsNullOrWhiteSpace(_account.AccountColorHex) ? ColorPalette.GetColors()[0] : _account.AccountColorHex;
        switch (ItemClass(state.FolderType))
        {
            case "Email" when _account.IsMailAccessGranted && state.FolderType != 3:
                var folder = await _folders.GetFolderAsync(_account.Id, state.RemoteId).ConfigureAwait(false);
                if (folder == null)
                {
                    var special = state.FolderType switch { 2 => SpecialFolderType.Inbox, 3 => SpecialFolderType.Draft,
                        4 => SpecialFolderType.Deleted, 5 => SpecialFolderType.Sent, _ => SpecialFolderType.Other };
                    await _folders.InsertFolderAsync(new MailItemFolder { Id = id, MailAccountId = _account.Id, RemoteFolderId = state.RemoteId,
                        ParentRemoteFolderId = state.ParentId == "0" ? null : state.ParentId, FolderName = state.DisplayName,
                        SpecialFolderType = special, IsSynchronizationEnabled = state.FolderType != 3, IsSticky = special != SpecialFolderType.Other,
                        ShowUnreadCount = true }).ConfigureAwait(false);
                }
                else
                {
                    folder.FolderName = state.DisplayName; folder.ParentRemoteFolderId = state.ParentId == "0" ? null : state.ParentId;
                    await _folders.UpdateFolderAsync(folder).ConfigureAwait(false);
                }
                break;
            case "Calendar" when _account.IsCalendarAccessGranted:
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(id).ConfigureAwait(false);
                if (calendar == null)
                    await _calendars.InsertAccountCalendarAsync(new AccountCalendar { Id = id, AccountId = _account.Id,
                        RemoteCalendarId = state.RemoteId, Name = state.DisplayName, IsPrimary = state.FolderType == 8,
                        BackgroundColorHex = color, MailAccount = _account, TimeZone = "UTC" }).ConfigureAwait(false);
                else
                {
                    calendar.MailAccount = _account;
                    calendar.Name = state.DisplayName;
                    if (string.IsNullOrWhiteSpace(calendar.BackgroundColorHex))
                        calendar.BackgroundColorHex = color;
                    await _calendars.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
                }
                break;
            case "Contacts" when _account.IsContactAccessGranted:
                await _contacts.GetOrCreateProviderAddressBookAsync(_account.Id, ContactSourceKind.Eas, state.RemoteId, state.DisplayName, state.FolderType == 9).ConfigureAwait(false);
                break;
            case "Tasks" when _account.IsTaskAccessGranted:
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == state.RemoteId && list.SourceKind == TaskSourceKind.Eas);
                list ??= new AccountTaskList { Id = id, MailAccountId = _account.Id, SourceKind = TaskSourceKind.Eas, RemoteId = state.RemoteId,
                    IsDefault = state.FolderType == 7, ColorHex = color };
                list.Title = state.DisplayName;
                await _tasks.UpsertRemoteTaskListAsync(list).ConfigureAwait(false);
                break;
        }
    }

    private async Task SynchronizeCollectionAsync(EasCollectionState state, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var sync = EasDataMapper.Sync;
        var recovered = false;
        if (ItemClass(state.FolderType) == "Calendar" && state.SyncKey != "0" && string.IsNullOrEmpty(state.ResetGeneration))
        {
            var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
            var duplicateClientIds = shadows.Select(shadow => EasDataMapper.ClientEventId(Parse(shadow.ApplicationData)))
                .OfType<Guid>().GroupBy(id => id).Where(group => group.Skip(1).Any()).Select(group => group.Key);
            foreach (var clientId in duplicateClientIds)
            {
                var local = await _database.Connection.FindAsync<CalendarItem>(clientId).ConfigureAwait(false);
                if (local?.CalendarId == EasDataMapper.StableId(state.Id))
                {
                    state.SyncKey = "0";
                    break;
                }
            }
        }
        if (state.SyncKey == "0")
        {
            state.ResetGeneration = Guid.NewGuid().ToString("N");
            var handshake = await _client.SyncAsync(state.RemoteId, "0", ItemClass(state.FolderType), null, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, handshake).ConfigureAwait(false);
        }

        for (var page = 0; page < 10000; page++)
        {
            XElement response;
            try { response = await _client.SyncAsync(state.RemoteId, state.SyncKey, ItemClass(state.FolderType), null, cancellationToken).ConfigureAwait(false); }
            catch (EasCommandException exception) when (exception.Status == "3" && !recovered)
            {
                recovered = true;
                state.ResetGeneration = Guid.NewGuid().ToString("N");
                var handshake = await _client.SyncAsync(state.RemoteId, "0", ItemClass(state.FolderType), null, cancellationToken).ConfigureAwait(false);
                await SaveKeyAsync(state, handshake).ConfigureAwait(false);
                continue;
            }
            await ApplyPageAsync(state, response, downloaded, cancellationToken).ConfigureAwait(false);
            await SaveKeyAsync(state, response).ConfigureAwait(false);
            if (response.Element(sync + "MoreAvailable") == null)
            {
                if (!string.IsNullOrEmpty(state.ResetGeneration))
                {
                    var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
                    foreach (var shadow in shadows.Where(shadow => shadow.Generation != state.ResetGeneration))
                        await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);
                    state.ResetGeneration = string.Empty;
                    await _database.Connection.UpdateAsync(state, typeof(EasCollectionState)).ConfigureAwait(false);
                }
                return;
            }
        }
        throw new InvalidDataException("The EAS server exceeded the synchronization page limit.");
    }

    private async Task ApplyPageAsync(EasCollectionState state, XElement response, List<MailCopy> downloaded, CancellationToken cancellationToken)
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

            var key = EasDataMapper.Key(_account.Id, state.RemoteId, remoteId);
            var shadow = await _database.Connection.FindAsync<EasItemShadow>(key).ConfigureAwait(false);
            var incoming = command.Element(sync + "ApplicationData") ?? throw new InvalidDataException("EAS application data is missing.");
            var previous = shadow?.ApplicationData;
            if (previous != null && ItemClass(state.FolderType) != "Email")
            {
                var retained = Parse(previous);
                retained.Elements().Where(element => element.Name != EasDataMapper.Body + "Body" && element.Name != EasDataMapper.Contact + "Picture").Remove();
                previous = retained.ToString(SaveOptions.DisableFormatting);
            }
            var data = Merge(previous, incoming);
            await ProjectAsync(state, remoteId, data, downloaded, cancellationToken).ConfigureAwait(false);
            if (ItemClass(state.FolderType) == "Email")
                data.Element(EasDataMapper.Body + "Body")?.Remove();
            await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                CollectionId = state.Id, RemoteId = remoteId, ApplicationData = data.ToString(SaveOptions.DisableFormatting),
                Generation = state.ResetGeneration }, typeof(EasItemShadow)).ConfigureAwait(false);
        }
    }

    private async Task ProjectAsync(EasCollectionState state, string remoteId, XElement data, List<MailCopy> downloaded, CancellationToken cancellationToken)
    {
        var id = EasDataMapper.StableId(state.Id);
        switch (ItemClass(state.FolderType))
        {
            case "Email":
                var folder = await _folders.GetFolderAsync(id).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS mail folder is missing.");
                var existing = await _mail.GetSingleMailItemAsync(EasDataMapper.Key(_account.Id, state.RemoteId, remoteId), state.RemoteId).ConfigureAwait(false);
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
                    var fetched = await _client.FetchAsync(state.RemoteId, remoteId, cancellationToken).ConfigureAwait(false);
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
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).First(item => item.RemoteId == state.RemoteId && item.SourceKind == ContactSourceKind.Eas);
                var contact = EasDataMapper.ReadContact(data, _account.Id, book.Id, remoteId);
                if (data.Element(EasDataMapper.Contact + "Picture") is XElement photo)
                {
                    var current = await _database.Connection.Table<AccountContact>().FirstOrDefaultAsync(contact => contact.AddressBookId == book.Id && contact.RemoteId == remoteId).ConfigureAwait(false);
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
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).First(list => list.RemoteId == state.RemoteId && list.SourceKind == TaskSourceKind.Eas);
                await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id,
                    Tasks = new[] { EasDataMapper.ReadTask(data, list, remoteId) }, TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
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

    private async Task DeleteProjectionAsync(EasCollectionState state, string remoteId)
    {
        var id = EasDataMapper.StableId(state.Id);
        switch (ItemClass(state.FolderType))
        {
            case "Email":
                await _mail.DeleteMailAsync(_account.Id, EasDataMapper.Key(_account.Id, state.RemoteId, remoteId)).ConfigureAwait(false);
                break;
            case "Calendar":
                await _calendars.DeleteCalendarItemAsync(remoteId, id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == state.RemoteId && item.SourceKind == ContactSourceKind.Eas);
                if (book != null)
                    await _contacts.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch(Array.Empty<AccountContact>(), new[] { remoteId }, state.SyncKey), false).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == state.RemoteId && list.SourceKind == TaskSourceKind.Eas);
                if (list != null)
                    await _tasks.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta { TaskListId = list.Id, DeletedTaskRemoteIds = new[] { remoteId }, TaskDeltaLink = state.SyncKey }).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, state.RemoteId, remoteId)).ConfigureAwait(false);
    }

    private async Task RemoveCollectionAsync(EasCollectionState state)
    {
        var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
        foreach (var shadow in shadows)
            await DeleteProjectionAsync(state, shadow.RemoteId).ConfigureAwait(false);

        switch (ItemClass(state.FolderType))
        {
            case "Email": await _folders.DeleteFolderAsync(_account.Id, state.RemoteId).ConfigureAwait(false); break;
            case "Calendar":
                var calendar = await _database.Connection.FindAsync<AccountCalendar>(EasDataMapper.StableId(state.Id)).ConfigureAwait(false);
                if (calendar != null) calendar.MailAccount = _account;
                if (calendar != null) await _calendars.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
                break;
            case "Tasks":
                var list = (await _tasks.GetTaskListsAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(list => list.RemoteId == state.RemoteId && list.SourceKind == TaskSourceKind.Eas);
                if (list != null) await _tasks.RemoveTaskListAsync(list.Id).ConfigureAwait(false);
                break;
            case "Contacts":
                var book = (await _contacts.GetAddressBooksAsync(_account.Id).ConfigureAwait(false)).FirstOrDefault(item => item.RemoteId == state.RemoteId && item.SourceKind == ContactSourceKind.Eas);
                if (book != null) await _contacts.DeleteAddressBookAsync(book.Id).ConfigureAwait(false);
                break;
        }
        await _database.Connection.DeleteAsync<EasCollectionState>(state.Id).ConfigureAwait(false);
    }

    private Task<EasCollectionState> GetStateAsync(string folderId) => _database.Connection.GetAsync<EasCollectionState>(EasDataMapper.Key(_account.Id, folderId, string.Empty));

    private async Task SaveKeyAsync(EasCollectionState state, XElement response)
    {
        state.SyncKey = Required(response, EasDataMapper.Sync + "SyncKey");
        if (state.SyncKey == "0")
            throw new InvalidDataException("The EAS server returned an invalid synchronization key.");
        await _database.Connection.UpdateAsync(state, typeof(EasCollectionState)).ConfigureAwait(false);
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
