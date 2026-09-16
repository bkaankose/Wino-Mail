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
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Exchange;
using Wino.Mapi;
using Wino.Mapi.Calendar;
using Wino.Mapi.Rops;
using Wino.Mapi.Transport;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// The Exchange synchronizer over native MAPI/HTTP. It derives from the EWS synchronizer and replaces
/// every mail surface: folder hierarchy, message list, message read incl. attachments, read/flag/move/
/// delete/junk, drafts and native send, and incremental sync over ICS. Servers that do not advertise
/// MAPI/HTTP are recorded on the account and the next synchronizer build lands on the EWS class.
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
    /// Junk is a move to or from the target folder. The server's Blocked Senders list lives in the junk
    /// rule's condition; editing it arrives with the junk-list surface, so for now the move stands alone.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        return Move(new BatchMoveRequest(requests.Select(r => new MoveRequest(r.Item, r.FromFolder, r.TargetFolder))));
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

            if (options?.Type == CalendarSynchronizationType.CalendarMetadata)
                return CalendarSynchronizationResult.Empty;

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
    }
}
