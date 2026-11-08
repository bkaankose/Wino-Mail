using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Rops;
using Wino.Core.MAPI.Transport;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Services;
using Wino.Core.Synchronizers.Exchange;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Synchronizes on-premises Exchange accounts over native MAPI over HTTP. It derives from the EWS synchronizer and replaces
/// every surface: folder hierarchy, incremental message sync over ICS, message bodies with attachments,
/// read/flag/move/delete, drafts with native send, folder operations, calendars, contacts and tasks. A server found not to advertise MAPI over
/// HTTP is recorded on the account, and the next synchronizer built for it is the EWS one.
///
/// Identity: a mail row's id is the MAPI message id, "mapi:" plus 16 hex digits. It is unique within the mailbox
/// database and survives moves between folders, which the (Id, FolderId) upsert relies on. Folder rows are keyed the
/// same way, with the bare hex id kept in MailItemFolder.MapiFolderId.
///
/// Sync: ICS per folder, with its state in MailItemFolder.DeltaToken. The first pass yields everything, later ones
/// only changes, deletions and read-state changes. A folder whose ICS pass fails falls back to a full read of its
/// newest rows for that pass.
/// </summary>
public sealed partial class MapiExchangeSynchronizer : ExchangeSynchronizer
{
    private static new readonly ILogger Logger = Log.ForContext<MapiExchangeSynchronizer>();

    private const string MapiIdPrefix = "mapi:";
    private const string UserAgent = "WinoMail/MAPI";

    private MapiEndpointInfo? _endpoint;

    public MapiExchangeSynchronizer(MailAccount account,
                                    IExchangeAuthenticator exchangeAuthenticator,
                                    IExchangeChangeProcessor exchangeChangeProcessor,
                                    IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
                                    IContactService? contactService = null,
                                    IPictureStorageService? contactPictureService = null,
                                    ITaskService? taskService = null)
        : base(account, exchangeAuthenticator, exchangeChangeProcessor, errorHandlerFactory, contactService, contactPictureService, taskService)
    {
    }

    #region Identity

    public static string ToMapiId(ulong id) => MapiIdPrefix + id.ToString("X16", CultureInfo.InvariantCulture);

    public static bool TryParseMapiId(string? id, out ulong value)
    {
        value = 0;
        return id is not null
            && id.StartsWith(MapiIdPrefix, StringComparison.Ordinal)
            && ulong.TryParse(id.AsSpan(MapiIdPrefix.Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseFolderId(MailItemFolder? folder, out ulong folderId)
    {
        folderId = 0;
        return folder?.MapiFolderId is { Length: 16 } hex
            && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out folderId);
    }

    private static ulong RequireFolderId(MailItemFolder? folder)
        => TryParseFolderId(folder, out var id)
            ? id
            : throw new InvalidOperationException($"Folder '{folder?.FolderName}' has no MAPI folder id yet; a folder sync must run first.");

    private static ulong RequireMessageId(MailCopy item)
        => TryParseMapiId(item.Id, out var id)
            ? id
            : throw new InvalidOperationException($"Mail '{item.Subject}' was synchronized over another transport; it is not addressable over MAPI until the next sync.");

    #endregion

    #region Session

    // Connect and Logon cost two round trips, plus Autodiscover on first use. One session is kept per synchronizer
    // behind a gate and reopened when it ages, idles or faults. A caller that finds the gate held (a sync pass in
    // flight) opens a temporary session instead of waiting, so user actions stay responsive.

    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionMaxIdle = TimeSpan.FromMinutes(4);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    // The ambient borrow is a holder set before the first await of the acquire, in a non-async method: a value set
    // inside an async method never flows back to its caller. The holder's field is filled once the session is known
    // and cleared when the lease ends.
    private sealed class AmbientHolder { public MapiSession? Session; }
    private static readonly AsyncLocal<AmbientHolder?> AmbientSession = new();
    private MapiSession? _sharedSession;
    private DateTime _sharedOpenedUtc;
    private DateTime _sharedUsedUtc;

    private enum LeaseKind { Shared, Temporary, Nested }

    /// <summary>
    /// A borrowed session. Disposing returns the shared one to the gate (dropping it when it faulted), closes a
    /// temporary one, or does nothing for a nested borrow of the ambient one.
    /// </summary>
    private sealed class SessionLease(MapiSession session, MapiExchangeSynchronizer owner, LeaseKind kind, AmbientHolder? holder) : IAsyncDisposable
    {
        public MapiSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            switch (kind)
            {
                case LeaseKind.Nested:
                    return;
                case LeaseKind.Temporary:
                    if (holder is not null) holder.Session = null;
                    await Session.DisposeAsync().ConfigureAwait(false);
                    return;
            }

            try
            {
                if (holder is not null) holder.Session = null;
                owner._sharedUsedUtc = DateTime.UtcNow;
                if (Session.Faulted)
                    await owner.DropSharedSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                owner._sessionGate.Release();
            }
        }
    }

    private Task<SessionLease> AcquireSessionAsync(CancellationToken cancellationToken)
    {
        // Already inside a borrow on this logical flow, such as a body download during a sync pass.
        if (AmbientSession.Value?.Session is { Faulted: false } ambient)
            return Task.FromResult(new SessionLease(ambient, this, LeaseKind.Nested, null));

        var holder = new AmbientHolder();
        AmbientSession.Value = holder;
        return AcquireSessionCoreAsync(holder, cancellationToken);
    }

    private async Task<SessionLease> AcquireSessionCoreAsync(AmbientHolder holder, CancellationToken cancellationToken)
    {
        if (!await _sessionGate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false))
        {
            var temporary = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            holder.Session = temporary;
            return new SessionLease(temporary, this, LeaseKind.Temporary, holder);
        }

        try
        {
            var now = DateTime.UtcNow;
            if (_sharedSession is not null && (_sharedSession.Faulted || now - _sharedOpenedUtc > SessionMaxAge || now - _sharedUsedUtc > SessionMaxIdle))
                await DropSharedSessionAsync().ConfigureAwait(false);

            if (_sharedSession is null)
            {
                _sharedSession = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
                _sharedOpenedUtc = now;
            }

            _sharedUsedUtc = now;
            holder.Session = _sharedSession;
            return new SessionLease(_sharedSession, this, LeaseKind.Shared, holder);
        }
        catch
        {
            _sessionGate.Release();
            throw;
        }
    }

