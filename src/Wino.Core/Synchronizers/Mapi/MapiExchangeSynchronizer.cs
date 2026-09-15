#nullable enable
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
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Exchange;
using Wino.Mapi;
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
    private static readonly ILogger Logger = Log.ForContext<MapiExchangeSynchronizer>();

    private const string MailCopyIdPrefix = "mapi:";
    private const string UserAgent = "WinoMail/MAPI";

    private MapiEndpointInfo? _endpoint;

    public MapiExchangeSynchronizer(MailAccount account,
                                    IExchangeAuthenticator exchangeAuthenticator,
                                    IExchangeChangeProcessor exchangeChangeProcessor,
                                    IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory)
        : base(account, exchangeAuthenticator, exchangeChangeProcessor, errorHandlerFactory)
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
