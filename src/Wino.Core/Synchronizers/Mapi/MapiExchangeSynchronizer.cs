#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Exchange;
using Wino.Mapi;
using Wino.Mapi.AddressBook;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using Wino.Mapi.Transport;
using Wino.Mapi.Wire;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// The Exchange synchronizer over native MAPI/HTTP. It derives from the EWS synchronizer and replaces
/// every surface: folder hierarchy, message list, message read incl. attachments, read/flag/move/
/// delete/junk, drafts and native send, incremental mail sync over ICS, and the calendar (series
/// expanded client-side, meetings and responses), contacts and tasks folders with native writes.
/// Servers that do not advertise MAPI/HTTP are recorded on the account and the next synchronizer
/// build lands on the EWS class.
///
/// Identity: a MailCopy.Id is the MAPI message id, "mapi:" + 16 hex digits. A message id is unique
/// within a mailbox database and survives moves between folders of the same mailbox, which is what the
/// (Id, FolderId) upsert in MailService needs. Folder rows are keyed the same way, with the bare hex
/// id kept in MailItemFolder.MapiFolderId for push notifications.
///
/// Sync: ICS per folder (state in MailItemFolder.DeltaToken); the first pass yields everything, later
/// ones only deltas, deletions and read-state changes. If ICS fails the folder falls back to a full
/// read of its newest rows for that pass.
/// </summary>
public sealed class MapiExchangeSynchronizer : ExchangeSynchronizer
{
    private static new readonly ILogger Logger = Log.ForContext<MapiExchangeSynchronizer>();

    private const string MailCopyIdPrefix = "mapi:";
    private const string UserAgent = "WinoMail/MAPI";

    private MapiEndpointInfo? _endpoint;
    private MapiEndpointInfo? _archiveEndpoint;
    private MapiEndpointInfo? _publicFolderEndpoint;

    public MapiExchangeSynchronizer(MailAccount account,
                                    IExchangeAuthenticator exchangeAuthenticator,
                                    IExchangeChangeProcessor exchangeChangeProcessor,
                                    IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
                                    IContactService? contactService = null,
                                    IContactPictureFileService? contactPictureFileService = null,
                                    ITaskService? taskService = null)
        : base(account, exchangeAuthenticator, exchangeChangeProcessor, errorHandlerFactory, contactService, contactPictureFileService, taskService)
    {
    }

    // ------------------------------------------------------------------------------------------------
    // Identity helpers
    // ------------------------------------------------------------------------------------------------

    public static string ToMailCopyId(ulong messageId) => MailCopyIdPrefix + messageId.ToString("X16");

    public static bool TryParseMailCopyId(string? id, out ulong messageId)
    {
        messageId = 0;
        return id is not null
            && id.StartsWith(MailCopyIdPrefix, StringComparison.Ordinal)
            && ulong.TryParse(id.AsSpan(MailCopyIdPrefix.Length), System.Globalization.NumberStyles.HexNumber, null, out messageId);
    }