    /// <summary>Closes the shared session; the next acquire opens a new one. Callers hold the gate.</summary>
    private async Task DropSharedSessionAsync()
    {
        var session = _sharedSession;
        _sharedSession = null;
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<MapiSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        // A kept session can outlive a bearer token; the transport re-mints it through the authenticator on a 401.
        var credential = await MapiConnectionProbe.ToMapiCredentialAsync(ExchangeAuthenticator, Account, cancellationToken).ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        var transport = new MapiHttpTransport(endpoint.MailStoreUrl, credential, UserAgent, Diagnostics);
        return await MapiSession.OpenAsync(transport, endpoint.LegacyDn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// POX Autodiscover once per synchronizer instance; the endpoint and DN do not change under a session. A server
    /// that answers without MAPI over HTTP is recorded on an Automatic account as an EWS server.
    /// </summary>
    private async Task<MapiEndpointInfo> ResolveEndpointAsync(MapiCredential credential, CancellationToken cancellationToken)
    {
        if (_endpoint is not null)
            return _endpoint;

        var server = Account.ServerInformation
            ?? throw new InvalidOperationException("The Exchange account has no server information.");

        if (!Uri.TryCreate(server.IncomingServer, UriKind.Absolute, out var ewsUri))
            throw new InvalidOperationException("The Exchange account has no valid server URL.");

        MapiEndpointInfo endpoint;
        try
        {
            endpoint = await MapiAutodiscover.DiscoverAsync(MapiConnectionProbe.AutodiscoverUrl(ewsUri), Account.Address, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (MapiNotAdvertisedException ex) when (server.ExchangeTransport == ExchangeTransport.Automatic)
        {
            Logger.Warning("MAPI {Account}: {Reason} Falling back to Exchange Web Services.", Account.Address, ex.Message);

            // Only a change of the recorded transport asks for another pass: a synchronizer that is still MAPI-based
            // would otherwise request one on every failure.
            if (server.DetectedExchangeTransport != ExchangeTransport.Ews)
            {
                server.DetectedExchangeTransport = ExchangeTransport.Ews;
                await ExchangeChangeProcessor.UpdateAccountServerInformationAsync(server).ConfigureAwait(false);
                Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = Account.Id,
                    Type = MailSynchronizationType.FullFolders
                }));
            }

            throw;
        }

        if (server.DetectedExchangeTransport != ExchangeTransport.MapiHttp)
        {
            server.DetectedExchangeTransport = ExchangeTransport.MapiHttp;
            await ExchangeChangeProcessor.UpdateAccountServerInformationAsync(server).ConfigureAwait(false);
        }

        _endpoint = endpoint;
        return endpoint;
    }

    private void Diagnostics(string line) => Logger.Debug("MAPI {Account} {Line}", Account.Address, line);

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync().ConfigureAwait(false);

        // The kept session holds an HTTP connection and a server-side session context; release both.
        if (await _sessionGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            try
            {
                await DropSharedSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionGate.Release();
            }
        }
    }

    #endregion

    #region Mail sync

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            await SynchronizeMapiFoldersAsync(cancellationToken).ConfigureAwait(false);

        if (options.Type == MailSynchronizationType.FoldersOnly)
            return MailSynchronizationResult.Empty;

        var foldersToSync = (await ExchangeChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
            .Where(f => TryParseFolderId(f, out _))
            .ToList();

        var downloaded = new List<MailCopy>();

        if (foldersToSync.Count == 0)
            return MailSynchronizationResult.Completed(downloaded);

        var remaining = new Queue<MailItemFolder>(foldersToSync);

        // The kept session can expire on the server between passes (idle timeout, app pool recycle). The first
        // ContextNotFound drops it (the lease sees it faulted) and the pass continues once on a new session.
        for (var attempt = 0; attempt < 2 && remaining.Count > 0; attempt++)
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            var sessionLost = false;

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = remaining.Peek();
                try
                {
                    downloaded.AddRange(await SynchronizeFolderMessagesAsync(session, folder, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MapiTransportException ex) when (ex.IsContextNotFound && attempt == 0)
                {
                    Logger.Information("MAPI session of {Account} no longer exists on the server; reopening it for the rest of this pass.", Account.Address);
                    sessionLost = true;
                    break;
                }
                catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                {
                    // The folder was deleted on the server. Dropping it locally stops it failing every sync.
                    Logger.Warning("MAPI folder {Folder} ({FolderId}) no longer exists on the server; removing it locally.", folder.FolderName, folder.MapiFolderId);
                    await ExchangeChangeProcessor.DeleteFolderAsync(Account.Id, folder.RemoteFolderId).ConfigureAwait(false);
                    Messenger.Send(new AccountFolderConfigurationUpdated(Account.Id));
                }
                catch (Exception ex)
                {
                    var errorContext = new SynchronizerErrorContext
                    {
                        Account = Account,
                        ErrorMessage = ex.Message,
                        Exception = ex,
                        FolderId = folder.Id,
                        FolderName = folder.FolderName,
                        OperationType = "MapiFolderSync"
                    };

                    await ErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                    CaptureSynchronizationIssue(errorContext);
                    Logger.Error(ex, "MAPI folder sync failed for {Folder} ({Account}).", folder.FolderName, Account.Name);
                }

                remaining.Dequeue();
            }

            if (!sessionLost)
                break;
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    /// <summary>
    /// One folder over ICS: the server reports what changed since the state handed to it, and the first sync of a
    /// folder yields everything. If ICS fails for any reason the folder's state is cleared and the folder falls back
    /// to a full read for this pass.
    /// </summary>
    private async Task<List<MailCopy>> SynchronizeFolderMessagesAsync(MapiSession session, MailItemFolder folder, CancellationToken cancellationToken)
    {
        try
        {
            return await SynchronizeFolderMessagesIncrementalAsync(session, folder, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is MapiException or InvalidOperationException && ex is not MapiTransportException)
        {
            // A transport failure says nothing about the ICS state; resetting it would turn a dropped session into a
            // full re-read of every folder.
            Logger.Warning(ex, "MAPI ICS sync failed for {Account}/{Folder}; falling back to a full read this pass and resetting the folder's state.", Account.Address, folder.FolderName);
            folder.DeltaToken = null;
            await ExchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
            return await SynchronizeFolderMessagesFullReadAsync(session, folder, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<MailCopy>> SynchronizeFolderMessagesIncrementalAsync(MapiSession session, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var folderId = RequireFolderId(folder);
        var state = IcsState.Deserialize(folder.DeltaToken);

        var sync = await MapiIcsOperations.SyncContentsAsync(session, folderId, state, PropertyTags.MessageListColumns, cancellationToken, Diagnostics).ConfigureAwait(false);

        if (sync.NewState.IsEmpty)
            throw new InvalidOperationException("ICS returned no state; the transfer state stream was not understood.");

        var local = await ExchangeChangeProcessor.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
        var localById = IndexById(local);

        var stateUpdates = new List<MailCopyStateUpdate>();
        var downloaded = await UpsertRemoteMessagesAsync(sync.Changes, folder, localById, stateUpdates, cancellationToken).ConfigureAwait(false);

        if (stateUpdates.Count > 0)
            await ExchangeChangeProcessor.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);

        // Read-state echoes are applied even when the local row already carries the value from the optimistic UI:
        // the change notification they raise is what tells the list that the server confirmed it.
        foreach (var (messageId, isRead) in sync.ReadStateChanges)
        {
            var id = ToMapiId(messageId);
            if (localById.ContainsKey(id))
                await ExchangeChangeProcessor.ChangeMailReadStatusAsync(id, isRead).ConfigureAwait(false);
        }

        var deleted = sync.DeletedMessageIds
            .Select(ToMapiId)
            .Where(localById.ContainsKey)
            .Distinct()
            .ToList();

        if (deleted.Count > 0)
            await ExchangeChangeProcessor.DeleteMailsAsync(Account.Id, deleted).ConfigureAwait(false);

        folder.DeltaToken = sync.NewState.Serialize();
        await ExchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        await ExchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        Logger.Information("MAPI ICS sync {Account}/{Folder}: {Mode}, {Changes} changes ({New} new), {StateChanges} state changes, {ReadChanges} read echoes, {Deleted} removed.",
            Account.Address, folder.FolderName, sync.IsInitial ? "initial" : "incremental", sync.Changes.Count, downloaded.Count, stateUpdates.Count, sync.ReadStateChanges.Count, deleted.Count);

        return downloaded;
    }

    /// <summary>
    /// The newest rows of the folder: inserts what is new, applies read and flag changes to what exists, and deletes
    /// local rows the server no longer has within the window that was read.
    /// </summary>
    private async Task<List<MailCopy>> SynchronizeFolderMessagesFullReadAsync(MapiSession session, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var folderId = RequireFolderId(folder);
        var limit = (int)InitialMessageDownloadCountPerFolder;

        var remote = await MapiMessageOperations.ReadMessageListAsync(session, folderId, limit, cancellationToken, Diagnostics).ConfigureAwait(false);
        var remoteIds = remote.Select(m => ToMapiId(m.MessageId)).ToHashSet(StringComparer.Ordinal);

        var local = await ExchangeChangeProcessor.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
        var localById = IndexById(local);

        var stateUpdates = new List<MailCopyStateUpdate>();
        var downloaded = await UpsertRemoteMessagesAsync(remote, folder, localById, stateUpdates, cancellationToken).ConfigureAwait(false);

        if (stateUpdates.Count > 0)
            await ExchangeChangeProcessor.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);

        // When the list was capped, only local rows inside the window that was read can be judged.
        var windowStart = remote.Count >= limit ? remote.Min(m => m.ReceivedTime ?? DateTime.MinValue) : DateTime.MinValue;

        // A draft row that is not MAPI-addressed yet (local, or saved and not mapped) must survive: deleting it
        // closes the compose window bound to it.
        var stale = local
            .Where(m => m.Id is not null && !remoteIds.Contains(m.Id) && !m.IsLocalDraft && m.CreationDate >= windowStart)
            .Where(m => !(m.IsDraft && !TryParseMapiId(m.Id, out _)))
            .Select(m => m.Id)
            .Distinct()
            .ToList();

        if (stale.Count > 0)
            await ExchangeChangeProcessor.DeleteMailsAsync(Account.Id, stale).ConfigureAwait(false);

        await ExchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        Logger.Information("MAPI mail sync {Account}/{Folder}: {Remote} on server (window {Limit}), {New} new, {Updated} state changes, {Deleted} removed.",
            Account.Address, folder.FolderName, remote.Count, limit, downloaded.Count, stateUpdates.Count, stale.Count);

        return downloaded;
    }

    private static Dictionary<string, MailCopy> IndexById(List<MailCopy> local)
        => local.Where(m => m.Id is not null).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// Folds server rows into the folder. A row that already exists locally only contributes a read/flag state
    /// update: creating it again would mint a new FileId and orphan the cached body. Anything else is new here, or
    /// moved here from another folder (the upsert removes the other row by id), and is inserted.
    /// </summary>
    private async Task<List<MailCopy>> UpsertRemoteMessagesAsync(IEnumerable<MapiMessageInfo> messages,
                                                                 MailItemFolder folder,
                                                                 IReadOnlyDictionary<string, MailCopy> localById,
                                                                 List<MailCopyStateUpdate> stateUpdates,
                                                                 CancellationToken cancellationToken)
    {
        var downloaded = new List<MailCopy>();

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = ToMapiId(message.MessageId);

            if (localById.TryGetValue(id, out var existing))
            {
                var readChanged = existing.IsRead != message.IsRead;
                var flagChanged = existing.IsFlagged != message.IsFlagged;
                if (readChanged || flagChanged)
                    stateUpdates.Add(new MailCopyStateUpdate(id, readChanged ? message.IsRead : null, flagChanged ? message.IsFlagged : null));

                continue;
            }

            var package = new NewMailItemPackage(MapToMailCopy(message, folder), null, folder.RemoteFolderId);
            if (await ExchangeChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false))
                downloaded.Add(package.Copy);
        }

        return downloaded;
    }

    private static MailCopy MapToMailCopy(MapiMessageInfo message, MailItemFolder folder)
    {
        var received = message.ReceivedTime ?? message.SentTime ?? message.LastModified ?? DateTime.UtcNow;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = ToMapiId(message.MessageId),
            FolderId = folder.Id,
            ThreadId = message.ConversationId is { Length: > 0 } conversation ? Convert.ToHexString(conversation) : null,
            MessageId = message.InternetMessageId,
            InReplyTo = message.InReplyTo,
            References = message.References,
            Subject = message.Subject,
            PreviewText = message.Preview,
            FromName = message.FromName,
            FromAddress = message.FromAddress,
            CreationDate = DateTime.SpecifyKind(received, DateTimeKind.Utc),
            IsRead = message.IsRead,
            IsFlagged = message.IsFlagged,
            HasAttachments = message.HasAttachments,
            Importance = message.Importance switch { 0 => MailImportance.Low, 2 => MailImportance.High, _ => MailImportance.Normal },
            // Unsent marks a draft only on mail: appointments and other classes carry the flag too, and a deleted
            // event must not show up as a draft in Deleted Items.
            IsDraft = folder.SpecialFolderType == SpecialFolderType.Draft
                      || (message.IsUnsent && (message.MessageClass ?? "IPM.Note").StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase)),
            ItemType = MapiCalendarOperations.MeetingMethod(message.MessageClass) switch
            {
                "REQUEST" => MailItemType.CalendarInvitation,
                "CANCEL" => MailItemType.CalendarCancellation,
                "REPLY" => MailItemType.CalendarResponse,
                _ => MailItemType.Mail
            },
        };
    }