    private static bool TryParseFolderId(MailItemFolder? folder, out ulong folderId)
    {
        folderId = 0;
        return folder?.MapiFolderId is { Length: 16 } hex
            && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out folderId);
    }

    private static ulong RequireFolderId(MailItemFolder? folder)
        => TryParseFolderId(folder, out var id)
            ? id
            : throw new InvalidOperationException($"Folder '{folder?.FolderName}' has no MAPI folder id yet; a folder sync must run first.");

    private static ulong RequireMessageId(MailCopy item)
        => TryParseMailCopyId(item.Id, out var id)
            ? id
            : throw new InvalidOperationException($"Mail '{item.Subject}' was synced by a different provider (id '{item.Id}'); it is not addressable over MAPI until the next sync.");

    // ------------------------------------------------------------------------------------------------
    // Session
    // ------------------------------------------------------------------------------------------------

    // ---- Session reuse -------------------------------------------------------------------------------
    // Connect + Logon cost two round trips plus Autodiscover on first use; doing that per operation was
    // the single largest fixed cost of every click. One session is kept per synchronizer behind a gate
    // and reopened when it ages, idles, or faults. A caller that finds the gate held (a sync pass in
    // flight) opens a temporary session as before rather than waiting, so actions stay responsive.

    private static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionMaxIdle = TimeSpan.FromMinutes(4);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    // The ambient borrow is a holder set BEFORE the first await of the acquire (in a non-async method),
    // because a value set inside an async method never flows back to its caller; the holder's field is
    // filled once the session is known and cleared when the lease ends.
    private sealed class AmbientHolder { public MapiSession? Session; }
    private static readonly AsyncLocal<AmbientHolder?> AmbientSession = new();
    private MapiSession? _sharedSession;
    private DateTime _sharedOpenedUtc;
    private DateTime _sharedUsedUtc;

    private enum LeaseKind { Shared, Temporary, Nested }

    /// <summary>
    /// A borrowed session. Disposing returns the shared one to the gate (dropping it when it
    /// faulted), closes a temporary one, or does nothing for a nested borrow of the ambient one.
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
        // Already inside a borrow on this logical flow (a body download during a sync pass, say): use it.
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
        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        var transport = new MapiHttpTransport(endpoint.MailStoreUrl, credential, UserAgent, line => Logger.Debug("MAPI {Account} {Line}", Account.Address, line));
        return await MapiSession.OpenAsync(transport, endpoint.LegacyDn, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MapiCredential> ResolveCredentialAsync()
    {
        var token = await ExchangeAuthenticator.TryGetBearerTokenAsync(Account).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            // A kept session (up to SessionMaxAge) can straddle the token's expiry; the transport
            // re-mints through the authenticator's cache on a 401 rather than failing the sync.
            return new MapiCredential.Bearer(token, _ => ExchangeAuthenticator.TryGetBearerTokenAsync(Account)!);
        }

        var credentials = await ExchangeAuthenticator.GetCredentialsAsync(Account).ConfigureAwait(false);
        return new MapiCredential.Integrated("NTLM", (credentials as WebCredentials)?.Credentials as NetworkCredential);
    }

    /// <summary>The Autodiscover endpoint that sits beside the account's Exchange server URL.</summary>
    private Uri AutodiscoverUrl
    {
        get
        {
            var ewsUri = new Uri(Account.ServerInformation!.IncomingServer);
            return new Uri($"{ewsUri.Scheme}://{ewsUri.Host}/autodiscover/autodiscover.xml");
        }
    }

    /// <summary>Autodiscover once per synchronizer instance; the endpoint and DN do not change under a session.</summary>
    private async Task<MapiEndpointInfo> ResolveEndpointAsync(MapiCredential credential, CancellationToken cancellationToken)
    {
        if (_endpoint is not null)
            return _endpoint;

        var server = Account.ServerInformation!;
        try
        {
            _endpoint = await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl, Account.Address, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (MapiNotAdvertisedException ex) when (server.ExchangeTransport == ExchangeTransport.Automatic)
        {
            // The server does not offer MAPI/HTTP (pre-2013 SP1, or disabled). Remember that on the
            // account so the next synchronizer build is the EWS one, and ask for that build now.
            Logger.Warning("MAPI {Account}: {Reason} Falling back to Exchange Web Services.", Account.Address, ex.Message);
            server.DetectedExchangeTransport = ExchangeTransport.Ews;
            await ExchangeChangeProcessor.UpdateAccountServerInformationAsync(server).ConfigureAwait(false);
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = Account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
            throw;
        }

        if (server.DetectedExchangeTransport != ExchangeTransport.MapiHttp)
        {
            server.DetectedExchangeTransport = ExchangeTransport.MapiHttp;
            await ExchangeChangeProcessor.UpdateAccountServerInformationAsync(server).ConfigureAwait(false);
        }

        return _endpoint;
    }

    private void Diagnostics(string line) => Logger.Debug("MAPI {Account} {Line}", Account.Address, line);

    // ------------------------------------------------------------------------------------------------
    // Mail sync, replacing the EWS SyncFolderItems loop end to end
    // ------------------------------------------------------------------------------------------------

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        // Nothing here touches EWS: folders are keyed by their MAPI id. The base signatures still carry
        // an ExchangeService, which is unused.
        if (options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            await SynchronizeFoldersAsync(null!, cancellationToken).ConfigureAwait(false);

        if (options.Type == MailSynchronizationType.FoldersOnly)
            return MailSynchronizationResult.Empty;

        var foldersToSync = (await ExchangeChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
            .Where(f => TryParseFolderId(f, out _))
            .ToList();

        var downloaded = new List<MailCopy>();

        if (foldersToSync.Count > 0)
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;

            foreach (var folder in foldersToSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    downloaded.AddRange(await SynchronizeFolderMessagesAsync(session, folder, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                {
                    // The folder was deleted on the server. Drop it locally so it stops failing every sync
                    // and disappears from the tree, then refresh the navigation.
                    Logger.Warning("MAPI folder {Folder} ({Fid}) no longer exists on the server; removing locally.", folder.FolderName, folder.MapiFolderId);
                    await ExchangeChangeProcessor.DeleteFolderAsync(Account.Id, folder.RemoteFolderId).ConfigureAwait(false);
                    WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
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
            }
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    /// <summary>
    /// One folder. ICS when it works: the server tells us what changed since the state we hand it,
    /// and the first sync of a folder yields everything. If ICS fails for any reason the folder
    /// falls back to the full read below for this pass and its state is cleared, so the app never
    /// gets worse than the pre-ICS behaviour while the stream parser is still being proven.
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
        catch (Exception ex) when (ex is MapiException or InvalidOperationException)
        {
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
            throw new InvalidOperationException("ICS returned no state; the transfer-state stream was not understood.");

        var local = await ExchangeChangeProcessor.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
        var localById = IndexById(local);

        var stateUpdates = new List<MailCopyStateUpdate>();
        var downloaded = await UpsertRemoteMessagesAsync(sync.Changes, folder, localById, stateUpdates, cancellationToken).ConfigureAwait(false);

        if (stateUpdates.Count > 0)
            await ExchangeChangeProcessor.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);

        // Read-state echoes are applied unconditionally, as the EWS path does with ReadFlagChange: the
        // local row may already carry the value from the optimistic UI, and the change notification
        // this raises is what tells the list the server has confirmed it.
        foreach (var (messageId, isRead) in sync.ReadStateChanges)
        {
            var id = ToMailCopyId(messageId);
            if (localById.ContainsKey(id))
                await ExchangeChangeProcessor.ChangeMailReadStatusAsync(id, isRead).ConfigureAwait(false);
        }

        var deleted = sync.DeletedMessageIds
            .Select(ToMailCopyId)
            .Where(localById.ContainsKey)
            .Distinct()
            .ToList();

        if (deleted.Count > 0)
            await ExchangeChangeProcessor.DeleteMailsAsync(Account.Id, deleted).ConfigureAwait(false);

        folder.DeltaToken = sync.NewState.Serialize();
        await ExchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        await ExchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        Logger.Information("MAPI ICS sync {Account}/{Folder}: {Mode}, {Changes} changes ({New} new), {StateChanges} state changes, {ReadChanges} read echoes, {Deleted} removed, {Buffers} buffers.",
            Account.Address, folder.FolderName, sync.IsInitial ? "initial" : "incremental", sync.Changes.Count, downloaded.Count, stateUpdates.Count, sync.ReadStateChanges.Count, deleted.Count, sync.Buffers);

        return downloaded;
    }

    /// <summary>
    /// The pre-ICS path: read the newest rows, insert what is new, apply read/flag changes to what
    /// exists, and delete local rows the server no longer has within the window that was read.
    /// </summary>
    private async Task<List<MailCopy>> SynchronizeFolderMessagesFullReadAsync(MapiSession session, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var folderId = RequireFolderId(folder);
        var limit = (int)InitialMessageDownloadCountPerFolder;

        var remote = await MapiMessageOperations.ReadMessageListAsync(session, folderId, limit, cancellationToken, Diagnostics).ConfigureAwait(false);
        var remoteById = remote.ToDictionary(m => ToMailCopyId(m.MessageId));

        var local = await ExchangeChangeProcessor.GetMailsByFolderIdAsync(folder.Id).ConfigureAwait(false);
        var localById = IndexById(local);

        var stateUpdates = new List<MailCopyStateUpdate>();
        var downloaded = await UpsertRemoteMessagesAsync(remote, folder, localById, stateUpdates, cancellationToken).ConfigureAwait(false);

        if (stateUpdates.Count > 0)
            await ExchangeChangeProcessor.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);

        // Deletions: local rows absent from the server's list. When the list was capped, only rows that
        // fall inside the window we actually saw can be judged; older ones are left alone until ICS.
        var windowStart = remote.Count >= limit ? remote.Min(m => m.ReceivedTime ?? DateTime.MinValue) : DateTime.MinValue;
        // A draft row that is not MAPI-addressed yet (local, or saved and not mapped) must survive:
        // deleting it closes the compose window that is bound to it.
        var stale = local
            .Where(m => m.Id is not null && !remoteById.ContainsKey(m.Id) && !m.IsLocalDraft && m.CreationDate >= windowStart)
            .Where(m => !(m.IsDraft && !TryParseMailCopyId(m.Id, out _)))
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

    /// <summary>The folder's local rows keyed by id; a duplicate id keeps the first row.</summary>
    private static Dictionary<string, MailCopy> IndexById(List<MailCopy> local)
        => local.Where(m => m.Id is not null).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>
    /// Folds a batch of server rows into the folder, shared by the ICS and full-read passes. A row that
    /// already exists locally contributes a read/flag state update only: re-creating it would mint a new
    /// FileId and orphan the cached body. Anything else is new here (or moved here from another folder,
    /// where the upsert deletes the other row by id) and is inserted. Returns the rows that were
    /// inserted; <paramref name="stateUpdates"/> collects the rest for one batched write.
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
            var id = ToMailCopyId(message.MessageId);

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
        => MapToMailCopy(message, folder.Id, folder.SpecialFolderType == SpecialFolderType.Draft);

    private static MailCopy MapToMailCopy(MapiMessageInfo message, Guid folderRowId, bool inDraftsFolder)
    {
        var received = message.ReceivedTime ?? message.SentTime ?? message.LastModified ?? DateTime.UtcNow;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = ToMailCopyId(message.MessageId),
            FolderId = folderRowId,
            ThreadId = message.ConversationId is { Length: > 0 } conversation ? Convert.ToHexString(conversation) : null,
            MessageId = message.InternetMessageId,
            InReplyTo = message.InReplyTo,
            References = message.References,
            Subject = message.Subject,
            FromName = message.FromName,
            FromAddress = message.FromAddress,
            CreationDate = DateTime.SpecifyKind(received, DateTimeKind.Utc),
            IsRead = message.IsRead,
            IsFlagged = message.IsFlagged,
            HasAttachments = message.HasAttachments,
            Importance = message.Importance switch { 0 => MailImportance.Low, 2 => MailImportance.High, _ => MailImportance.Normal },
            // Unsent marks a draft only on mail: appointments and other non-mail classes carry the flag
            // too, and a deleted event must not surface as a "Draft" in Deleted Items.
            IsDraft = inDraftsFolder
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

    // ------------------------------------------------------------------------------------------------
    // Message body: properties and attachments off the wire, assembled into a MIME message
    // ------------------------------------------------------------------------------------------------

    private MapiCalendarTags? _calendarTags;

    private async Task<MapiCalendarTags> ResolveCalendarTagsAsync(MapiSession session, CancellationToken cancellationToken)
        => _calendarTags ??= await MapiCalendarOperations.ResolveTagsAsync(session, cancellationToken, Diagnostics).ConfigureAwait(false);

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress? transferProgress = null, CancellationToken cancellationToken = default)
    {
        var messageId = RequireMessageId(mailItem);

        // An item of a read-only remote tree (public folder or online archive) has no local folder row:
        // its MIME is read from the remote store by the folder id the navigation node carries.
        if (mailItem.AssignedFolder is { IsRemoteReadOnlyNode: true } remoteFolder && !string.IsNullOrEmpty(remoteFolder.RemoteFolderId))
        {
            var remoteMime = remoteFolder.IsOnlineArchiveNode
                ? await GetOnlineArchiveMailMimeAsync(remoteFolder.RemoteFolderId, mailItem.Id, cancellationToken).ConfigureAwait(false)
                : await GetPublicFolderMailMimeAsync(remoteFolder.RemoteFolderId, mailItem.Id, cancellationToken).ConfigureAwait(false);

            if (remoteMime is null)
                throw new SynchronizerEntityNotFoundException($"Mail '{mailItem.Subject}' is not available in its remote folder.");

            using var remoteStream = new System.IO.MemoryStream(remoteMime);
            var remoteMessage = await MimeKit.MimeMessage.LoadAsync(remoteStream, cancellationToken).ConfigureAwait(false);
            await ExchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, remoteMessage, Account.Id).ConfigureAwait(false);
            return;
        }

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
            // One extra property read, only when the headers cannot supply it.
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

            // A meeting message carries its schedule in named properties, not MIME; the text/calendar
            // part the app's invitation handling expects (and EWS's MIME download provided) is built here.
            if (method is not null)
            {
                try
                {
                    var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
                    var meeting = await MapiCalendarOperations.ReadMeetingIdentityAsync(session, folderId, messageId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
                    calendarPart = MapiCalendarOperations.BuildICalendar(meeting, method, DateTime.UtcNow);

                    if (method == "REQUEST")
                        await TryMapCalendarInvitationAsync(session, tags, mailItem, InvitationDetails.Parse(calendarPart).Uid, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Links a meeting request to the calendar item it created, in the shape the Outlook synchronizer
    /// writes: the appointment whose clean global object id forms the request's UID is looked up in each
    /// synced calendar, persisted locally when the calendar sync has not reached it yet, and the mapping
    /// row the reader's invitation card resolves is written. Failures are logged; the mail itself is
    /// saved regardless.
    /// </summary>
    private async Task TryMapCalendarInvitationAsync(MapiSession session, MapiCalendarTags tags, MailCopy mailCopy, string? invitationUid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invitationUid))
            return;

        var calendars = await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);
        if (calendars == null || calendars.Count == 0)
            return;

        foreach (var calendar in calendars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryParseMailCopyId(calendar.RemoteCalendarId, out var calendarFolderId))
                continue;

            try
            {
                var rows = await MapiCalendarOperations.ReadAppointmentsAsync(session, calendarFolderId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
                var row = rows.FirstOrDefault(r => r.IsAppointment
                                                   && r.CleanGlobalObjectId is { Length: > 0 } goid
                                                   && string.Equals(Convert.ToHexString(goid), invitationUid, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                    continue;

                var remoteEventId = ToMailCopyId(row.MessageId);
                var localCalendarItem = await ExchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, remoteEventId).ConfigureAwait(false);

                // Not synced yet: persist the appointment the way the calendar sync does, so the card can
                // respond without waiting for the next calendar pass.
                if (localCalendarItem == null)
                {
                    if (row.IsMeeting)
                    {
                        try
                        {
                            row.Attendees = await MapiCalendarOperations.ReadAttendeesAsync(session, calendarFolderId, row.MessageId, cancellationToken, Diagnostics).ConfigureAwait(false);
                        }
                        catch (MapiException ex)
                        {
                            Logger.Debug(ex, "MAPI calendar {Account}: attendees of 0x{Id:X16} not read.", Account.Address, row.MessageId);
                        }
                    }

                    if (MapiCalendarExpander.Master(row) is { } master)
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(master, calendar, Account).ConfigureAwait(false);

                    var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
                    var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);
                    foreach (var occurrence in MapiCalendarExpander.Expand(row, windowStartUtc, windowEndUtc, Diagnostics))
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(occurrence, calendar, Account).ConfigureAwait(false);

                    localCalendarItem = await ExchangeChangeProcessor.GetCalendarItemAsync(calendar.Id, remoteEventId).ConfigureAwait(false);
                }

                if (localCalendarItem == null)
                    return;

                await ExchangeChangeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping
                {
                    Id = Guid.NewGuid(),
                    AccountId = Account.Id,
                    MailCopyId = mailCopy.Id,
                    InvitationUid = invitationUid,
                    CalendarId = calendar.Id,
                    CalendarItemId = localCalendarItem.Id,
                    CalendarRemoteEventId = remoteEventId
                }).ConfigureAwait(false);

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "MAPI {Account}: invitation mail {MailCopyId} not mapped to calendar {CalendarId}.", Account.Address, mailCopy.Id, calendar.Id);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Mail operations. Each runs on a borrowed session inside the bundle the base class executes;
    // the ExchangeService the executor passes is ignored.
    // ------------------------------------------------------------------------------------------------

    private List<IRequestBundle<EwsRequest>> MapiBundle(Func<MapiSession, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => Bundle(async _ =>
        {
            await using var lease = await AcquireSessionAsync(CancellationToken.None).ConfigureAwait(false);
            var session = lease.Session;
            await action(session).ConfigureAwait(false);
        }, request, uiChangeRequest);

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var byFolder = requests.GroupBy(r => r.Item.FolderId).ToList();

        return MapiBundle(async session =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var group in byFolder)
            {
                var folderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == group.Key));
                var ids = group.Select(r => RequireMessageId(r.Item)).ToList();
                await MapiMessageOperations.SetReadAsync(session, folderId, ids, isRead).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return MapiBundle(async session =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var request in requests)
            {
                var folderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == request.Item.FolderId));
                await MapiMessageOperations.SetFlaggedAsync(session, folderId, RequireMessageId(request.Item), request.IsFlagged).ConfigureAwait(false);
            }

            // The list row reads the flag from the local row; persist it once the server has it.
            foreach (var request in requests)
                await ExchangeChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = RequireFolderId(requests[0].ToFolder);
        var bySource = requests.GroupBy(r => r.FromFolder?.Id ?? r.Item.FolderId).ToList();

        return MapiBundle(async session =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            foreach (var group in bySource)
            {
                var source = RequireFolderId(folders.FirstOrDefault(f => f.Id == group.Key));
                var ids = group.Select(r => RequireMessageId(r.Item)).ToList();
                var complete = await MapiMessageOperations.MoveAsync(session, source, destination, ids, copy: false).ConfigureAwait(false);
                if (!complete)
                    Logger.Warning("MAPI move: not every message moved from {Source} to {Destination}.", group.Key, requests[0].ToFolder.FolderName);
            }
        }, requests[0], requests);
    }

    /// <summary>
    /// Junk is a move to or from the target folder plus the server's Blocked Senders list, which
    /// lives in the junk rule's condition: marking junk adds the sender there (and drops it from
    /// the safe lists), marking not-junk removes it, the same edits EWS MarkAsJunk makes. The list
    /// edit is best effort: the move stands even if the rule cannot be rewritten.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var bundles = Move(new BatchMoveRequest(requests.Select(r => new MoveRequest(r.Item, r.FromFolder, r.TargetFolder))));

        var senders = requests
            .Where(r => !string.IsNullOrWhiteSpace(r.Item.FromAddress) && r.Item.FromAddress.Contains('@'))
            .Select(r => (r.IsJunk, Address: JunkRuleEditor.Normalize(r.Item.FromAddress)))
            .Distinct()
            .ToList();
        if (senders.Count == 0)
            return bundles;

        bundles.AddRange(MapiBundle(async session =>
        {
            try
            {
                var inbox = session.Logon!.InboxFolderId;
                var written = await MapiRulesOperations.UpdateJunkRuleAsync(session, inbox, root =>
                {
                    foreach (var (isJunk, address) in senders)
                    {
                        if (isJunk) JunkRuleEditor.Block(root, address);
                        else JunkRuleEditor.Unblock(root, address);
                    }
                    return root;
                }, CancellationToken.None, Diagnostics).ConfigureAwait(false);

                if (written)
                {
                    Logger.Information("MAPI junk {Account}: blocked senders updated ({Blocked} blocked, {Unblocked} unblocked).", Account.Address, senders.Count(s => s.IsJunk), senders.Count(s => !s.IsJunk));
                    await ImportServerJunkListsAsync(session, inbox, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    Logger.Debug("MAPI junk {Account}: no junk rule on the server; blocked senders not updated.", Account.Address);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "MAPI junk {Account}: the server's blocked senders could not be updated; the move stands.", Account.Address);
            }
        }, requests[0], requests));

        return bundles;
    }

    // ------------------------------------------------------------------------------------------------
    // Inbox rules: the rules table of the Inbox, read and written over RopGetRulesTable /
    // RopModifyRules. Same DTO subset and fidelity guard as the EWS path; the classic Outlook rule
    // blob (IPM.RuleOrganizer) is detected and removed only with the user's consent, as EWS does.
    // ------------------------------------------------------------------------------------------------

    private static string? FolderKey(MailItemFolder folder) => folder.RemoteFolderId ?? folder.MapiFolderId;

    private static ulong ParseFolderId(MailItemFolder folder) => TryParseFolderId(folder, out var id) ? id : 0;

    public override async Task<IReadOnlyList<RemoteInboxRule>> GetInboxRulesAsync(CancellationToken cancellationToken = default)
    {
        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var inboxId = RequireFolderId(folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Inbox));

        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;

        var rows = await MapiRulesOperations.ReadRulesAsync(session, inboxId, cancellationToken, RulesDiagnostics).ConfigureAwait(false);

        // Only Outlook/OWA-managed rules are the user's; delegate and other provider rules stay hidden, as on EWS.
        var userRules = rows.Where(r => string.Equals(r.Provider, PropertyTags.RuleOrganizerProvider, StringComparison.OrdinalIgnoreCase)).ToList();

        uint? keywordsTag = null;
        if (userRules.Any(r => r.Actions?.Any(a => a.Type == Wino.Mapi.Rules.RuleActionType.Tag) == true))
            keywordsTag = await TryResolveKeywordsTagAsync(session, cancellationToken).ConfigureAwait(false);

        var byMapiId = folders.Where(f => TryParseFolderId(f, out _)).ToDictionary(f => ParseFolderId(f), f => f);
        var rules = userRules
            .Select(r => MapiRuleMapper.ToDto(r, id => byMapiId.TryGetValue(id, out var folder) ? FolderKey(folder) : null, keywordsTag))
            .OrderBy(r => r.Priority)
            .ToList();

        Logger.Information("MAPI rules {Account}: {Count} rules ({ReadOnly} read-only).", Account.Address, rules.Count, rules.Count(r => r.IsReadOnly));
        return rules;
    }

    public override async Task<InboxRuleUpdateResult> UpdateInboxRulesAsync(IReadOnlyList<InboxRuleChange> changes, bool removeOutlookRuleBlob = false, CancellationToken cancellationToken = default)
    {
        if (changes == null || changes.Count == 0)
            return InboxRuleUpdateResult.Ok;

        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var inboxId = RequireFolderId(folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Inbox));
        var byKey = folders
            .Where(f => TryParseFolderId(f, out _) && FolderKey(f) is not null)
            .GroupBy(f => FolderKey(f)!)
            .ToDictionary(g => g.Key, g => ParseFolderId(g.First()));

        try
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;

            // The Outlook rule blob is only removed with explicit user consent: while a mailbox's rules are
            // held by classic Outlook's blob, a table write would leave the two out of step. Callers surface
            // the refusal as a consent prompt and retry with removeOutlookRuleBlob.
            var blob = await MapiRulesOperations.FindOutlookRuleBlobAsync(session, inboxId, cancellationToken).ConfigureAwait(false);
            if (blob is { } blobId)
            {
                if (!removeOutlookRuleBlob)
                {
                    Logger.Warning("MAPI rules {Account}: update blocked by the classic Outlook rule blob.", Account.Address);
                    return new InboxRuleUpdateResult
                    {
                        Success = false,
                        Errors = new[] { Translator.Rules_OutlookBlobExists },
                        RequiresOutlookRuleBlobRemoval = true
                    };
                }

                await MapiRulesOperations.DeleteFaiMessageAsync(session, inboxId, blobId, cancellationToken).ConfigureAwait(false);
                Logger.Information("MAPI rules {Account}: removed the classic Outlook rule blob with consent.", Account.Address);
            }

            uint? keywordsTag = null;
            if (changes.Any(c => c.Rule is not null && MapiRuleMapper.NeedsKeywordsTag(c.Rule)))
                keywordsTag = await MapiRulesOperations.ResolveKeywordsTagAsync(session, cancellationToken).ConfigureAwait(false);

            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case InboxRuleChangeKind.Create:
                        await MapiRulesOperations.AddRuleAsync(session, inboxId, MapiRuleMapper.ToDefinition(change.Rule, key => byKey.TryGetValue(key, out var id) ? id : null, keywordsTag), cancellationToken, RulesDiagnostics).ConfigureAwait(false);
                        break;

                    case InboxRuleChangeKind.Update:
                        if (!MapiRuleMapper.TryParseRuleId(change.Rule?.Id, out var updateId))
                            return InboxRuleUpdateResult.Failed($"Rule '{change.Rule?.Name}' has no server id.");
                        await MapiRulesOperations.UpdateRuleAsync(session, inboxId, updateId, MapiRuleMapper.ToDefinition(change.Rule, key => byKey.TryGetValue(key, out var id) ? id : null, keywordsTag), cancellationToken, RulesDiagnostics).ConfigureAwait(false);
                        break;

                    case InboxRuleChangeKind.Delete:
                        if (!MapiRuleMapper.TryParseRuleId(change.RuleId, out var deleteId))
                            return InboxRuleUpdateResult.Failed("The rule has no server id.");
                        await MapiRulesOperations.DeleteRuleAsync(session, inboxId, deleteId, cancellationToken, RulesDiagnostics).ConfigureAwait(false);
                        break;
                }
            }

            return InboxRuleUpdateResult.Ok;
        }
        catch (NotSupportedException ex)
        {
            return InboxRuleUpdateResult.Failed(ex.Message);
        }
        catch (MapiException ex)
        {
            Logger.Warning(ex, "MAPI rules {Account}: update failed.", Account.Address);
            return InboxRuleUpdateResult.Failed(ex.Message);
        }
    }

    private void RulesDiagnostics(string line) => Logger.Debug("MAPI rules {Account}: {Line}", Account.Address, line);

    private async Task<uint?> TryResolveKeywordsTagAsync(MapiSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await MapiRulesOperations.ResolveKeywordsTagAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (MapiException ex)
        {
            Logger.Debug(ex, "MAPI rules {Account}: the Keywords property could not be resolved; category actions read as unsupported.", Account.Address);
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Junk lists: the server's Blocked and Safe senders live in the junk rule's condition, which EWS
    // never exposed. They are merged into the account's local lists on each folder pass (one-way,
    // server to local; local-only entries survive), and an edit from the settings page goes straight
    // to the server through UpdateServerJunkListAsync.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Failure here never fails the folder sync.</summary>
    private async Task ImportServerJunkListsAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken)
    {
        try
        {
            var lists = await MapiRulesOperations.ReadJunkListsAsync(session, inboxFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);
            if (lists is null)
            {
                Logger.Debug("MAPI junk {Account}: the mailbox has no junk rule.", Account.Address);
                return;
            }

            var blocked = await ExchangeChangeProcessor.ImportJunkSendersAsync(Account.Id, JunkListType.Blocked, lists.BlockedSenders).ConfigureAwait(false);
            var safe = await ExchangeChangeProcessor.ImportJunkSendersAsync(Account.Id, JunkListType.Safe, lists.SafeSenders).ConfigureAwait(false);
            if (blocked > 0 || safe > 0)
                Logger.Information("MAPI junk {Account}: imported {Blocked} blocked and {Safe} safe senders from the server (server holds {ServerBlocked}/{ServerSafe}).",
                    Account.Address, blocked, safe, lists.BlockedSenders.Count, lists.SafeSenders.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI junk {Account}: server junk lists could not be read; local lists unchanged.", Account.Address);
        }
    }

    public override bool SupportsServerJunkLists => true;

    public override async Task UpdateServerJunkListAsync(string address, JunkListType listType, bool add, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var inbox = session.Logon!.InboxFolderId;
        var written = await MapiRulesOperations.UpdateJunkRuleAsync(session, inbox, root => (listType, add) switch
        {
            (JunkListType.Blocked, true) => JunkRuleEditor.Block(root, address),
            (JunkListType.Blocked, false) => JunkRuleEditor.Unblock(root, address),
            (_, true) => JunkRuleEditor.Trust(root, address),
            _ => JunkRuleEditor.Untrust(root, address),
        }, cancellationToken, Diagnostics).ConfigureAwait(false);

        if (!written)
            throw new InvalidOperationException("The mailbox has no junk rule to hold the list.");

        Logger.Information("MAPI junk {Account}: {List} list {Action} {Address}.", Account.Address, listType, add ? "gained" : "dropped", address);
    }

    // ------------------------------------------------------------------------------------------------
    // GAL: NSPI over MAPI/HTTP on the AddressBook endpoint Autodiscover named, replacing the EWS
    // ResolveName lookup. GetMatches with a PidTagAnr restriction is the directory's own ambiguous-name
    // resolution, so "mat" finds the Matts the way Outlook's To line does. The address-book endpoint is
    // a separate transport from the mailbox session (no ROP logon), so each search binds its own
    // short-lived NSPI client rather than borrowing the shared MapiSession.
    // ------------------------------------------------------------------------------------------------

    public override async Task<IReadOnlyList<AccountContact>> SearchGlobalAddressListAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return Array.Empty<AccountContact>();

        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        if (endpoint.AddressBookUrl is null)
            throw new InvalidOperationException("Autodiscover named no MAPI/HTTP AddressBook endpoint for this mailbox.");

        // Failures propagate: the GAL service degrades to local-only suggestions on any exception.
        var transport = new MapiHttpTransport(endpoint.AddressBookUrl, credential, UserAgent, line => Logger.Debug("MAPI nspi {Account} {Line}", Account.Address, line));
        await using var nspi = new NspiClient(transport, Diagnostics);
        await nspi.BindAsync(cancellationToken).ConfigureAwait(false);
        var rows = await nspi.GetMatchesAsync(query.Trim(), NspiClient.SuggestionColumns, (uint)Math.Min(maxResults * 2, 100), cancellationToken).ConfigureAwait(false);
        var results = MapGalRows(rows, maxResults, Account.Id);
        Logger.Debug("MAPI GAL {Account}: '{Query}' matched {Rows} rows, {Results} usable.", Account.Address, query, rows.Count, results.Count);
        return results;
    }

    /// <summary>Rows in <see cref="NspiClient.SuggestionColumns"/> order to transient contacts: SMTP-addressed entries only, deduped by address.</summary>
    internal static List<AccountContact> MapGalRows(IReadOnlyList<PropertyValue[]> rows, int maxResults, Guid accountId = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<AccountContact>();
        foreach (var row in rows)
        {
            var smtp = row[1].AsString;
            if (string.IsNullOrWhiteSpace(smtp) && string.Equals(row[3].AsString, "SMTP", StringComparison.OrdinalIgnoreCase))
                smtp = row[2].AsString;
            if (string.IsNullOrWhiteSpace(smtp) || !smtp.Contains('@') || !seen.Add(smtp.Trim()))
                continue;

            results.Add(new AccountContact
            {
                MailAccountId = accountId,
                SourceKind = ContactSourceKind.Exchange,
                Address = smtp.Trim(),
                Name = string.IsNullOrWhiteSpace(row[0].AsString) ? smtp.Trim() : row[0].AsString,
                CompanyName = row[4].AsString,
                JobTitle = row[5].AsString,
                Department = row[6].AsString,
            });
            if (results.Count >= maxResults)
                break;
        }
        return results;
    }

    /// <summary>Soft delete: a move to Deleted Items; an item already there is hard-deleted.</summary>
    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var byFolder = requests.GroupBy(r => r.MailItem.FolderId).ToList();

        return MapiBundle(async session =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var deletedItems = folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted);
            var deletedItemsId = RequireFolderId(deletedItems);

            foreach (var group in byFolder)
            {
                var sourceFolder = folders.FirstOrDefault(f => f.Id == group.Key);
                var source = RequireFolderId(sourceFolder);
                var sourceIsDrafts = sourceFolder?.SpecialFolderType == SpecialFolderType.Draft;

                // A draft is deleted for good, as Outlook does on discard: moved to Deleted Items it would
                // come back on the next sync as an empty "Draft" row there, under a new id.
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
                    await MapiMessageOperations.DeleteAsync(session, source, drafts).ConfigureAwait(false);
                if (mails.Count == 0)
                    continue;

                if (source == deletedItemsId)
                    await MapiMessageOperations.DeleteAsync(session, source, mails).ConfigureAwait(false);
                else
                    await MapiMessageOperations.MoveAsync(session, source, deletedItemsId, mails, copy: false).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest request)
        => Move(new BatchMoveRequest(request.Select(a => new MoveRequest(a.Item, a.FromFolder, a.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    // ------------------------------------------------------------------------------------------------
    // Drafts and send (native): the compose window's MIME is lifted into properties and written with
    // RopCreateMessage / RopSetProperties / write streams / RopModifyRecipients / attachments; sending
    // is RopSubmitMessage with the Sent Items server id set. No EWS on the mail path.
    // ------------------------------------------------------------------------------------------------

    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var draftsFolder = preparation.CreatedLocalDraftCopy.AssignedFolder;

        return MapiBundle(async session =>
        {
            var draftsFolderId = RequireFolderId(draftsFolder);
            var outgoing = MapiOutgoingMessageMapper.FromMime(preparation.CreatedLocalDraftMimeMessage);

            var messageId = await MapiMessageComposer.CreateAsync(session, draftsFolderId, outgoing, CancellationToken.None, Diagnostics).ConfigureAwait(false);
            var id = ToMailCopyId(messageId);

            var isMapped = await ExchangeChangeProcessor.MapLocalDraftAsync(
                Account.Id,
                preparation.CreatedLocalDraftCopy.UniqueId,
                id,
                id,
                preparation.CreatedLocalDraftCopy.ThreadId).ConfigureAwait(false);

            if (!isMapped)
            {
                // The local draft was discarded while the create was in flight. Delete the server
                // draft right away so the next Drafts sync cannot resurrect it.
                await MapiMessageOperations.DeleteAsync(session, draftsFolderId, [messageId]).ConfigureAwait(false);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;

        return MapiBundle(async session =>
        {
            var mime = preparation.Mime;

            // Strip the local-draft marker so it never leaks to recipients.
            mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var sentFolder = preparation.SentFolder ?? folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Sent);
            var sentFolderId = TryParseFolderId(sentFolder, out var sentId) ? sentId : session.Logon!.SentItemsFolderId;

            // Created in the Outbox, submitted from there; the transport files the copy to Sent Items and
            // deletes the original (PidTagDeleteAfterSubmit). On-prem Exchange sends as the mailbox.
            var outgoing = MapiOutgoingMessageMapper.FromMime(mime);
            await MapiMessageComposer.SendAsync(session, session.Logon!.OutboxFolderId, sentFolderId, outgoing, CancellationToken.None, Diagnostics).ConfigureAwait(false);

            // Best-effort cleanup of the server draft created by CreateDraft.
            if (preparation.MailItem is { } draft && TryParseMailCopyId(draft.Id, out var draftMessageId))
            {
                try
                {
                    if (TryParseFolderId(folders.FirstOrDefault(f => f.Id == draft.FolderId), out var draftsFolderId))
                        await MapiMessageOperations.DeleteAsync(session, draftsFolderId, [draftMessageId]).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Debug(ex, "Could not delete server draft {DraftId} after send (it may already be gone).", draft.Id);
                }
            }
        }, request, request);
    }

    /// <summary>
    /// A later save of a server draft: the new MIME is written as a fresh message beside the old one,
    /// the old one is deleted for good (a draft moved to Deleted Items would come back as an empty row),
    /// and the new "mapi:" id is handed back so the local row follows it.
    /// </summary>
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default)
    {
        var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var draftsFolderId = RequireFolderId(folders.FirstOrDefault(f => f.Id == draft.FolderId));
        var previousMessageId = TryParseMailCopyId(draft.Id, out var parsed) ? parsed : (ulong?)null;

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
                Logger.Debug(ex, "Could not delete the superseded server draft {DraftId}.", draft.Id);
            }
        }

        var id = ToMailCopyId(messageId);
        return new DraftUpdateIdentity(id, id, draft.ThreadId);
    }

    // ------------------------------------------------------------------------------------------------
    // Folder operations: create, rename, delete. The follow-up FoldersOnly sync the request delegator
    // queues after each reconciles the local tree, as it does for EWS.
    // ------------------------------------------------------------------------------------------------

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        return MapiBundle(async session =>
        {
            await MapiFolderOperations.CreateFolderAsync(session, session.Logon!.IpmSubtreeFolderId, name.Trim()).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name) || request.Folder is null)
            return [];

        return MapiBundle(async session =>
        {
            await MapiFolderOperations.CreateFolderAsync(session, RequireFolderId(request.Folder), name.Trim()).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request)
    {
        if (request.Folder is null || string.IsNullOrWhiteSpace(request.NewFolderName))
            return [];

        return MapiBundle(async session =>
        {
            try
            {
                await MapiFolderOperations.RenameFolderAsync(session, RequireFolderId(request.Folder), request.NewFolderName.Trim()).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                Logger.Warning("Skipping MAPI folder rename; folder {Folder} no longer exists.", request.Folder.FolderName);
            }
        }, request, request);
    }

    /// <summary>
    /// A first delete moves the folder into Deleted Items; deleting a folder already inside Deleted
    /// Items removes it for good, matching OWA and Outlook.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        if (request.Folder is null)
            return [];

        return MapiBundle(async session =>
        {
            var folders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
            var folderId = RequireFolderId(request.Folder);
            var parentId = ParentFolderId(folders, request.Folder, session);
            var deletedItems = folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted);

            try
            {
                if (deletedItems is not null && TryParseFolderId(deletedItems, out var deletedItemsId) && !IsUnder(folders, request.Folder, deletedItems))
                    await MapiFolderOperations.MoveFolderAsync(session, folderId, parentId, deletedItemsId, request.Folder.FolderName).ConfigureAwait(false);
                else
                    await MapiFolderOperations.DeleteFolderAsync(session, parentId, folderId, hardDelete: true).ConfigureAwait(false);
            }
            catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
            {
                Logger.Warning("MAPI folder {Folder} already absent on delete; treating as done.", request.Folder.FolderName);
            }
        }, request, request);
    }

    /// <summary>The MAPI id of a folder's parent from the local tree, or the IPM subtree for a top-level folder.</summary>
    private static ulong ParentFolderId(List<MailItemFolder> folders, MailItemFolder folder, MapiSession session)
    {
        var parent = string.IsNullOrEmpty(folder.ParentRemoteFolderId) ? null : folders.FirstOrDefault(f => f.RemoteFolderId == folder.ParentRemoteFolderId);
        return parent is not null && TryParseFolderId(parent, out var id) ? id : session.Logon!.IpmSubtreeFolderId;
    }

    /// <summary>True when <paramref name="folder"/> sits anywhere under <paramref name="ancestor"/>, walking the local parent chain.</summary>
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

    // ------------------------------------------------------------------------------------------------
    // Folder hierarchy
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The folder hierarchy over MAPI. Special folders come from the logon (Inbox, Sent, Deleted by folder
    /// id) and from two properties on the Inbox (Drafts, Junk by entry id), so there is no per-folder
    /// well-known bind and none of its failure modes.
    /// </summary>
    protected override async Task SynchronizeFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        List<MapiFolderInfo> remoteFolders;
        MapiSpecialFolderEntryIds specialEntryIds;
        Dictionary<ulong, SpecialFolderType> specialByFolderId;

        await using (var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false))
        {
            var session = lease.Session;
            var logon = session.Logon!;

            remoteFolders = await MapiFolderOperations.ReadHierarchyAsync(session, logon.IpmSubtreeFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);
            await MapiFolderOperations.ResolveEntryIdsAsync(session, remoteFolders, cancellationToken, Diagnostics).ConfigureAwait(false);
            specialEntryIds = await MapiFolderOperations.ReadSpecialFolderEntryIdsAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);

            // Self-check of the EntryID construction: the Drafts id the server hands out must equal the one
            // built from the Drafts folder's long-term id. If it does not, the ProviderUID/FolderType
            // assumption is wrong and every EntryID this client builds would be refused.
            if (specialEntryIds.Drafts is { } draftsId)
            {
                var built = remoteFolders.Any(f => f.EntryId is { } e && e.AsSpan().SequenceEqual(draftsId));
                Logger.Debug("MAPI {Account} EntryID self-check: server Drafts id {Match} a constructed folder EntryID ({Length} bytes).",
                    Account.Address, built ? "matches" : "does NOT match", draftsId.Length);
            }

            specialByFolderId = new Dictionary<ulong, SpecialFolderType>
            {
                [logon.InboxFolderId] = SpecialFolderType.Inbox,
                [logon.SentItemsFolderId] = SpecialFolderType.Sent,
                [logon.DeletedItemsFolderId] = SpecialFolderType.Deleted,
            };

            await ImportServerJunkListsAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);
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

        // Mail folders only, as the EWS path does; a special folder always counts even if its class is odd.
        var mailFolders = remoteFolders
            .Where(f => f.IsMailFolder || specialByFolderId.ContainsKey(f.FolderId))
            .ToList();

        var localFolders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);

        // Folder rows are keyed by "mapi:" + folder id, the same scheme as mail rows. A row still carrying
        // an EWS id (the account ran on EWS before the transport flipped) is matched by its MapiFolderId
        // and re-keyed in place, so nothing under it (mail rows key on the row's Guid) moves.
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
        var seenRemoteIds = new HashSet<string>();

        foreach (var remote in mailFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteId = ToMailCopyId(remote.FolderId);
            seenRemoteIds.Add(remoteId);
            var parentRemoteId = mailFolderIds.Contains(remote.ParentFolderId) ? ToMailCopyId(remote.ParentFolderId) : null;

            var specialType = specialByFolderId.TryGetValue(remote.FolderId, out var mapped) ? mapped : SpecialFolderType.Other;
            var isSystem = specialType != SpecialFolderType.Other;
            var mapiFolderId = remote.FolderId.ToString("X16");

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

                // Classification here is authoritative (ids, not a bind that may have failed), so both
                // heals are safe: a folder that stops being system is unpinned, one that becomes system
                // is pinned. The Inbox is never left unpinned; the nav only finds it at the top level.
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

        // Remove local folders that no longer exist remotely.
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
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));

        // Autodiscover names the archive as an AlternativeMailbox of type Archive, so no EWS probe is needed.
        await ProbeOnlineArchiveOverMapiAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeOnlineArchiveOverMapiAsync(CancellationToken cancellationToken)
    {
        bool hasArchive;
        try
        {
            var endpoint = await ResolveEndpointAsync(await ResolveCredentialAsync().ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            hasArchive = !string.IsNullOrEmpty(endpoint.ArchiveSmtpAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Debug(ex, "MAPI {Account}: online archive probe failed; keeping cached state.", Account.Address);
            return;
        }

        await PersistOnlineArchiveStateAsync(hasArchive).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------
    // Contacts: the Contacts folder's contents table replaces the EWS FindItems sweep, and the three
    // contact writes go native. Rows land in the same address book the EWS path fills; RemoteId is the
    // "mapi:" message id and the book is keyed by the "mapi:" folder id.
    // ------------------------------------------------------------------------------------------------

    private ulong? _contactsFolderId;
    private MapiContactTags? _contactTags;

    private async Task<(ulong FolderId, MapiContactTags Tags)?> ResolveContactsAsync(MapiSession session, CancellationToken cancellationToken)
    {
        _contactsFolderId ??= await MapiContactOperations.FindContactsFolderIdAsync(session, session.Logon!.InboxFolderId, cancellationToken).ConfigureAwait(false);
        if (_contactsFolderId is not { } folderId)
        {
            Logger.Information("MAPI contacts {Account}: the mailbox has no Contacts folder pointer; nothing to sync.", Account.Address);
            return null;
        }

        _contactTags ??= await MapiContactOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);
        return (folderId, _contactTags);
    }

    /// <summary>The contacts folder for a write, which cannot proceed without one.</summary>
    private async Task<(ulong FolderId, MapiContactTags Tags)> RequireContactsAsync(MapiSession session, CancellationToken cancellationToken)
        => await ResolveContactsAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mailbox has no Contacts folder.");

    protected override async Task<ContactSynchronizationResult> SynchronizeProviderContactsAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        if (await ResolveContactsAsync(session, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return ContactSynchronizationResult.Empty;

        var book = await GetOrCreateContactsBookAsync(ToMailCopyId(resolved.FolderId)).ConfigureAwait(false);
        var rows = await MapiContactOperations.ReadContactsAsync(session, resolved.FolderId, resolved.Tags, cancellationToken, Diagnostics).ConfigureAwait(false);

        var upserts = new List<AccountContact>();
        var photos = new List<(AccountContact, Func<Task<byte[]>>)>();
        foreach (var row in rows.Where(r => r.IsContact))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contact = MapiContactMapper.ToAccountContact(row, ToMailCopyId(row.MessageId), Account.Id, book.Id);
            upserts.Add(contact);

            if (row.HasAttachments)
            {
                var folderId = resolved.FolderId;
                var messageId = row.MessageId;
                photos.Add((contact, () => MapiContactOperations.ReadContactPhotoAsync(session, folderId, messageId, cancellationToken)!));
            }
        }

        // Photos are read on the session this pass holds, so they are fetched before the lease ends.
        await DownloadContactPhotosAsync(photos, book, cancellationToken).ConfigureAwait(false);
        await ContactService.ReplaceAddressBookAsync(book.Id, upserts, null).ConfigureAwait(false);

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
                case ContactSynchronizerOperation.Create:
                {
                    var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                    var id = await MapiContactOperations.CreateContactAsync(session, resolved.FolderId, resolved.Tags, MapiContactMapper.ToWrite(local), cancellationToken).ConfigureAwait(false);
                    Logger.Information("MAPI contacts {Account}: created 0x{Id:X16}.", Account.Address, id);

                    var mapped = RequestEntityCloner.Contact(local);
                    mapped.RemoteId = ToMailCopyId(id);
                    mapped.SourceKind = ContactSourceKind.Exchange;
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, mapped, false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Update:
                {
                    if (!TryParseMailCopyId(local.RemoteId, out var messageId))
                        throw new InvalidOperationException($"Contact '{local.DisplayName}' was synced by a different transport (id '{local.RemoteId}'); it is not addressable over MAPI until the next sync.");

                    var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                    await MapiContactOperations.UpdateContactAsync(session, resolved.FolderId, messageId, resolved.Tags, MapiContactMapper.ToWrite(local), cancellationToken).ConfigureAwait(false);
                    await ExchangeChangeProcessor.CommitContactMutationAsync(local.Id, RequestEntityCloner.Contact(local), false).ConfigureAwait(false);
                    break;
                }
                case ContactSynchronizerOperation.Delete:
                {
                    if (TryParseMailCopyId(local?.RemoteId, out var messageId))
                    {
                        var resolved = await RequireContactsAsync(session, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await MapiMessageOperations.MoveAsync(session, resolved.FolderId, session.Logon!.DeletedItemsFolderId, [messageId], cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                        {
                            Logger.Warning("MAPI contact {RemoteId} already absent on delete; treating as done.", local!.RemoteId);
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

    // ------------------------------------------------------------------------------------------------
    // Tasks: the Tasks folder's contents table replaces the EWS FindItems sweep; the three task writes
    // go native. RemoteId becomes the "mapi:" message id; a create stamps it back so the next pull
    // reconciles in place.
    // ------------------------------------------------------------------------------------------------

    private ulong? _tasksFolderId;
    private MapiTaskTags? _taskTags;

    private async Task<(ulong FolderId, MapiTaskTags Tags)?> ResolveTasksAsync(MapiSession session, CancellationToken cancellationToken)
    {
        _tasksFolderId ??= await MapiTaskOperations.FindTasksFolderIdAsync(session, session.Logon!.InboxFolderId, cancellationToken).ConfigureAwait(false);
        if (_tasksFolderId is not { } folderId)
        {
            Logger.Information("MAPI tasks {Account}: the mailbox has no Tasks folder pointer; nothing to sync.", Account.Address);
            return null;
        }

        _taskTags ??= await MapiTaskOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);
        return (folderId, _taskTags);
    }

    /// <summary>The tasks folder for a write, which cannot proceed without one.</summary>
    private async Task<(ulong FolderId, MapiTaskTags Tags)> RequireTasksAsync(MapiSession session, CancellationToken cancellationToken)
        => await ResolveTasksAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mailbox has no Tasks folder.");

    protected override async Task<TaskSynchronizationResult> SynchronizeProviderTasksAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        if (await ResolveTasksAsync(session, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return TaskSynchronizationResult.Empty;

        var list = await EnsureTaskListAsync(ToMailCopyId(resolved.FolderId), "Tasks").ConfigureAwait(false);
        var rows = await MapiTaskOperations.ReadTasksAsync(session, resolved.FolderId, resolved.Tags, cancellationToken, Diagnostics).ConfigureAwait(false);

        var tasks = rows.Where(r => r.IsTask)
            .Select(row => MapiTaskMapper.ToAccountTask(row, ToMailCopyId(row.MessageId), list))
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
                if (TryParseMailCopyId(localTask.RemoteId, out var deletedId))
                {
                    try
                    {
                        await MapiMessageOperations.MoveAsync(session, resolved.FolderId, session.Logon!.DeletedItemsFolderId, [deletedId], cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (MapiRopException ex) when (ex.ReturnValue == MapiRopException.NotFound)
                    {
                        Logger.Warning("MAPI task {RemoteId} already absent on delete; treating as done.", localTask.RemoteId);
                    }
                }

                await ExchangeChangeProcessor.CommitTaskMutationAsync(localTask.Id, null, true, localTask, request.Operation).ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }

            ulong messageId;
            if (TryParseMailCopyId(localTask.RemoteId, out messageId))
            {
                await MapiTaskOperations.UpdateTaskAsync(session, resolved.FolderId, messageId, resolved.Tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(localTask.RemoteId))
            {
                throw new InvalidOperationException($"Task '{localTask.Title}' was synced by a different transport (id '{localTask.RemoteId}'); it is not addressable over MAPI until the next sync.");
            }
            else
            {
                messageId = await MapiTaskOperations.CreateTaskAsync(session, resolved.FolderId, resolved.Tags, MapiTaskMapper.ToWrite(localTask), cancellationToken).ConfigureAwait(false);
                Logger.Information("MAPI tasks {Account}: created 0x{Id:X16}.", Account.Address, messageId);
            }

            await CommitTaskAsync(request, localTask, ToMailCopyId(messageId), null).ConfigureAwait(false);
            MarkTaskRequestProcessed(request);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Calendar: the Calendar folder(s) read over MAPI, series expanded client-side (an EWS CalendarView
    // expanded them server-side), occurrences stored flat as before. Calendars are keyed "mapi:" +
    // folder id; the MAPI folder id doubles as the push-notification key.
    // ------------------------------------------------------------------------------------------------

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
            return CalendarSynchronizationResult.Empty;

        if (Account.CalendarIntegrationSource != AccountIntegrationSource.Provider || !Account.IsCalendarAccessGranted)
            return CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable));

        try
        {
            await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            await SynchronizeMapiCalendarsAsync(session, cancellationToken).ConfigureAwait(false);

            // The periodic loop only asks for calendar metadata; Exchange has no server-side change feed
            // for events outside push, and the window read is cheap on a kept session, so every pass
            // refreshes events as well. Otherwise events would arrive only on a manual sync.

            var tags = await ResolveCalendarTagsAsync(session, cancellationToken).ConfigureAwait(false);
            var windowStartUtc = DateTime.UtcNow.AddMonths(-CalendarWindowPastMonths);
            var windowEndUtc = DateTime.UtcNow.AddMonths(CalendarWindowFutureMonths);

            foreach (var calendar in await GetCalendarsToSynchronizeAsync(options).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseMailCopyId(calendar.RemoteCalendarId, out var folderId))
                    continue;

                var rows = await MapiCalendarOperations.ReadAppointmentsAsync(session, folderId, tags, cancellationToken, Diagnostics).ConfigureAwait(false);

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var occurrences = 0;
                foreach (var row in rows.Where(r => r.IsAppointment))
                {
                    if (row.IsMeeting)
                    {
                        try
                        {
                            row.Attendees = await MapiCalendarOperations.ReadAttendeesAsync(session, folderId, row.MessageId, cancellationToken, Diagnostics).ConfigureAwait(false);
                        }
                        catch (MapiException ex)
                        {
                            Logger.Debug(ex, "MAPI calendar {Account}: attendees of 0x{Id:X16} not read.", Account.Address, row.MessageId);
                        }
                    }

                    // The master row goes first so its occurrences can link to it.
                    if (MapiCalendarExpander.Master(row) is { } master)
                    {
                        seen.Add(master.RemoteId.GetProviderRemoteEventId());
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(master, calendar, Account).ConfigureAwait(false);
                    }

                    foreach (var occurrence in MapiCalendarExpander.Expand(row, windowStartUtc, windowEndUtc, Diagnostics))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        seen.Add(occurrence.RemoteId.GetProviderRemoteEventId());
                        await ExchangeChangeProcessor.ManageCalendarEventAsync(occurrence, calendar, Account).ConfigureAwait(false);
                        occurrences++;
                    }
                }

                var removed = 0;
                var localEvents = await ExchangeChangeProcessor.GetCalendarItemsInRangeAsync(calendar, windowStartUtc, windowEndUtc).ConfigureAwait(false);
                foreach (var local in localEvents)
                {
                    if (!string.IsNullOrEmpty(local.RemoteEventId) && !seen.Contains(local.RemoteEventId.GetProviderRemoteEventId()))
                    {
                        await ExchangeChangeProcessor.DeleteCalendarItemAsync(local.Id).ConfigureAwait(false);
                        removed++;
                    }
                }

                // Masters are outside the range query; a series gone from the server takes its occurrences with it.
                foreach (var master in await ExchangeChangeProcessor.GetRecurringMastersAsync(calendar).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(master.RemoteEventId) && !seen.Contains(master.RemoteEventId.GetProviderRemoteEventId()))
                    {
                        await ExchangeChangeProcessor.DeleteCalendarItemAsync(master.Id).ConfigureAwait(false);
                        removed++;
                    }
                }

                Logger.Information("MAPI calendar {Account}/{Calendar}: {Rows} rows, {Occurrences} occurrences in window, {Removed} removed.", Account.Address, calendar.Name, rows.Count, occurrences, removed);
            }

            return CalendarSynchronizationResult.Empty;
        }
        catch (OperationCanceledException)
        {
            return CalendarSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "MAPI calendar synchronization failed for {Address}.", Account.Address);
            return CalendarSynchronizationResult.Failed(ex);
        }
    }

    /// <summary>
    /// Calendars are the IPF.Appointment folders of the mailbox, keyed by "mapi:" folder id. Rows that
    /// still carry an EWS id from before the transport flipped are re-keyed in place (the primary to the
    /// default calendar, others by name) so colours and sync preferences survive the switch.
    /// </summary>
    private async Task SynchronizeMapiCalendarsAsync(MapiSession session, CancellationToken cancellationToken)
    {
        var logon = session.Logon!;
        var defaultFolderId = await MapiCalendarOperations.FindCalendarFolderIdAsync(session, logon.InboxFolderId, cancellationToken).ConfigureAwait(false);
        var hierarchy = await MapiFolderOperations.ReadHierarchyAsync(session, logon.IpmSubtreeFolderId, cancellationToken).ConfigureAwait(false);

        var remote = new Dictionary<ulong, string>();
        foreach (var folder in hierarchy.Where(f => string.Equals(f.ContainerClass, PropertyTags.CalendarContainerClass, StringComparison.OrdinalIgnoreCase)))
            remote[folder.FolderId] = string.IsNullOrWhiteSpace(folder.DisplayName) ? "Calendar" : folder.DisplayName;
        if (defaultFolderId is { } primary && !remote.ContainsKey(primary))
            remote[primary] = "Calendar";

        var local = await ExchangeChangeProcessor.GetAccountCalendarsAsync(Account.Id).ConfigureAwait(false);

        // One-time re-key of EWS-addressed rows.
        foreach (var calendar in local.Where(c => !TryParseMailCopyId(c.RemoteCalendarId, out _)).ToList())
        {
            ulong? target = null;
            if (calendar.IsPrimary && defaultFolderId is { } primaryId)
            {
                target = primaryId;
            }
            else
            {
                // Otherwise by name, ignoring folders another local row has already claimed.
                var byName = remote.FirstOrDefault(r => string.Equals(r.Value, calendar.Name, StringComparison.OrdinalIgnoreCase)
                                                        && !local.Any(l => l.RemoteCalendarId == ToMailCopyId(r.Key))).Key;
                if (byName != 0)
                    target = byName;
            }

            if (target is { } folderId)
            {
                Logger.Information("MAPI calendar {Account}: re-keying '{Name}' from EWS id to 0x{Folder:X16}.", Account.Address, calendar.Name, folderId);
                calendar.RemoteCalendarId = ToMailCopyId(folderId);
                await ExchangeChangeProcessor.UpdateAccountCalendarAsync(calendar).ConfigureAwait(false);
            }
        }

        foreach (var calendar in local)
        {
            if (!TryParseMailCopyId(calendar.RemoteCalendarId, out var folderId) || !remote.ContainsKey(folderId))
                await ExchangeChangeProcessor.DeleteAccountCalendarAsync(calendar).ConfigureAwait(false);
        }

        foreach (var (folderId, name) in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remoteId = ToMailCopyId(folderId);
            var isPrimary = folderId == defaultFolderId;
            var existing = local.FirstOrDefault(c => c.RemoteCalendarId == remoteId);
            if (existing == null)
            {
                await ExchangeChangeProcessor.InsertAccountCalendarAsync(BuildAccountCalendar(remoteId, name, isPrimary)).ConfigureAwait(false);
            }
            else if (existing.Name != name || existing.IsPrimary != isPrimary)
            {
                existing.Name = name;
                existing.IsPrimary = isPrimary;
                await ExchangeChangeProcessor.UpdateAccountCalendarAsync(existing).ConfigureAwait(false);
            }
        }
    }

    // ---- Calendar writes ---------------------------------------------------------------------------

    /// <summary>
    /// An occurrence row's id is "mapi:{master}:{originalStartUtc}" (see MapiCalendarExpander); a
    /// single appointment's is just "mapi:{mid}". Returns the master id and the original start when
    /// the item is an occurrence.
    /// </summary>
    private static bool TryParseOccurrenceId(IAccountCalendar? calendar, CalendarItem item, out ulong folderId, out ulong masterId, out DateTime originalStartUtc)
    {
        folderId = 0; masterId = 0; originalStartUtc = default;
        if (!TryParseMailCopyId(calendar?.RemoteCalendarId, out folderId))
            return false;

        var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
        var parts = remoteId.Split(':');
        if (parts.Length != 3 || !TryParseMailCopyId(parts[0] + ":" + parts[1], out masterId))
            return false;

        return DateTime.TryParseExact(parts[2], "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out originalStartUtc);
    }

    private static (ulong FolderId, ulong MessageId) RequireAppointmentIds(IAccountCalendar? calendar, CalendarItem item)
    {
        if (!TryParseMailCopyId(calendar?.RemoteCalendarId, out var folderId))
            throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

        var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
        if (remoteId.Count(c => c == ':') > 1)
            throw new NotSupportedException("Changing one occurrence of a repeating series is not supported over MAPI yet; edit it in Outlook on the web.");
        if (!TryParseMailCopyId(remoteId, out var messageId))
            throw new InvalidOperationException("The event is not addressable over MAPI until the next calendar sync.");

        return (folderId, messageId);
    }

    private MapiCalendarOperations.AppointmentWrite ToAppointmentWrite(CalendarItem item, List<Reminder>? reminders, string? clientTrackingId, List<CalendarEventAttendee>? attendees = null)
    {
        var startUtc = item.StartDate;
        if (!string.IsNullOrEmpty(item.StartTimeZone))
        {
            try { startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(item.StartDate, DateTimeKind.Unspecified), TimeZoneInfo.FindSystemTimeZoneById(item.StartTimeZone)); }
            catch { startUtc = DateTime.SpecifyKind(item.StartDate, DateTimeKind.Utc); }
        }
        else
        {
            startUtc = DateTime.SpecifyKind(item.StartDate, DateTimeKind.Utc);
        }

        var write = new MapiCalendarOperations.AppointmentWrite
        {
            OrganizerName = Account.SenderName ?? Account.Name ?? Account.Address,
            OrganizerAddress = Account.Address,
            TimeZoneId = item.StartTimeZone,
            RecurrenceRule = RecurrenceRuleOf(item),
            Subject = item.Title,
            Body = item.Description,
            Location = item.Location,
            StartUtc = startUtc,
            EndUtc = startUtc.AddSeconds(item.DurationInSeconds),
            AllDay = item.IsAllDayEvent,
            BusyStatus = item.ShowAs switch
            {
                CalendarItemShowAs.Free => 0u,
                CalendarItemShowAs.Tentative => 1u,
                CalendarItemShowAs.OutOfOffice => 3u,
                CalendarItemShowAs.WorkingElsewhere => 4u,
                _ => 2u
            },
            Sensitivity = item.Visibility switch
            {
                CalendarItemVisibility.Private => 2u,
                CalendarItemVisibility.Confidential => 3u,
                _ => 0u
            },
            ReminderMinutes = reminders?.FirstOrDefault() is { } reminder ? (int)(reminder.DurationInSeconds / 60) : null,
            ClientTrackingId = clientTrackingId
        };

        foreach (var attendee in attendees ?? [])
        {
            if (attendee.IsOrganizer || string.IsNullOrWhiteSpace(attendee.Email)) continue;
            if (string.Equals(attendee.Email, Account.Address, StringComparison.OrdinalIgnoreCase)) continue;
            write.Attendees.Add(new MapiCalendarOperations.MeetingAttendee(attendee.Name, attendee.Email.Trim(), attendee.IsOptionalAttendee));
        }

        return write;
    }

    /// <summary>The RRULE line of the item's recurrence text (lines separated by the app's separator), or null.</summary>
    private static string? RecurrenceRuleOf(CalendarItem item)
        => item.Recurrence?
            .Split(Constants.CalendarEventRecurrenceRuleSeperator, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// After one occurrence of a meeting the account organizes was changed or removed, the attendees
    /// get the matching instance-level request or cancellation (MS-OXOCAL 3.1.4.4.3 / 3.1.4.6). Best
    /// effort: the local change stands if the send fails.
    /// </summary>
    private async Task NotifyOccurrenceChangeAsync(MapiSession session, ulong folderId, ulong masterId, MapiCalendarTags tags, DateTime originalStartUtc,
        MapiCalendarOperations.AppointmentWrite change, CalendarItem item, bool cancel)
    {
        if (!OrganizesItem(item))
            return;

        try
        {
            var master = await MapiCalendarOperations.ReadMeetingIdentityAsync(session, folderId, masterId, tags, CancellationToken.None, Diagnostics).ConfigureAwait(false);
            var attendees = (master.Attendees ?? []).Where(r => !r.IsOrganizer && !string.IsNullOrEmpty(r.SmtpAddress) && !string.Equals(r.SmtpAddress, Account.Address, StringComparison.OrdinalIgnoreCase)).ToList();
            if (attendees.Count == 0 || !GlobalObjectId.IsWellFormed(master.GlobalObjectId))
                return;

            var zone = Wino.Mapi.Calendar.TimeZoneDefinition.Resolve(change.TimeZoneId);
            var originalWall = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(originalStartUtc, DateTimeKind.Utc), zone);
            var now = DateTime.UtcNow;
            var message = cancel
                ? MapiCalendarOperations.BuildOccurrenceCancellation(tags, master, originalStartUtc, originalWall, master.Sequence, now)
                : MapiCalendarOperations.BuildOccurrenceRequest(tags, master, change, originalStartUtc, originalWall, master.Sequence, now);
            var logon = session.Logon!;
            await MapiMessageComposer.SendAsync(session, logon.OutboxFolderId, logon.SentItemsFolderId, message, CancellationToken.None, Diagnostics).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: {Kind} for occurrence {Start:u} of 0x{Id:X16} sent to {Attendees} attendees.", Account.Address, cancel ? "cancellation" : "update", originalStartUtc, masterId, attendees.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI calendar {Account}: attendees were not told about the occurrence change of 0x{Id:X16}.", Account.Address, masterId);
        }
    }

    /// <summary>The account organizes the item when it carries no organizer or names this account as one.</summary>
    private bool OrganizesItem(CalendarItem item)
        => string.IsNullOrEmpty(item?.OrganizerEmail) || string.Equals(item.OrganizerEmail, Account.Address, StringComparison.OrdinalIgnoreCase);

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var reminders = request.ComposeResult?.SelectedReminders;
        var calendar = request.AssignedCalendar;
        var hasAttendees = request.ComposeResult?.Attendees is { Count: > 0 };

        return MapiBundle(async session =>
        {
            if (!TryParseMailCopyId(calendar?.RemoteCalendarId, out var folderId))
                throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

            var tags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
            var write = ToAppointmentWrite(item, reminders, item.Id.ToString("N"), hasAttendees ? request.ComposeResult!.Attendees : null);
            var logon = session.Logon!;
            var id = write.IsMeeting
                ? await MapiCalendarOperations.CreateMeetingAsync(session, folderId, logon.OutboxFolderId, logon.SentItemsFolderId, tags, write, CancellationToken.None, Diagnostics).ConfigureAwait(false)
                : await MapiCalendarOperations.CreateAppointmentAsync(session, folderId, tags, write).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: created 0x{Id:X16} ({Kind}, {Attendees} attendees, recurring={Recurring}).", Account.Address, id, write.IsMeeting ? "meeting" : "appointment", write.Attendees.Count, write.IsRecurring);

            // Stamp the new id on the local row right away so the optimistic item is adopted rather than
            // duplicated by the resync (a series master reconciles by its "mapi:" prefix).
            await ExchangeChangeProcessor.PersistCreatedCalendarEventAsync(
                item,
                request.PreparedEvent.Attendees,
                request.PreparedEvent.Reminders,
                ToMailCopyId(id).WithClientTrackingId(item.Id)).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        var hasAttendees = request.Attendees is { Count: > 0 };

        return MapiBundle(async session =>
        {
            Diagnostics($"update {item.RemoteEventId}: StartDate {item.StartDate:s} ({item.StartDate.Kind}), zone {item.StartTimeZone ?? "-"}, duration {item.DurationInSeconds}s, all-day {item.IsAllDayEvent}");

            if (TryParseOccurrenceId(item.AssignedCalendar, item, out var seriesFolderId, out var masterId, out var originalStartUtc))
            {
                var seriesTags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
                var change = ToAppointmentWrite(item, null, null);
                await MapiOccurrenceOperations.ModifyOccurrenceAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, change, CancellationToken.None, Diagnostics).ConfigureAwait(false);
                await NotifyOccurrenceChangeAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, change, item, cancel: false).ConfigureAwait(false);
                return;
            }

            var (folderId, messageId) = RequireAppointmentIds(item.AssignedCalendar, item);
            var tags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
            var write = ToAppointmentWrite(item, null, null, hasAttendees ? request.Attendees : null);

            // Editing a series rewrites its pattern; the deleted and changed occurrences it already has
            // survive when the pattern itself did not change, as Outlook keeps them.
            if (write.IsRecurring)
            {
                try
                {
                    write.ExistingRecurrence = (await MapiOccurrenceOperations.ReadMasterAsync(session, folderId, messageId, tags, CancellationToken.None).ConfigureAwait(false)).Recurrence;
                }
                catch (MapiException ex)
                {
                    Logger.Debug(ex, "MAPI calendar {Account}: existing pattern of 0x{Id:X16} not read; exceptions will not be carried over.", Account.Address, messageId);
                }
            }

            // Only the organizer re-sends the request; an attendee editing their copy just saves it.
            if (write.IsMeeting && OrganizesItem(item))
            {
                var logon = session.Logon!;
                await MapiCalendarOperations.UpdateMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, tags, write, CancellationToken.None, Diagnostics).ConfigureAwait(false);
                Logger.Information("MAPI calendar {Account}: meeting 0x{Id:X16} updated and re-sent to {Attendees} attendees.", Account.Address, messageId, write.Attendees.Count);
            }
            else
            {
                await MapiCalendarOperations.UpdateAppointmentAsync(session, folderId, messageId, tags, write).ConfigureAwait(false);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request)
        => UpdateCalendarEvent(request);

    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request)
    {
        var item = request.Item;

        return MapiBundle(async session =>
        {
            if (TryParseOccurrenceId(item.AssignedCalendar, item, out var seriesFolderId, out var masterId, out var originalStartUtc))
            {
                var seriesTags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
                await MapiOccurrenceOperations.DeleteOccurrenceAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, CancellationToken.None, Diagnostics).ConfigureAwait(false);
                Logger.Information("MAPI calendar {Account}: occurrence {Start:u} of 0x{Id:X16} deleted.", Account.Address, originalStartUtc, masterId);
                await NotifyOccurrenceChangeAsync(session, seriesFolderId, masterId, seriesTags, originalStartUtc, ToAppointmentWrite(item, null, null), item, cancel: true).ConfigureAwait(false);
                return;
            }

            var (folderId, messageId) = RequireAppointmentIds(item.AssignedCalendar, item);
            var logon = session.Logon!;

            // A meeting the account organizes is cancelled (attendees told) rather than silently removed.
            if (OrganizesItem(item))
            {
                var tags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
                var cancelled = await MapiCalendarOperations.CancelMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, logon.DeletedItemsFolderId,
                    tags, Account.Address, CancellationToken.None, Diagnostics).ConfigureAwait(false);
                if (cancelled)
                    Logger.Information("MAPI calendar {Account}: meeting 0x{Id:X16} cancelled.", Account.Address, messageId);
                return;
            }

            await MapiMessageOperations.MoveAsync(session, folderId, logon.DeletedItemsFolderId, [messageId]).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Accept, request.Item, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Tentative, request.Item, request.ResponseMessage);

    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request)
        => RespondToMeeting(request, MapiCalendarOperations.MeetingResponse.Decline, request.Item, request.ResponseMessage);

    // A response to one occurrence of a series is sent for the series (the master's id is the part
    // before the occurrence suffix); per-occurrence responses are a later refinement.
    private List<IRequestBundle<EwsRequest>> RespondToMeeting(CalendarRequestBase request, MapiCalendarOperations.MeetingResponse response, CalendarItem item, string? comment)
    {
        return MapiBundle(async session =>
        {
            if (!TryParseMailCopyId(item.AssignedCalendar?.RemoteCalendarId, out var folderId))
                throw new InvalidOperationException("The calendar is not addressable over MAPI until the next calendar sync.");

            var remoteId = item.RemoteEventId?.GetProviderRemoteEventId() ?? string.Empty;
            var masterId = remoteId.Count(c => c == ':') > 1 ? remoteId.Substring(0, remoteId.IndexOf(':', remoteId.IndexOf(':') + 1)) : remoteId;
            if (!TryParseMailCopyId(masterId, out var messageId))
                throw new InvalidOperationException("The event is not addressable over MAPI until the next calendar sync.");

            var tags = await ResolveCalendarTagsAsync(session, CancellationToken.None).ConfigureAwait(false);
            var logon = session.Logon!;
            await MapiCalendarOperations.RespondToMeetingAsync(session, folderId, messageId, logon.OutboxFolderId, logon.SentItemsFolderId, logon.DeletedItemsFolderId,
                tags, response, comment, Account.SenderName ?? Account.Name ?? Account.Address, CancellationToken.None, Diagnostics).ConfigureAwait(false);
            Logger.Information("MAPI calendar {Account}: {Response} sent for 0x{Id:X16}.", Account.Address, response, messageId);
        }, request, request);
    }

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

        await _archiveSlot.CloseAsync().ConfigureAwait(false);
        await _publicFolderSlot.CloseAsync().ConfigureAwait(false);

        List<RemoteSessionSlot> contentSlots;
        lock (_publicContentStores)
            contentSlots = _publicContentStores.Values.Select(s => s.Slot).ToList();

        foreach (var slot in contentSlots)
            await slot.CloseAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------
    // Read-only remote stores: the online archive and the public folders. Each is another store the
    // user may open; Autodiscover names them, their own Autodiscover gives the routable MailStore URL,
    // and the logon is private (archive) or a public-store logon (public folders). Rows are transient,
    // as on EWS. Browsing a tree is a burst of calls, so one session is kept per store.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One kept session for a remote store. Same age, idle and fault rules as the mailbox session; calls
    /// on one slot are serialized, which browsing does not mind.
    /// </summary>
    private sealed class RemoteSessionSlot
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private MapiSession? _session;
        private DateTime _openedUtc;
        private DateTime _usedUtc;

        public sealed class Lease(RemoteSessionSlot slot, MapiSession? session) : IAsyncDisposable
        {
            public MapiSession? Session { get; } = session;

            public async ValueTask DisposeAsync()
            {
                try
                {
                    slot._usedUtc = DateTime.UtcNow;
                    if (Session is { Faulted: true })
                        await slot.DropAsync().ConfigureAwait(false);
                }
                finally
                {
                    slot._gate.Release();
                }
            }
        }

        public async Task<Lease> AcquireAsync(Func<CancellationToken, Task<MapiSession?>> open, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (_session is not null && (_session.Faulted || now - _openedUtc > SessionMaxAge || now - _usedUtc > SessionMaxIdle))
                    await DropAsync().ConfigureAwait(false);

                if (_session is null)
                {
                    _session = await open(cancellationToken).ConfigureAwait(false);
                    _openedUtc = now;
                }

                _usedUtc = now;
                return new Lease(this, _session);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        public async Task CloseAsync()
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                return;

            try
            {
                await DropAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task DropAsync()
        {
            var session = _session;
            _session = null;
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private readonly RemoteSessionSlot _archiveSlot = new();
    private readonly RemoteSessionSlot _publicFolderSlot = new();

    /// <summary>A session on the archive mailbox, or null when the account has none.</summary>
    private async Task<MapiSession?> OpenArchiveSessionAsync(CancellationToken cancellationToken)
    {
        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var primary = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(primary.ArchiveSmtpAddress))
            return null;

        if (_archiveEndpoint is null)
        {
            _archiveEndpoint = await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl, primary.ArchiveSmtpAddress, credential, cancellationToken).ConfigureAwait(false);
            Diagnostics($"archive mailbox {primary.ArchiveSmtpAddress} discovered");
        }

        var transport = new MapiHttpTransport(_archiveEndpoint.MailStoreUrl, credential, UserAgent, line => Logger.Debug("MAPI archive {Account} {Line}", Account.Address, line));
        return await MapiSession.OpenAsync(transport, primary.LegacyDn, cancellationToken, mailboxDn: _archiveEndpoint.LegacyDn).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<PublicFolderNode>> GetOnlineArchiveChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var lease = await _archiveSlot.AcquireAsync(OpenArchiveSessionAsync, cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            if (session is null)
                return null;

            var subtree = session.Logon!.IpmSubtreeFolderId;
            var parent = string.IsNullOrEmpty(parentFolderId) ? subtree : TryParseMailCopyId(parentFolderId, out var p) ? p : subtree;
            return await ReadRemoteChildrenAsync(session, parent, parentFolderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI archive {Account}: folder list failed; using EWS for this call.", Account.Address);
            return await base.GetOnlineArchiveChildrenAsync(parentFolderId, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<IReadOnlyList<MailCopy>> GetOnlineArchiveMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid))
            return await base.GetOnlineArchiveMailItemsAsync(folderId, skip, take, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var lease = await _archiveSlot.AcquireAsync(OpenArchiveSessionAsync, cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            if (session is null)
                return [];

            return await ReadRemoteMailAsync(session, fid, skip, take, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI archive {Account}: message list of 0x{Id:X16} failed.", Account.Address, fid);
            return [];
        }
    }

    public override async Task<byte[]> GetOnlineArchiveMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid) || !TryParseMailCopyId(itemId, out var mid))
            return await base.GetOnlineArchiveMailMimeAsync(folderId, itemId, cancellationToken).ConfigureAwait(false);

        await using var lease = await _archiveSlot.AcquireAsync(OpenArchiveSessionAsync, cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        if (session is null)
            return null;

        return await ReadRemoteMimeAsync(session, fid, mid, cancellationToken).ConfigureAwait(false);
    }

    // ---- Shared live reads for the archive and public folder trees (rows transient, never persisted) ----

    private async Task<IReadOnlyList<PublicFolderNode>> ReadRemoteChildrenAsync(MapiSession session, ulong parent, string parentFolderId, CancellationToken cancellationToken)
    {
        var folders = await MapiFolderOperations.ReadHierarchyAsync(session, parent, cancellationToken, Diagnostics, deep: false).ConfigureAwait(false);
        return folders
            .Where(f => !f.IsHidden)           // system folders that OWA keeps out of the tree
            .Select(f => new PublicFolderNode
            {
                Id = ToMailCopyId(f.FolderId),
                ParentId = parentFolderId,
                Name = string.IsNullOrWhiteSpace(f.DisplayName) ? Translator.RemoteFolders_Unnamed : f.DisplayName,
                Kind = PublicFolderClassifier.Classify(f.ContainerClass),
                HasChildren = f.HasSubfolders,
            })
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<MailCopy>> ReadRemoteMailAsync(MapiSession session, ulong folderId, int skip, int take, CancellationToken cancellationToken)
    {
        var pageSize = take <= 0 ? (int)InitialMessageDownloadCountPerFolder : take;
        var offset = Math.Max(0, skip);
        var rows = await MapiMessageOperations.ReadMessageListAsync(session, folderId, offset + pageSize, cancellationToken, Diagnostics).ConfigureAwait(false);
        return rows.Skip(offset).Select(r =>
        {
            var copy = MapToMailCopy(r, Guid.Empty, inDraftsFolder: false);
            copy.FileId = RemoteItemFileId(copy.Id);
            copy.IsRead = true;                // read state is not per-user-meaningful in a shared store
            copy.AssignedAccount = Account;
            return copy;
        }).ToList();
    }

    private async Task<byte[]> ReadRemoteMimeAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken)
    {
        var info = await MapiMessageOperations.ReadMessageInfoAsync(session, folderId, messageId, cancellationToken).ConfigureAwait(false);
        var content = await MapiMessageOperations.ReadMessageContentAsync(session, folderId, messageId, cancellationToken, Diagnostics).ConfigureAwait(false);
        var mime = MapiMimeAssembler.Build(MapToMailCopy(info, Guid.Empty, inDraftsFolder: false), content, info.DisplayTo);
        using var buffer = new System.IO.MemoryStream();
        mime.WriteTo(buffer);
        return buffer.ToArray();
    }

    // ------------------------------------------------------------------------------------------------
    // Public folders over MAPI: Autodiscover names the public folder mailbox, whose own Autodiscover
    // gives the routable endpoint; the logon is a public-store logon and the tree is the public IPM
    // subtree, expanded one level at a time. Calendar and contact folders read with the same
    // operations the mailbox uses, against named property ids resolved on the public store.
    // ------------------------------------------------------------------------------------------------

    /// <summary>A public-store session, or null when Autodiscover names no public folder mailbox.</summary>
    private async Task<MapiSession?> OpenPublicFolderSessionAsync(CancellationToken cancellationToken)
    {
        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var primary = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(primary.PublicFolderSmtpAddress))
            return null;

        if (_publicFolderEndpoint is null)
        {
            _publicFolderEndpoint = await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl, primary.PublicFolderSmtpAddress, credential, cancellationToken).ConfigureAwait(false);
            Diagnostics($"public folder mailbox {primary.PublicFolderSmtpAddress} discovered");
        }

        var transport = new MapiHttpTransport(_publicFolderEndpoint.MailStoreUrl, credential, UserAgent, line => Logger.Debug("MAPI public {Account} {Line}", Account.Address, line));
        return await MapiSession.OpenAsync(transport, primary.LegacyDn, cancellationToken, publicStore: true).ConfigureAwait(false);
    }

    // Content of a ghosted public folder lives in another public folder mailbox, which the replica list
    // names by legacyDN. Autodiscover accepts a legacyDN as the address, so the content mailbox gets its
    // own endpoint and public-store session, kept per DN; a folder remembers where its content was found
    // so the message body read goes straight there.
    private readonly Dictionary<string, (MapiEndpointInfo Endpoint, RemoteSessionSlot Slot)> _publicContentStores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where a ghosted folder's content was found: the replica, and the folder's id in that store.</summary>
    private readonly Dictionary<ulong, (string Replica, ulong ContentFolderId)> _publicContentByFolder = new();

    /// <summary>
    /// Folder ids are per store (the ReplId half is this logon's mapping), so a folder id from the
    /// hierarchy mailbox means nothing in the content mailbox. The long-term id (ReplGuid + counter) is
    /// the same everywhere: convert on the way out, and back on the way in.
    /// </summary>
    private static async Task<ulong> TranslateFolderIdAsync(MapiSession from, MapiSession to, ulong folderId, CancellationToken cancellationToken)
    {
        var (rops, _) = await from.ExecuteAsync(RopIds.BuildLongTermIdFromId(0, folderId), [from.LogonHandle], cancellationToken).ConfigureAwait(false);
        var longTermId = RopIds.ParseLongTermIdFromId(new RopReader(rops));
        (rops, _) = await to.ExecuteAsync(RopIds.BuildIdFromLongTermId(0, longTermId), [to.LogonHandle], cancellationToken).ConfigureAwait(false);
        return RopIds.ParseIdFromLongTermId(new RopReader(rops));
    }

    private async Task<RemoteSessionSlot.Lease> AcquirePublicContentSessionAsync(string replicaDn, CancellationToken cancellationToken)
    {
        (MapiEndpointInfo Endpoint, RemoteSessionSlot Slot) store;
        lock (_publicContentStores)
            _publicContentStores.TryGetValue(replicaDn, out store);

        var credential = await ResolveCredentialAsync().ConfigureAwait(false);
        var primary = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        if (store.Endpoint is null)
        {
            var address = ReplicaToAddress(replicaDn);
            var endpoint = await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl, address, credential, cancellationToken).ConfigureAwait(false);
            Diagnostics($"public folder content mailbox {address} discovered");
            store = (endpoint, new RemoteSessionSlot());
            lock (_publicContentStores)
                _publicContentStores[replicaDn] = store;
        }

        return await store.Slot.AcquireAsync(async ct =>
        {
            var transport = new MapiHttpTransport(store.Endpoint.MailStoreUrl, credential, UserAgent, line => Logger.Debug("MAPI public-content {Account} {Line}", Account.Address, line));
            return await MapiSession.OpenAsync(transport, primary.LegacyDn, ct, publicStore: true).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The address Autodiscover takes for a replica. The list names the content mailbox as a server DN,
    /// ".../cn=Configuration/cn=Servers/cn={mailbox guid}@{domain}/cn=Microsoft Public MDB", and the
    /// guid@domain segment is the mailbox's routing address (the same form the archive uses). Anything
    /// else is passed through as-is.
    /// </summary>
    internal static string ReplicaToAddress(string replica)
    {
        var match = System.Text.RegularExpressions.Regex.Match(replica, @"/cn=Servers/cn=([^/]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : replica;
    }

    /// <summary>
    /// Runs a content read on the public folder mailbox, following the replica list once when the folder
    /// is ghosted elsewhere. The read gets the session to use and the folder's id in that store.
    /// </summary>
    private async Task<T> ReadPublicContentAsync<T>(ulong folderId, Func<MapiSession, ulong, Task<T>> read, CancellationToken cancellationToken)
    {
        (string Replica, ulong ContentFolderId) known;
        lock (_publicContentByFolder)
            _publicContentByFolder.TryGetValue(folderId, out known);

        if (known.Replica is not null)
        {
            await using var direct = await AcquirePublicContentSessionAsync(known.Replica, cancellationToken).ConfigureAwait(false);
            return await read(direct.Session!, known.ContentFolderId).ConfigureAwait(false);
        }

        await using var lease = await _publicFolderSlot.AcquireAsync(OpenPublicFolderSessionAsync, cancellationToken).ConfigureAwait(false);
        if (lease.Session is null)
            throw new InvalidOperationException("Autodiscover named no public folder mailbox.");

        try
        {
            return await read(lease.Session, folderId).ConfigureAwait(false);
        }
        catch (MapiGhostedFolderException ghosted)
        {
            var replica = ghosted.Replicas[0];
            Logger.Information("MAPI public folders {Account}: folder 0x{Id:X16} content is in {Replica}; following.", Account.Address, folderId, replica);
            await using var content = await AcquirePublicContentSessionAsync(replica, cancellationToken).ConfigureAwait(false);
            var contentFolderId = await TranslateFolderIdAsync(lease.Session, content.Session!, folderId, cancellationToken).ConfigureAwait(false);
            Diagnostics($"public folder 0x{folderId:X16} is 0x{contentFolderId:X16} in its content mailbox");
            var result = await read(content.Session!, contentFolderId).ConfigureAwait(false);
            lock (_publicContentByFolder)
                _publicContentByFolder[folderId] = (replica, contentFolderId);
            return result;
        }
    }

    public override async Task<IReadOnlyList<PublicFolderNode>> GetPublicFolderChildrenAsync(string parentFolderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var lease = await _publicFolderSlot.AcquireAsync(OpenPublicFolderSessionAsync, cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            if (session is null)
                return await base.GetPublicFolderChildrenAsync(parentFolderId, cancellationToken).ConfigureAwait(false);

            var root = session.Logon!.PublicIpmSubtreeFolderId;
            var parent = string.IsNullOrEmpty(parentFolderId) ? root : TryParseMailCopyId(parentFolderId, out var p) ? p : root;
            return await ReadRemoteChildrenAsync(session, parent, parentFolderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI public folders {Account}: folder list failed; using EWS for this call.", Account.Address);
            return await base.GetPublicFolderChildrenAsync(parentFolderId, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<IReadOnlyList<MailCopy>> GetPublicFolderMailItemsAsync(string folderId, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid))
            return await base.GetPublicFolderMailItemsAsync(folderId, skip, take, cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadPublicContentAsync(fid, (session, folder) => ReadRemoteMailAsync(session, folder, skip, take, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI public folders {Account}: message list of 0x{Id:X16} failed.", Account.Address, fid);
            return [];
        }
    }

    public override async Task<byte[]> GetPublicFolderMailMimeAsync(string folderId, string itemId, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid) || !TryParseMailCopyId(itemId, out var mid))
            return await base.GetPublicFolderMailMimeAsync(folderId, itemId, cancellationToken).ConfigureAwait(false);

        return await ReadPublicContentAsync(fid, (session, folder) => ReadRemoteMimeAsync(session, folder, mid, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<CalendarItem>> GetPublicFolderAppointmentsAsync(string folderId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid))
            return await base.GetPublicFolderAppointmentsAsync(folderId, startUtc, endUtc, cancellationToken).ConfigureAwait(false);

        try
        {
            var rows = await ReadPublicContentAsync(fid, async (session, folder) =>
            {
                // Named property ids are per store: resolved on the store that answers, not from the mailbox's cache.
                var tags = await MapiCalendarOperations.ResolveTagsAsync(session, cancellationToken, Diagnostics).ConfigureAwait(false);
                return await MapiCalendarOperations.ReadAppointmentsAsync(session, folder, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            var items = new List<CalendarItem>();
            foreach (var row in rows.Where(r => r.IsAppointment))
            {
                foreach (var occurrence in MapiCalendarExpander.Expand(row, startUtc, endUtc, Diagnostics))
                    items.Add(MapRemoteAppointment(occurrence));
            }

            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI public folders {Account}: appointments of 0x{Id:X16} failed.", Account.Address, fid);
            return [];
        }
    }

    public override async Task<IReadOnlyList<PublicFolderContact>> GetPublicFolderContactsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (!TryParseMailCopyId(folderId, out var fid))
            return await base.GetPublicFolderContactsAsync(folderId, cancellationToken).ConfigureAwait(false);

        try
        {
            var rows = await ReadPublicContentAsync(fid, async (session, folder) =>
            {
                var tags = await MapiContactOperations.ResolveTagsAsync(session, cancellationToken).ConfigureAwait(false);
                return await MapiContactOperations.ReadContactsAsync(session, folder, tags, cancellationToken, Diagnostics).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return rows
                .Where(c => c.IsContact)
                .Select(c => new PublicFolderContact
                {
                    RemoteId = ToMailCopyId(c.MessageId),
                    DisplayName = c.DisplayName,
                    Address = c.Email1 ?? c.Email2 ?? c.Email3,
                    Company = c.Company,
                    Title = c.JobTitle,
                    BusinessPhone = c.BusinessPhone,
                    HomePhone = c.HomePhone,
                    MobilePhone = c.MobilePhone,
                    BusinessFax = c.BusinessFax,
                    StreetAddress = c.WorkStreet,
                    Notes = c.Notes,
                })
                .OrderBy(c => c.DisplayName ?? c.Address, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI public folders {Account}: contacts of 0x{Id:X16} failed.", Account.Address, fid);
            return [];
        }
    }
}