    #endregion

    #region Message body

    private MapiCalendarTags? _calendarTags;

    private async Task<MapiCalendarTags> ResolveCalendarTagsAsync(MapiSession session, CancellationToken cancellationToken)
        => _calendarTags ??= await MapiCalendarOperations.ResolveTagsAsync(session, cancellationToken, Diagnostics).ConfigureAwait(false);

    /// <summary>
    /// There is no MIME on the MAPI wire: bodies, headers and attachments are read as properties and assembled into
    /// the message the reader expects.
    /// </summary>
    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress? transferProgress = null, CancellationToken cancellationToken = default)
    {
        var messageId = RequireMessageId(mailItem);

        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var folder = folders.FirstOrDefault(f => f.Id == mailItem.FolderId)
            ?? throw new SynchronizerEntityNotFoundException($"Folder {mailItem.FolderId} of mail '{mailItem.Subject}' is not known locally.");
        var folderId = RequireFolderId(folder);

        MapiMessageContent content;
        string? displayTo = null;
        string? calendarPart = null;
        var method = mailItem.ItemType switch
        {
            MailItemType.CalendarInvitation => "REQUEST",
            MailItemType.CalendarCancellation => "CANCEL",
            MailItemType.CalendarResponse => "REPLY",
            _ => null
        };

        await using (var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            var session = lease.Session;
            try
            {
                content = await MapiMessageOperations.ReadMessageContentAsync(session, folderId, messageId, cancellationToken, Diagnostics).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                throw new SynchronizerEntityNotFoundException($"Mail '{mailItem.Subject}' no longer exists on the server.");
            }

            // Drafts and sent items carry no transport headers, so the To line comes from PidTagDisplayTo.
            if (string.IsNullOrWhiteSpace(content.TransportHeaders))
            {
                try
                {
                    var info = await MapiMessageOperations.ReadMessageInfoAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
                    displayTo = info.DisplayTo;
                }
                catch (MapiException ex)
                {
                    Logger.Debug(ex, "MAPI {Account}: recipients of 0x{Id:X16} not read; the message is shown without a To line.", Account.Address, messageId);
                }
            }

            // A meeting message carries its schedule in named properties, not MIME; the text/calendar part the
            // reader's invitation handling expects is built from them.
            if (method is not null)
            {
                try
                {
                    var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
                    var meeting = await MapiCalendarOperations.ReadMeetingIdentityAsync(session, folderId, messageId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
                    calendarPart = MapiCalendarOperations.BuildICalendar(meeting, method, DateTime.UtcNow);

                    if (method == "REQUEST" && (meeting.CleanGlobalObjectId ?? meeting.GlobalObjectId) is { Length: > 0 } goid)
                        await TryMapCalendarInvitationAsync(session, tags, mailItem, Convert.ToHexString(goid), cancellationToken).ConfigureAwait(false);
                }
                catch (MapiException ex)
                {
                    Logger.Warning(ex, "MAPI {Account}: meeting details of 0x{Id:X16} not read; the message is shown without its calendar part.", Account.Address, messageId);
                }
            }
        }

        var mime = MapiMimeAssembler.Build(mailItem, content, displayTo, calendarPart, method);
        await ExchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mime, Account.Id).ConfigureAwait(false);
    }

    #endregion

    #region Mail operations

    // Each operation runs on a borrowed session inside the bundle the base class executes.
    private List<IRequestBundle<EwsRequest>> MapiBundle(Func<MapiSession, CancellationToken, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => CreateBundle(async (_, cancellationToken) =>
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            await action(lease.Session, cancellationToken).ConfigureAwait(false);
        }, request, uiChangeRequest);

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var byFolder = requests.GroupBy(r => r.Item.FolderId).ToList();

        return MapiBundle(async (session, cancellationToken) =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var group in byFolder)
            {
                var folderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == group.Key));
                var ids = group.Select(r => RequireMessageId(r.Item)).ToList();
                await MapiMessageOperations.SetReadAsync(session, folderId, ids, isRead, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return MapiBundle(async (session, cancellationToken) =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var request in requests)
            {
                var folderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == request.Item.FolderId));
                await MapiMessageOperations.SetFlaggedAsync(session, folderId, RequireMessageId(request.Item), request.IsFlagged, cancellationToken).ConfigureAwait(false);
            }

            // The list row reads the flag from the local row; it is stored once the server has it.
            foreach (var request in requests)
                await ExchangeChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destinationFolder = requests[0].ToFolder;
        var bySource = requests.GroupBy(r => r.FromFolder?.Id ?? r.Item.FolderId).ToList();

        return MapiBundle(async (session, cancellationToken) =>
        {
            var destination = RequireFolderId(destinationFolder);
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var group in bySource)
            {
                var source = RequireFolderId(folders.FirstOrDefault(f => f.Id == group.Key));
                var ids = group.Select(r => RequireMessageId(r.Item)).ToList();
                var complete = await MapiMessageOperations.MoveAsync(session, source, destination, ids, copy: false, cancellationToken).ConfigureAwait(false);
                if (!complete)
                    Logger.Warning("MAPI move: not every message moved from {Source} to {Destination}.", group.Key, destinationFolder?.FolderName);
            }
        }, requests[0], requests);
    }

    // A plain move to or from the junk folder. The server's blocked senders list is not edited.
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return Move(new BatchMoveRequest(requests.Select(r => new MoveRequest(r.Item, r.FromFolder, r.TargetFolder))));
    }

    /// <summary>Soft delete: a move to Deleted Items. Items already there, and drafts, are deleted for good.</summary>
    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var byFolder = requests.GroupBy(r => r.MailItem.FolderId).ToList();

        return MapiBundle(async (session, cancellationToken) =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var deletedItemsId = RequireFolderId(folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted));

            foreach (var group in byFolder)
            {
                var sourceFolder = folders.FirstOrDefault(f => f.Id == group.Key);
                var source = RequireFolderId(sourceFolder);
                var sourceIsDrafts = sourceFolder?.SpecialFolderType == SpecialFolderType.Draft;

                // A draft moved to Deleted Items would come back on the next sync as an empty draft row there.
                var drafts = new List<ulong>();
                var mails = new List<ulong>();
                foreach (var request in group)
                {
                    var messageId = RequireMessageId(request.MailItem);
                    if (sourceIsDrafts || request.MailItem.IsDraft)
                        drafts.Add(messageId);
                    else
                        mails.Add(messageId);
                }

                if (drafts.Count > 0)
                    await MapiMessageOperations.DeleteAsync(session, source, drafts, cancellationToken).ConfigureAwait(false);

                if (mails.Count == 0)
                    continue;

                if (source == deletedItemsId)
                    await MapiMessageOperations.DeleteAsync(session, source, mails, cancellationToken).ConfigureAwait(false);
                else
                    await MapiMessageOperations.MoveAsync(session, source, deletedItemsId, mails, copy: false, cancellationToken).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    #endregion

    #region Drafts and send

    // The compose window's MIME is lifted into properties and written natively; sending submits the message with the
    // Sent Items folder set.

    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var localDraft = preparation.CreatedLocalDraftCopy;
        var draftsFolder = localDraft.AssignedFolder;

        return MapiBundle(async (session, cancellationToken) =>
        {
            var draftsFolderId = RequireFolderId(draftsFolder);
            var outgoing = MapiOutgoingMessageMapper.FromMime(preparation.CreatedLocalDraftMimeMessage);

            var messageId = await MapiMessageComposer.CreateAsync(session, draftsFolderId, outgoing, cancellationToken, Diagnostics, interactive: true).ConfigureAwait(false);
            var id = ToMapiId(messageId);

            var isMapped = await ExchangeChangeProcessor.MapLocalDraftAsync(Account.Id, localDraft.UniqueId, id, id, localDraft.ThreadId).ConfigureAwait(false);

            if (!isMapped)
            {
                // The local draft was discarded while the create was in flight. The server copy goes at once so the
                // next Drafts sync cannot bring it back.
                await MapiMessageOperations.DeleteAsync(session, draftsFolderId, [messageId], cancellationToken).ConfigureAwait(false);
            }
        }, request, request);
    }

    /// <summary>
    /// Creates the message in the Outbox and submits it; the transport files the copy in Sent Items and removes the
    /// original. On-premises Exchange sends as the mailbox. Once the message is submitted, removing the draft is best
    /// effort: reporting a failure after that point would bring the draft back and invite a second send.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;
        var draft = preparation.MailItem;

        return MapiBundle(async (session, cancellationToken) =>
        {
            var logon = session.Logon
                ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var sentFolder = preparation.SentFolder ?? folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Sent);
            var sentFolderId = TryParseFolderId(sentFolder, out var sentId) ? sentId : logon.SentItemsFolderId;

            var outgoing = MapiOutgoingMessageMapper.FromMime(MailKitSmtpTransport.CreateSmtpMessage(preparation.Mime));
            await MapiMessageComposer.SendAsync(session, logon.OutboxFolderId, sentFolderId, outgoing, cancellationToken, Diagnostics, interactive: true).ConfigureAwait(false);

            if (!draft.IsLocalDraft && TryParseMapiId(draft.Id, out var draftMessageId))
            {
                try
                {
                    if (TryParseFolderId(folders.FirstOrDefault(f => f.Id == draft.FolderId), out var draftsFolderId))
                        await MapiMessageOperations.DeleteAsync(session, draftsFolderId, [draftMessageId], cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Debug(ex, "The MAPI draft {DraftId} was not deleted after sending; it may already be gone.", draft.Id);
                }
            }

            try
            {
                await ExchangeChangeProcessor.DeleteMailAsync(Account.Id, draft.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Message {MailId} was sent, but deleting its local draft failed for account {AccountId}.", draft.Id, Account.Id);
            }
        }, request, request);
    }

    /// <summary>
    /// A later save of a server draft: the new MIME is written as a new message beside the old one, the old one is
    /// deleted for good, and the new id is handed back for the local row to follow.
    /// </summary>
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default)
    {
        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var draftsFolderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == draft.FolderId));
        ulong? previousMessageId = TryParseMapiId(draft.Id, out var parsed) ? parsed : null;

        using var mime = snapshot.OpenMime();
        var outgoing = MapiOutgoingMessageMapper.FromMime(mime);

        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var messageId = await MapiMessageComposer.CreateAsync(session, draftsFolderId, outgoing, cancellationToken, Diagnostics).ConfigureAwait(false);

        if (previousMessageId is { } previous)
        {
            try
            {
                await MapiMessageOperations.DeleteAsync(session, draftsFolderId, [previous], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Debug(ex, "The superseded MAPI draft {DraftId} was not deleted.", draft.Id);
            }
        }

        var id = ToMapiId(messageId);
        return new DraftUpdateIdentity(id, id, draft.ThreadId);
    }

    #endregion

    #region Folders

    // The FoldersOnly sync the request delegator queues after each folder operation reconciles the local tree.

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        return MapiBundle(async (session, cancellationToken) =>
        {
            var logon = session.Logon
                ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

            await MapiFolderOperations.CreateFolderAsync(session, logon.IpmSubtreeFolderId, name.Trim(), cancellationToken).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var name = request.NewFolderName;
        var parent = request.Folder;
        if (string.IsNullOrWhiteSpace(name) || parent is null)
            return [];

        return MapiBundle(async (session, cancellationToken) =>
        {
            await MapiFolderOperations.CreateFolderAsync(session, RequireFolderId(parent), name.Trim(), cancellationToken).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request)
    {
        var folder = request.Folder;
        if (folder is null || string.IsNullOrWhiteSpace(request.NewFolderName))
            return [];

        return MapiBundle(async (session, cancellationToken) =>
        {
            try
            {
                await MapiFolderOperations.RenameFolderAsync(session, RequireFolderId(folder), request.NewFolderName.Trim(), cancellationToken).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                Logger.Warning("Skipping MAPI folder rename; folder {Folder} no longer exists.", folder.FolderName);
            }
        }, request, request);
    }

    /// <summary>
    /// A first delete moves the folder into Deleted Items; deleting a folder that is already inside Deleted Items
    /// removes it for good, as OWA and Outlook do.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var folder = request.Folder;
        if (folder is null)
            return [];

        return MapiBundle(async (session, cancellationToken) =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var folderId = RequireFolderId(folder);
            var parentId = ParentFolderId(folders, folder, session);
            var deletedItems = folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted);

            try
            {
                if (deletedItems is not null && TryParseFolderId(deletedItems, out var deletedItemsId) && !IsUnder(folders, folder, deletedItems))
                    await MapiFolderOperations.MoveFolderAsync(session, folderId, parentId, deletedItemsId, folder.FolderName, cancellationToken).ConfigureAwait(false);
                else
                    await MapiFolderOperations.DeleteFolderAsync(session, parentId, folderId, hardDelete: true, cancellationToken).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                Logger.Warning("MAPI folder {Folder} is already gone; treating the delete as done.", folder.FolderName);
            }
        }, request, request);
    }

    /// <summary>The MAPI id of a folder's parent from the local tree, or the IPM subtree for a top-level folder.</summary>
    private static ulong ParentFolderId(List<MailItemFolder> folders, MailItemFolder folder, MapiSession session)
    {
        var parent = string.IsNullOrEmpty(folder.ParentRemoteFolderId) ? null : folders.FirstOrDefault(f => f.RemoteFolderId == folder.ParentRemoteFolderId);
        if (parent is not null && TryParseFolderId(parent, out var id))
            return id;

        var logon = session.Logon
            ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

        return logon.IpmSubtreeFolderId;
    }

    /// <summary>Whether <paramref name="folder"/> sits anywhere under <paramref name="ancestor"/>, walking the local parent chain.</summary>
    private static bool IsUnder(List<MailItemFolder> folders, MailItemFolder folder, MailItemFolder ancestor)
    {
        var byRemoteId = folders.Where(f => !string.IsNullOrEmpty(f.RemoteFolderId)).GroupBy(f => f.RemoteFolderId).ToDictionary(g => g.Key, g => g.First());
        var parentId = folder.ParentRemoteFolderId;
        var guard = 0;
        while (!string.IsNullOrEmpty(parentId) && guard++ < 64)
        {
            if (parentId == ancestor.RemoteFolderId)
                return true;

            if (!byRemoteId.TryGetValue(parentId, out var parent))
                break;

            parentId = parent.ParentRemoteFolderId;
        }

        return false;
    }

    /// <summary>
    /// The folder hierarchy over MAPI. Special folders come from the logon (Inbox, Sent Items, Deleted Items by folder
    /// id) and from two properties on the Inbox (Drafts, Junk by entry id), so no per-folder bind can fail.
    /// </summary>
    private async Task SynchronizeMapiFoldersAsync(CancellationToken cancellationToken)
    {
        List<MapiFolderInfo> remoteFolders;
        MapiSpecialFolderEntryIds specialEntryIds;
        Dictionary<ulong, SpecialFolderType> specialByFolderId;

        await using (var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            var session = lease.Session;
            var logon = session.Logon
                ?? throw new InvalidOperationException("The MAPI session has no mailbox logon.");

            remoteFolders = await MapiFolderOperations.ReadHierarchyAsync(session, logon.IpmSubtreeFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);
            await MapiFolderOperations.ResolveEntryIdsAsync(session, remoteFolders, cancellationToken, Diagnostics).ConfigureAwait(false);
            specialEntryIds = await MapiFolderOperations.ReadSpecialFolderEntryIdsAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);

            specialByFolderId = new Dictionary<ulong, SpecialFolderType>
            {
                [logon.InboxFolderId] = SpecialFolderType.Inbox,
                [logon.SentItemsFolderId] = SpecialFolderType.Sent,
                [logon.DeletedItemsFolderId] = SpecialFolderType.Deleted,
            };
        }

        foreach (var folder in remoteFolders)
        {
            if (folder.EntryId is not { } entryId)
                continue;

            if (specialEntryIds.Drafts is { } drafts && entryId.AsSpan().SequenceEqual(drafts))
                specialByFolderId[folder.FolderId] = SpecialFolderType.Draft;
            else if (specialEntryIds.Junk is { } junk && entryId.AsSpan().SequenceEqual(junk))
                specialByFolderId[folder.FolderId] = SpecialFolderType.Junk;
        }

        // Mail folders only, as on EWS; a special folder always counts even if its class is unusual.
        var mailFolders = remoteFolders
            .Where(f => f.IsMailFolder || specialByFolderId.ContainsKey(f.FolderId))
            .ToList();

        var localFolders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        // A row still carrying an EWS id (the account ran on EWS before the transport changed) is matched by its
        // MapiFolderId and re-keyed in place, so nothing under it moves: mail rows key on the folder row's Guid.
        var mailFolderIds = mailFolders.Select(f => f.FolderId).ToHashSet();
        var localByRemoteId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());
        var localByMapiId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.MapiFolderId))
            .GroupBy(f => f.MapiFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        var structureChanged = false;
        var seenRemoteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var remote in mailFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteId = ToMapiId(remote.FolderId);
            seenRemoteIds.Add(remoteId);
            var parentRemoteId = mailFolderIds.Contains(remote.ParentFolderId) ? ToMapiId(remote.ParentFolderId) : null;

            var specialType = specialByFolderId.TryGetValue(remote.FolderId, out var mapped) ? mapped : SpecialFolderType.Other;
            var isSystem = specialType != SpecialFolderType.Other;
            var mapiFolderId = remote.FolderId.ToString("X16", CultureInfo.InvariantCulture);

            if (localByRemoteId.TryGetValue(remoteId, out var existing) || localByMapiId.TryGetValue(mapiFolderId, out existing))
            {
                if (existing.RemoteFolderId != remoteId)
                {
                    Logger.Information("MAPI folder sync: re-keying {Name} from its EWS id to {Key}.", remote.DisplayName, remoteId);
                    existing.RemoteFolderId = remoteId;
                    structureChanged = true;
                }

                if (existing.FolderName != remote.DisplayName || existing.ParentRemoteFolderId != parentRemoteId)
                    structureChanged = true;

                // Classification here comes from ids, not from a bind that may have failed, so both corrections are
                // safe. The Inbox is never left unpinned: the navigation only looks for it at the top level.
                if (existing.IsSystemFolder && !isSystem)
                {
                    existing.IsSticky = false;
                    structureChanged = true;
                }

                if ((!existing.IsSystemFolder && isSystem) || (specialType == SpecialFolderType.Inbox && !existing.IsSticky))
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                existing.FolderName = remote.DisplayName;
                existing.ParentRemoteFolderId = parentRemoteId;
                existing.SpecialFolderType = specialType;
                existing.IsSystemFolder = isSystem;
                existing.MapiFolderId = mapiFolderId;
                await ExchangeChangeProcessor.UpdateFolderAsync(existing).ConfigureAwait(false);
            }
            else
            {
                structureChanged = true;
                await ExchangeChangeProcessor.InsertFolderAsync(new MailItemFolder
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = Account.Id,
                    RemoteFolderId = remoteId,
                    ParentRemoteFolderId = parentRemoteId,
                    MapiFolderId = mapiFolderId,
                    FolderName = remote.DisplayName,
                    SpecialFolderType = specialType,
                    IsSticky = isSystem,
                    IsSystemFolder = isSystem,
                    IsSynchronizationEnabled = true,
                    ShowUnreadCount = specialType != SpecialFolderType.Deleted,
                    IsCountedInAccountTotal = specialType != SpecialFolderType.Deleted,
                }).ConfigureAwait(false);
            }
        }

        foreach (var local in localFolders)
        {
            if (!string.IsNullOrEmpty(local.RemoteFolderId) && !seenRemoteIds.Contains(local.RemoteFolderId))
            {
                structureChanged = true;
                await ExchangeChangeProcessor.DeleteFolderAsync(Account.Id, local.RemoteFolderId).ConfigureAwait(false);
            }
        }

        Logger.Information("MAPI folder sync for {Account}: {Total} folders in hierarchy, {Mail} mail folders, changed={Changed}.",
            Account.Address, remoteFolders.Count, mailFolders.Count, structureChanged);

        if (structureChanged)
            Messenger.Send(new AccountFolderConfigurationUpdated(Account.Id));
    }

    #endregion
}
