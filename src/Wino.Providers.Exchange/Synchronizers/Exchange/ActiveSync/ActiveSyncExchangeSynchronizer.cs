using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Messaging;
using ITransferProgress = MailKit.ITransferProgress;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Extensions;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Providers.Exchange.Interfaces;
using Wino.Providers.Exchange.Requests.Bundles;
using Wino.Core.Requests.Calendar;
using Wino.Core.Requests.Contact;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Tasks;
using Wino.Core.Services;
using Wino.Services;
using Wino.Core.Synchronizers;
using Wino.Services.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed partial class ActiveSyncExchangeSynchronizer : WinoSynchronizer<EwsRequest, MimeMessage, CalendarItem, AccountContact>
{
    private readonly EasSynchronizationEngine _engine;
    private readonly IMailService _mail;
    private readonly IFolderService _folders;
    private readonly IContactService _contacts;
    private readonly ITaskService _tasks;
    private readonly ICalendarService _calendars;
    private readonly IMimeFileService _mime;
    private readonly IPictureStorageService _pictures;
    private readonly ICalDavChangeProcessor _changeProcessor;
    private readonly LocalContactSynchronizer _localContacts;
    private readonly LocalTaskSynchronizer _localTasks;
    private readonly LocalCalendarOperationHandler _localCalendar;
    private readonly IExchangeSynchronizerErrorHandlerFactory _errorHandlerFactory;
    private readonly IAccountService _accounts;

    internal ActiveSyncExchangeSynchronizer(MailAccount account, IMessenger messenger, RequestUiChangeCoordinator uiChanges,
        IDatabaseService database, IAccountService accounts,
        IFolderService folders, IMailService mail, ICalendarService calendars, IContactService contacts, ITaskService tasks,
        IMimeFileService mime, IPictureStorageService pictures, ICalDavChangeProcessor changeProcessor, string applicationDataFolder,
        IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory, EasClient? client = null)
        : base(account, messenger, uiChanges)
    {
        _errorHandlerFactory = errorHandlerFactory;
        _accounts = accounts;
        _engine = new EasSynchronizationEngine(account, database, accounts, folders, mail, calendars, contacts, tasks, pictures,
            () => Messenger.Send(new AccountFolderConfigurationUpdated(account.Id)), ReportCollectionFailure, ReportItemFailure, client);
        _mail = mail; _folders = folders; _contacts = contacts; _tasks = tasks; _calendars = calendars; _mime = mime;
        _pictures = pictures;
        _changeProcessor = changeProcessor;
        _localContacts = new LocalContactSynchronizer(contacts, pictures); _localTasks = new LocalTaskSynchronizer(tasks);
        _localCalendar = new LocalCalendarOperationHandler(account, changeProcessor, calendars, applicationDataFolder, "local");
    }

    private const int ProfilePictureMaximumBytes = 100 * 1024;

    /// <summary>
    /// The display name from Settings UserInformation (14.1 and later) and the directory photo from ResolveRecipients.
    /// What the server does not say keeps the stored value: an older server names no display name, and a failed photo
    /// lookup leaves the picture alone.
    /// </summary>
    public override async Task<ProfileInformation> GetProfileInformationAsync()
    {
        var senderName = Account.SenderName;
        try
        {
            var information = EasUserInformation.Parse(await _engine.GetUserInformationAsync(CancellationToken.None).ConfigureAwait(false), Account.Address);
            if (!string.IsNullOrWhiteSpace(information.DisplayName))
                senderName = information.DisplayName;
        }
        catch (EasCommandException exception)
        {
            Logger.Warning(exception, "The ActiveSync server answered no user information for {Account}; the profile stays as it is.", Account.Name);
        }

        // ResolveRecipients pictures need 14.1; below it the stored picture stays.
        ProfilePictureFetchResult? picture = null;
        if (Version.Parse(await _engine.GetProtocolVersionAsync(CancellationToken.None).ConfigureAwait(false)) >= new Version(14, 1))
        {
            try
            {
                picture = EasUserInformation.ReadPicture(await _engine.ResolveRecipientPictureAsync(Account.Address, ProfilePictureMaximumBytes, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or AuthenticationAttentionException))
            {
                Logger.Debug(exception, "The ActiveSync directory photo of {Account} could not be read.", Account.Name);
                picture = ProfilePictureFetchResult.FetchFailed;
            }
        }

        return new ProfileInformation(senderName, picture, null);
    }

    /// <summary>
    /// The mailbox's SMTP addresses from Settings UserInformation. The account address stays the root alias, as on EWS;
    /// the others become aliases discovered on the server.
    /// </summary>
    protected override async Task SynchronizeAliasesAsync()
    {
        var information = EasUserInformation.Parse(await _engine.GetUserInformationAsync(CancellationToken.None).ConfigureAwait(false), Account.Address);
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in information.Addresses.Prepend(Account.Address))
        {
            if (string.IsNullOrWhiteSpace(address) || aliases.ContainsKey(address))
                continue;

            var isAccountAddress = address.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);
            aliases[address] = new RemoteAccountAlias
            {
                AliasAddress = address,
                ReplyToAddress = address,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = true,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Confirmed
            };
        }

        await _accounts.UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList()).ConfigureAwait(false);
    }

    protected override bool SerializeMailOperations => true;
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 100;

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(MimeMessage message, MailItemFolder folder, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("An EAS message requires its server identifier and application data.");

    /// <summary>The negotiated MS-ASProtocolVersion (for example 16.1); server drafts and meeting mail need 16.0.</summary>
    internal Task<string> GetProtocolVersionAsync(CancellationToken cancellationToken = default) => _engine.GetProtocolVersionAsync(cancellationToken);

    /// <summary>
    /// A pass that fails after some folders were stored still reports the messages it stored, as the EWS synchronizer
    /// does, so they are announced.
    /// </summary>
    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var downloaded = new List<MailCopy>();
        try
        {
            if (options.Type == MailSynchronizationType.FoldersOnly)
            {
                await _engine.SynchronizeFolderMetadataAsync(cancellationToken).ConfigureAwait(false);
                return MailSynchronizationResult.Completed(downloaded);
            }
            await _engine.SynchronizeMailAsync(options, downloaded, cancellationToken).ConfigureAwait(false);
            return MailSynchronizationResult.Completed(downloaded);
        }
        catch (EasResyncRequiredException exception)
        {
            Logger.Information(exception, "The ActiveSync data of {Account} was discarded; synchronizing it again.", Account.Name);
            Messenger.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions { AccountId = Account.Id, Type = MailSynchronizationType.FullFolders }));
            return MailSynchronizationResult.Completed(new List<MailCopy>());
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or AuthenticationAttentionException))
        {
            var error = new SynchronizerErrorContext
            {
                Account = Account,
                ErrorMessage = exception.Message,
                Exception = exception,
                OperationType = "ActiveSyncMailSync"
            };
            await _errorHandlerFactory.HandleErrorAsync(error).ConfigureAwait(false);
            CaptureSynchronizationIssue(error);
            Logger.Error(exception, "EAS mail synchronization failed for {Account}.", Account.Name);

            // A throttled pass is not a failure: the server asked to come back later, as EWS treats ServerBusy.
            if (error.Category == SynchronizerErrorCategory.RateLimit)
                return MailSynchronizationResult.Completed(downloaded);
            var failed = MailSynchronizationResult.Failed(exception);
            failed.DownloadedMessages = downloaded;
            return failed;
        }
    }

    private void ReportCollectionFailure(EasCollection collection, Exception exception)
    {
        Logger.Error(exception, "EAS collection {Collection} failed for {Account}; the other collections continue.", collection.Name, Account.Name);
        CaptureSynchronizationIssue(new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            FolderId = collection.IsMail ? collection.Id : null,
            FolderName = collection.Name,
            OperationType = "ActiveSyncCollectionSync"
        });
    }

    private void ReportItemFailure(EasCollection collection, string remoteId, Exception exception)
    {
        Logger.Warning(exception, "EAS item {RemoteId} in {Collection} could not be applied for {Account}; it is skipped.", remoteId, collection.Name, Account.Name);
        CaptureSynchronizationIssue(new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            FolderId = collection.IsMail ? collection.Id : null,
            FolderName = collection.Name,
            OperationType = "ActiveSyncItemSync"
        });
    }

    protected override async Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Calendar", cancellationToken).ConfigureAwait(false);
        return CalendarSynchronizationResult.Empty;
    }

    protected override async Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.ContactIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Contacts", cancellationToken).ConfigureAwait(false);
        return ContactSynchronizationResult.Empty;
    }

    protected override async Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        if (Account.TaskIntegrationSource == AccountIntegrationSource.Provider)
            await _engine.SynchronizeAsync("Tasks", cancellationToken).ConfigureAwait(false);
        return TaskSynchronizationResult.Empty;
    }

    /// <summary>
    /// Runs each bundle on its own, as the EWS synchronizer does: a failed bundle goes through the error handlers and
    /// is reverted, the others still run, and the failures are reported together. Cancellation reverts the bundle
    /// that was running and every later one.
    /// </summary>
    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> requests, CancellationToken cancellationToken = default)
    {
        if (requests == null || requests.Count == 0)
            return;

        ApplyOptimisticUiChanges(requests);
        var errors = new List<string>();
        for (var index = 0; index < requests.Count; index++)
        {
            var bundle = requests[index];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await bundle.NativeRequest.IntegratorTask(bundle.Request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                for (var later = index; later < requests.Count; later++)
                    UiChanges.RevertBundle(requests[later]);
                throw;
            }
            catch (Exception exception)
            {
                await HandleFailedRequestAsync(bundle, exception, errors).ConfigureAwait(false);
            }
            finally
            {
                UiChanges.CompleteRequests(new[] { bundle.Request });
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private async Task HandleFailedRequestAsync(IRequestBundle<EwsRequest> bundle, Exception exception, List<string> errors)
    {
        var error = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = IsEntityNotFound(exception, bundle.UIChangeRequest),
            OperationType = "ActiveSyncExecuteRequest"
        };

        var handled = await HandleRequestErrorAsync(_errorHandlerFactory, error).ConfigureAwait(false);

        // Handlers own the non-transient errors they handled.
        if (!handled || error.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(error);
            UiChanges.RevertBundle(bundle);
            Logger.Error(exception, "EAS request execution failed for {Account}.", Account.Name);
            errors.Add(exception.Message);
        }
    }

    /// <summary>
    /// Whether an operation on an existing item failed because the item or its folder is gone. ActiveSync status 8 is
    /// not one: the engine resolves it by synchronizing, as MS-ASCMD asks, and the server's Delete removes the item.
    /// </summary>
    private static bool IsEntityNotFound(Exception exception, IUIChangeRequest? request)
    {
        if (request is not (BatchDeleteRequest or BatchMoveRequest or BatchChangeFlagRequest or BatchMarkReadRequest or BatchArchiveRequest
            or DeleteRequest or MoveRequest or ChangeFlagRequest or MarkReadRequest))
            return false;

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is SynchronizerEntityNotFoundException)
                return true;
        }
        return false;
    }

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests) => Bundle(token => MutateMailAsync(requests,
        request => request.Item,
        (request, id) => EasMutation.Change(id, new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(EasDataMapper.Email + "Read", request.IsRead ? "1" : "0"))),
        request => _mail.ChangeReadStatusAsync(request.Item.Id, request.IsRead), deletesAsMoves: true, token), requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests) => Bundle(token => MutateMailAsync(requests,
        request => request.Item,
        (request, id) => EasMutation.Change(id, EasDataMapper.WriteFlag(request.IsFlagged)),
        request => _mail.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged), deletesAsMoves: true, token), requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests) => Bundle(async token =>
    {
        var moves = new List<EasMove>();
        foreach (var request in requests)
        {
            var (folder, id) = await RemoteMailAsync(request.Item).ConfigureAwait(false);
            moves.Add(new EasMove(folder.RemoteFolderId, id, request.ToFolder.RemoteFolderId));
        }

        var failures = (await _engine.MoveItemsAsync(moves, token).ConfigureAwait(false))
            .Where(result => !result.Succeeded && !result.IsGone).Select(result => result.Status).ToList();
        if (failures.Count > 0)
            throw new EasCommandException("MoveItems", string.Join(", ", failures.Distinct()) + " for " + failures.Count + " item(s)");
    }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest requests)
        => Move(new BatchMoveRequest(requests.Select(request => new MoveRequest(request.Item, request.FromFolder, request.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
        => Move(new BatchMoveRequest(requests.Select(request => new MoveRequest(request.Item, request.FromFolder, request.TargetFolder))));

    // A delete request is a permanent delete; moving to Deleted Items arrives as a move.
    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests) => Bundle(token => MutateMailAsync(requests,
        request => request.Item,
        (_, id) => EasMutation.Delete(id),
        request => _mail.DeleteMailAsync(Account.Id, request.Item.Id), deletesAsMoves: false, token,
        request => _mail.DiscardLocalDraftAsync(Account.Id, request.Item.UniqueId)), requests.FirstOrDefault(), requests);

    /// <summary>
    /// Sends the requests as one batched Sync per folder and stores each result as the server answers it. An item the
    /// server no longer has was removed by the synchronization that followed; the other failed items fail the bundle
    /// after the rest are stored.
    /// Local drafts, and drafts kept on a server without drafts, never reached the server and change only locally.
    /// </summary>
    private async Task MutateMailAsync<TRequest>(IEnumerable<TRequest> requests, Func<TRequest, MailCopy> itemOf, Func<TRequest, string, EasMutation> mutationOf,
        Func<TRequest, Task> commit, bool deletesAsMoves, CancellationToken cancellationToken, Func<TRequest, Task>? commitLocalDraft = null)
    {
        var remote = new List<(TRequest Request, MailItemFolder Folder, string RemoteId)>();
        foreach (var request in requests)
        {
            var item = itemOf(request);
            if (item.IsLocalDraft)
            {
                await (commitLocalDraft ?? commit)(request).ConfigureAwait(false);
                continue;
            }
            if (!TryRemoteMail(item, out _))
            {
                await commit(request).ConfigureAwait(false);
                continue;
            }
            var (folder, remoteId) = await RemoteMailAsync(item).ConfigureAwait(false);
            remote.Add((request, folder, remoteId));
        }

        var failures = new List<string>();
        foreach (var group in remote.GroupBy(entry => entry.Folder.Id))
        {
            var entries = group.ToList();
            var results = await _engine.MutateItemsAsync(entries[0].Folder.RemoteFolderId,
                entries.Select(entry => mutationOf(entry.Request, entry.RemoteId)).ToList(), deletesAsMoves, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < entries.Count; index++)
            {
                var result = results[index];
                if (result.Succeeded)
                    await commit(entries[index].Request).ConfigureAwait(false);
                else if (result.IsGone)
                    Logger.Information("EAS item {RemoteId} is no longer on the server; the synchronization removed it.", entries[index].RemoteId);
                else
                    failures.Add(result.Status);
            }
        }

        if (failures.Contains("7"))
            throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncChangeConflict);
        if (failures.Count > 0)
            throw new EasCommandException("Sync", string.Join(", ", failures.Distinct()) + " for " + failures.Count + " item(s)");
    }

    /// <summary>
    /// Deletes exactly the messages the request lists, as the EWS synchronizer does, in batched Sync Deletes. Messages
    /// the user never saw (not yet synchronized, or outside the synchronization range) stay on the server.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(mail => new DeleteRequest(mail))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(mail => new MarkReadRequest(mail, true))));

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderCreate", string.Empty, "0", request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderCreate", string.Empty, request.Folder.RemoteFolderId, request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderUpdate", request.Folder.RemoteFolderId, request.Folder.ParentRemoteFolderId, request.NewFolderName, 12, token).ConfigureAwait(false), request, request);

    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request) => Bundle(async token =>
        await _engine.ChangeFolderAsync("FolderDelete", request.Folder.RemoteFolderId, string.Empty, string.Empty, 12, token).ConfigureAwait(false), request, request);

    /// <summary>
    /// On 16.x the draft is added to the server Drafts folder and the local row takes the server id, as the EWS
    /// synchronizer does. Older servers keep no drafts: the local copy becomes the draft of record, as on POP3.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request) => Bundle(async token =>
    {
        var preparation = request.DraftPreperationRequest;
        var localDraft = preparation.CreatedLocalDraftCopy;
        if (!await _engine.SupportsServerDraftsAsync(token).ConfigureAwait(false))
        {
            if (!await _mail.MapLocalDraftAsync(Account.Id, localDraft.UniqueId, localDraft.Id, localDraft.UniqueId.ToString(), localDraft.ThreadId).ConfigureAwait(false))
                throw new InvalidOperationException("The local ActiveSync draft could not be prepared for sending.");
            return;
        }

        var folder = await DraftsFolderAsync(localDraft).ConfigureAwait(false);
        var (serverId, threadId) = await AddServerDraftAsync(folder, localDraft.UniqueId, preparation.CreatedLocalDraftMimeMessage, token).ConfigureAwait(false);
        var key = EasDataMapper.Key(Account.Id, folder.RemoteFolderId, serverId);
        if (!await _mail.MapLocalDraftAsync(Account.Id, localDraft.UniqueId, key, key, threadId ?? localDraft.ThreadId).ConfigureAwait(false))
        {
            // The local draft was discarded while the Add was in flight; the server copy goes so the next sync cannot bring it back.
            await _engine.DeleteSentDraftAsync(folder.RemoteFolderId, serverId, CancellationToken.None).ConfigureAwait(false);
        }
    }, request, request);

    /// <summary>
    /// A later save of a server draft: a Change of the draft's fields and attachments, so the draft keeps its id.
    /// Without server drafts the save is local only. Either way the identity stays.
    /// </summary>
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default)
    {
        if (!TryRemoteMail(draft, out var serverId) || !await _engine.SupportsServerDraftsAsync(cancellationToken).ConfigureAwait(false))
            return DraftUpdateIdentity.From(draft);

        var folder = await DraftsFolderAsync(draft).ConfigureAwait(false);
        using var mime = snapshot.OpenMime();
        await _engine.UpdateServerDraftAsync(folder.RemoteFolderId, serverId, mime, draft.IsRead, draft.IsFlagged, cancellationToken).ConfigureAwait(false);
        return DraftUpdateIdentity.From(draft);
    }

    /// <summary>
    /// Sends the composed MIME with SendMail, which keeps a copy in Sent Items, then deletes the server draft for good.
    /// The ClientId is derived from the draft and the exact message, so sending the same version again after a lost
    /// response is recognized as a duplicate, while an edited version is a new message. Once the server accepted the
    /// message the draft deletion is recorded and retried until it succeeds; reporting a failure after that point would
    /// bring the draft back and invite a second send.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request) => Bundle(async token =>
    {
        var preparation = request.Request;
        var draft = preparation.MailItem;
        var outgoing = MailKitSmtpTransport.CreateSmtpMessage(preparation.Mime);
        using (var stream = new MemoryStream())
        {
            await outgoing.WriteToAsync(stream, token).ConfigureAwait(false);
            var bytes = stream.ToArray();
            await _engine.SendAsync(bytes, SendClientId(draft.UniqueId, bytes), token).ConfigureAwait(false);
        }

        if (!draft.IsLocalDraft && TryRemoteMail(draft, out var draftId))
        {
            var folder = await DraftsFolderAsync(draft).ConfigureAwait(false);
            await _engine.DeleteSentDraftAsync(folder.RemoteFolderId, draftId, CancellationToken.None).ConfigureAwait(false);
        }

        try { await _mail.DeleteMailAsync(Account.Id, draft.Id).ConfigureAwait(false); }
        catch (Exception exception) { Logger.Error(exception, "EAS accepted message {MailId}, but removing the local draft failed.", draft.Id); }
    }, request, request);

    /// <summary>The SendMail ClientId (at most 40 characters): a hash of the draft id and the message as sent.</summary>
    private static string SendClientId(Guid draftId, byte[] message)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(draftId.ToByteArray());
        hash.AppendData(message);
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 20);
    }

    /// <summary>
    /// Adds a draft as MIME, which keeps every header and part. A refusal is the request's failure. The ClientId is
    /// the draft's id, so an addition answered in a request sent again after its response was lost is found again.
    /// </summary>
    private async Task<(string ServerId, string? ThreadId)> AddServerDraftAsync(MailItemFolder folder, Guid draftId, MimeMessage mime, CancellationToken cancellationToken)
    {
        var result = (await _engine.MutateItemsAsync(folder.RemoteFolderId, [EasMutation.Add(draftId.ToString("N"), EasDraftMapper.Mime(mime))],
            deletesAsMoves: true, cancellationToken).ConfigureAwait(false))[0];
        if (!result.Succeeded || string.IsNullOrEmpty(result.ServerId))
            throw new EasCommandException("Sync", result.Status);

        var conversation = result.Data?.Element(EasDataMapper.Email2 + "ConversationId")?.Value;
        return (result.ServerId, string.IsNullOrEmpty(conversation) ? null : conversation);
    }

    private async Task<MailItemFolder> DraftsFolderAsync(MailCopy draft)
    {
        var folder = await _folders.GetFolderAsync(draft.FolderId).ConfigureAwait(false);
        return folder != null && folder.EasFolderType > 0 && !string.IsNullOrEmpty(folder.RemoteFolderId)
            ? folder
            : throw new InvalidOperationException("The draft has no ActiveSync folder to be saved into.");
    }

    /// <summary>
    /// Saves the fetched MIME and the threading headers it carries; synchronization reads no MIME and ActiveSync has
    /// no Message-ID field, so replies and threading learn them here.
    /// </summary>
    public override async Task DownloadMissingMimeMessageAsync(MailCopy mail, ITransferProgress transferProgress = null, CancellationToken cancellationToken = default)
    {
        var (folder, id) = await RemoteMailAsync(mail).ConfigureAwait(false);
        var data = await _engine.FetchAsync(folder.RemoteFolderId, id, cancellationToken).ConfigureAwait(false);
        var mime = EasDataMapper.ReadMime(data);
        _engine.ApplyAttachmentPolicy(mime);
        if (!await _mime.SaveMimeMessageAsync(mail.FileId, mime, Account.Id).ConfigureAwait(false))
            throw new IOException("The EAS MIME message could not be saved.");

        var messageId = MailHeaderExtensions.NormalizeMessageId(mime.Headers[HeaderId.MessageId]) ?? string.Empty;
        var inReplyTo = MailHeaderExtensions.NormalizeMessageId(string.IsNullOrWhiteSpace(mime.InReplyTo) ? mime.Headers[HeaderId.InReplyTo] : mime.InReplyTo) ?? string.Empty;
        var references = (mime.References?.Count > 0 ? MailHeaderExtensions.JoinStoredReferences(mime.References)
            : MailHeaderExtensions.NormalizeReferences(mime.Headers[HeaderId.References])) ?? string.Empty;
        if (messageId != (mail.MessageId ?? string.Empty) || inReplyTo != (mail.InReplyTo ?? string.Empty) || references != (mail.References ?? string.Empty))
        {
            await _engine.SaveMessageHeadersAsync(mail.UniqueId, messageId, inReplyTo, references).ConfigureAwait(false);
            mail.MessageId = messageId;
            mail.InReplyTo = inReplyTo;
            mail.References = references;
        }
        await TryMapCalendarInvitationAsync(mail, mime, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Links a meeting message to the event it belongs to, as the EWS synchronizer does: the event is found by the
    /// invitation's iCalendar UID, which Exchange also reports as the event's UID. A series maps to its first stored
    /// occurrence, because the series itself is not shown. Only stored events are searched: an invitation whose event
    /// the calendar has not synchronized yet stays unlinked. Failures are logged; the mail is already saved.
    /// </summary>
    private async Task TryMapCalendarInvitationAsync(MailCopy mail, MimeMessage message, CancellationToken cancellationToken)
    {
        if (Account.CalendarIntegrationSource != AccountIntegrationSource.Provider || !Account.IsCalendarAccessGranted)
            return;
        var uid = message.ExtractInvitationUid();
        if (string.IsNullOrWhiteSpace(uid))
            return;
        try
        {
            if (await _engine.FindEventByUidAsync(uid).ConfigureAwait(false) is not { } match)
                return;
            var item = await _calendars.GetCalendarItemAsync(match.CalendarId, match.RemoteId).ConfigureAwait(false);
            if (item?.IsRecurringParent == true)
                item = (await _calendars.GetRecurringChildrenAsync(item.Id).ConfigureAwait(false)).OrderBy(child => child.StartDate).FirstOrDefault();
            if (item == null)
                return;
            await _changeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping
            {
                Id = Guid.NewGuid(), AccountId = Account.Id, MailCopyId = mail.Id, InvitationUid = uid,
                CalendarId = match.CalendarId, CalendarItemId = item.Id, CalendarRemoteEventId = item.RemoteEventId
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.Warning(exception, "EAS invitation mail {MailCopyId} was not linked to its event.", mail.Id);
        }
    }

    protected override async Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            if (request is ContactListRequest || request.SourceKind == ContactSourceKind.Local)
            {
                await _localContacts.ExecuteRequestsAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (request is ContactCategoryRequest categories)
            {
                var categoryBook = (await _contacts.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(book => book.Id == categories.AddressBookId);
                await _engine.MutateAsync(EasDataMapper.ServerId(categoryBook.RemoteId), "Change", EasDataMapper.ServerId(categories.Contact.RemoteId),
                    new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(EasDataMapper.Contact + "Categories",
                        categories.CategoryNames.Select(name => new XElement(EasDataMapper.Contact + "Category", name)))), string.Empty, cancellationToken).ConfigureAwait(false);
                await _contacts.SetContactCategoriesAsync(categories.LocalContactId, categories.CategoryNames).ConfigureAwait(false);
                continue;
            }
            if (request is not ContactActionRequest action)
                throw new NotSupportedException("This EAS contact operation is not supported.");

            var book = (await _contacts.GetAddressBooksAsync(Account.Id).ConfigureAwait(false)).First(book => book.Id == action.AddressBookId);
            var operation = action.Operation switch { ContactSynchronizerOperation.Create => "Add", ContactSynchronizerOperation.Delete => "Delete", _ => "Change" };
            var data = operation == "Delete" ? null : EasDataMapper.WriteContact(action.Contact);
            if (operation == "Change" && action.OriginalContact != null && data != null)
                data = EasDataMapper.ChangedFields(data, EasDataMapper.WriteContact(action.OriginalContact));
            if (action.Operation is ContactSynchronizerOperation.SetPhoto or ContactSynchronizerOperation.DeletePhoto)
                data = new XElement(EasDataMapper.Sync + "ApplicationData", new XElement(EasDataMapper.Contact + "Picture",
                    action.Operation == ContactSynchronizerOperation.SetPhoto ? Convert.ToBase64String(action.Photo) : string.Empty));
            async Task PersistContactAsync(string remoteId)
            {
                var committed = RequestEntityCloner.Contact(action.Contact);
                committed.RemoteId = EasDataMapper.ProviderId(remoteId);
                if (action.Operation == ContactSynchronizerOperation.SetPhoto)
                {
                    committed.ContactPictureFileId = await _pictures.SavePictureAsync(PictureKind.Contact, action.Photo).ConfigureAwait(false);
                    committed.RemotePhotoKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(action.Photo));
                }
                await _contacts.CompleteMutationAsync(action.LocalContactId, committed, operation == "Delete").ConfigureAwait(false);
                if (action.Operation == ContactSynchronizerOperation.DeletePhoto)
                    await _contacts.SuppressContactPictureAsync(action.LocalContactId, string.Empty).ConfigureAwait(false);
            }
            await _engine.MutateAsync(EasDataMapper.ServerId(book.RemoteId), operation, EasDataMapper.ServerId(action.Contact.RemoteId), data, action.LocalContactId.ToString("N"),
                cancellationToken, PersistContactAsync).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
    {
        foreach (var request in requests)
        {
            if (request is not TaskActionRequest action)
                throw new NotSupportedException("This EAS task operation is not supported.");
            if ((action.Task?.SourceKind ?? action.List?.SourceKind) == TaskSourceKind.Local || action.Step != null || action.Group != null || action.Operation == TaskSynchronizerOperation.UpdateListPlacement)
            {
                await _localTasks.ExecuteRequestsAsync(new[] { request }, cancellationToken).ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }
            if (action.Task == null && action.List != null)
            {
                var folderCommand = action.Operation switch { TaskSynchronizerOperation.CreateList => "FolderCreate",
                    TaskSynchronizerOperation.UpdateList => "FolderUpdate", TaskSynchronizerOperation.DeleteList => "FolderDelete",
                    _ => throw new NotSupportedException("This EAS task list operation is not supported.") };
                if (action.List.IsDefault && folderCommand == "FolderDelete")
                    throw new InvalidOperationException("The default EAS task list cannot be deleted.");
                var listServerId = EasDataMapper.ServerId(action.List.RemoteId);
                // FolderUpdate's ParentId is where the folder stays; a rename keeps the parent the hierarchy reported.
                var parentId = folderCommand == "FolderUpdate" ? await _engine.GetCollectionParentAsync(listServerId).ConfigureAwait(false) : "0";
                var id = await _engine.ChangeFolderAsync(folderCommand, listServerId, parentId, action.List.Title, 15, cancellationToken).ConfigureAwait(false);
                var committedList = RequestEntityCloner.TaskList(action.List);
                committedList.RemoteId = EasDataMapper.ProviderId(id);
                await _tasks.CompleteListMutationAsync(action.List.Id, committedList, folderCommand == "FolderDelete").ConfigureAwait(false);
                MarkTaskRequestProcessed(request);
                continue;
            }
            if (action.Task == null)
                throw new NotSupportedException("This EAS task operation is not supported.");

            var list = await _tasks.GetTaskListAsync(action.Task.TaskListId).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS task list is missing.");
            var operation = action.Operation switch { TaskSynchronizerOperation.CreateTask => "Add", TaskSynchronizerOperation.UpdateTask => "Change",
                TaskSynchronizerOperation.DeleteTask => "Delete", _ => throw new NotSupportedException("This EAS task operation is not supported.") };
            var data = operation == "Delete" ? null : EasDataMapper.WriteTask(action.Task, clearDueDate: action.OriginalTask?.DueDate != null);
            if (operation == "Change" && action.OriginalTask != null && data != null)
                data = EasDataMapper.ChangedFields(data, EasDataMapper.WriteTask(action.OriginalTask));
            await _engine.MutateAsync(EasDataMapper.ServerId(list.RemoteId), operation, EasDataMapper.ServerId(action.Task.RemoteId),
                data, action.Task.Id.ToString("N"), cancellationToken, async remoteId =>
                {
                    var committed = RequestEntityCloner.Task(action.Task);
                    committed.RemoteId = EasDataMapper.ProviderId(remoteId);
                    await _tasks.CompleteTaskMutationAsync(action.Task.Id, committed, operation == "Delete", action.Task, action.Operation).ConfigureAwait(false);
                }).ConfigureAwait(false);
            MarkTaskRequestProcessed(request);
        }
    }

    public override List<IRequestBundle<EwsRequest>> CreateCalendarEvent(CreateCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.CreateCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.ComposeResult.Attachments?.Count > 0)
            throw new NotSupportedException("EAS event attachments are not supported yet.");
        var isProtocol16 = await _engine.IsProtocol16Async(token).ConfigureAwait(false);
        RequireMeetingSupport(isProtocol16, HasAttendees(request.PreparedEvent.Attendees));
        var data = EasDataMapper.WriteEvent(request.PreparedItem, request.PreparedEvent.Attendees, request.PreparedEvent.Reminders, isProtocol16);
        // 16.x generates the UID itself and takes the client's id as ClientUid.
        data.Add(new XElement(EasDataMapper.Calendar + (isProtocol16 ? "ClientUid" : "UID"), request.PreparedItem.Id.ToString("N")));
        await _engine.MutateEventAsync(request.AssignedCalendar.RemoteCalendarId, new EasEventMutation("Add", string.Empty, data, request.PreparedItem.Id.ToString("N"),
            Persist: async id =>
            {
                var trackedId = id.WithClientTrackingId(request.PreparedItem.Id);
                await _changeProcessor.PersistCreatedCalendarEventAsync(request.PreparedItem, request.PreparedEvent.Attendees,
                    request.PreparedEvent.Reminders, trackedId).ConfigureAwait(false);
                var stored = await _calendars.GetCalendarItemAsync(request.AssignedCalendar.Id, id).ConfigureAwait(false);
                if (stored != null && stored.Id != request.PreparedItem.Id)
                {
                    UiChanges.RevertRequest(request);
                    stored.RemoteEventId = trackedId;
                    await _calendars.UpdateCalendarItemAsync(stored, await _calendars.GetAttendeesAsync(stored.Id).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }), token).ConfigureAwait(false);
    }, request, request);

    /// <summary>
    /// Changes an event, or one occurrence of a series. The organizer's change carries the attendees, and on 16.x the
    /// server sends them the update; an attendee's change to their copy leaves the attendees alone.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> UpdateCalendarEvent(UpdateCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.UpdateCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        if (request.Attachments?.Count > 0)
            throw new NotSupportedException("EAS event attachments are not supported yet.");
        var calendar = await _calendars.GetAccountCalendarAsync(request.Item.CalendarId).ConfigureAwait(false);
        var attendees = request.Attendees ?? await _calendars.GetAttendeesAsync(request.Item.Id).ConfigureAwait(false);
        var reminders = request.Reminders ?? await _calendars.GetRemindersAsync(request.Item.Id).ConfigureAwait(false);
        var isProtocol16 = await _engine.IsProtocol16Async(token).ConfigureAwait(false);
        var isOrganizer = !request.Item.IsLocked;
        if (isOrganizer)
            RequireMeetingSupport(isProtocol16, HasAttendees(attendees));
        var occurrence = request.Item.IsRecurringChild ? EasDataMapper.SplitOccurrenceId(request.Item.RemoteEventId) : null;
        var sendsAttendees = isOrganizer && occurrence == null;
        var data = EasDataMapper.WriteEvent(request.Item, sendsAttendees ? attendees : null, reminders, isProtocol16);
        if (isProtocol16)
        {
            // 16.x keeps the calendar elements a Change leaves out, and a StartTime, EndTime or Recurrence sent for a
            // series deletes its exceptions, so only the elements that differ from the stored event are sent.
            var stored = await _calendars.GetCalendarItemAsync(request.Item.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncItemRequiresSync);
            var original = EasDataMapper.WriteEvent(stored, sendsAttendees ? await _calendars.GetAttendeesAsync(stored.Id).ConfigureAwait(false) : null,
                await _calendars.GetRemindersAsync(stored.Id).ConfigureAwait(false), isProtocol16);
            data = EasDataMapper.ChangedFields(data, original);
        }
        await _engine.MutateEventAsync(calendar.RemoteCalendarId, new EasEventMutation("Change",
            occurrence?.SeriesId ?? request.Item.RemoteEventId.GetProviderRemoteEventId(), data, InstanceKey: occurrence?.InstanceKey,
            Persist: async _ =>
            {
                await _calendars.UpdateCalendarItemAsync(request.Item, attendees).ConfigureAwait(false);
                await _calendars.SaveRemindersAsync(request.Item.Id, reminders).ConfigureAwait(false);
            }), token).ConfigureAwait(false);
    }, request, request);

    public override List<IRequestBundle<EwsRequest>> ChangeStartAndEndDate(ChangeStartAndEndDateRequest request) => UpdateCalendarEvent(request);

    /// <summary>Deletes an event or one occurrence. On 16.x the server cancels the meeting for its attendees when the organizer deletes it.</summary>
    public override List<IRequestBundle<EwsRequest>> DeleteCalendarEvent(DeleteCalendarEventRequest request) => Bundle(async token =>
    {
        if (Account.CalendarIntegrationSource == AccountIntegrationSource.Local)
        {
            await _localCalendar.DeleteCalendarEventAsync(request).ConfigureAwait(false);
            return;
        }
        var calendar = await _calendars.GetAccountCalendarAsync(request.Item.CalendarId).ConfigureAwait(false);
        if (!request.Item.IsLocked)
            RequireMeetingSupport(await _engine.IsProtocol16Async(token).ConfigureAwait(false),
                HasAttendees(await _calendars.GetAttendeesAsync(request.Item.Id).ConfigureAwait(false)));
        var occurrence = request.Item.IsRecurringChild ? EasDataMapper.SplitOccurrenceId(request.Item.RemoteEventId) : null;
        await _engine.MutateEventAsync(calendar.RemoteCalendarId, new EasEventMutation("Delete",
            occurrence?.SeriesId ?? request.Item.RemoteEventId.GetProviderRemoteEventId(), null, InstanceKey: occurrence?.InstanceKey), token).ConfigureAwait(false);
    }, request, request);

    public override List<IRequestBundle<EwsRequest>> AcceptEvent(AcceptEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.AcceptEventAsync(request), request, request) : MeetingResponse(request, request.Item, 1, request.ResponseMessage);
    public override List<IRequestBundle<EwsRequest>> TentativeEvent(TentativeEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.TentativeEventAsync(request), request, request) : MeetingResponse(request, request.Item, 2, request.ResponseMessage);
    public override List<IRequestBundle<EwsRequest>> DeclineEvent(DeclineEventRequest request) => Account.CalendarIntegrationSource == AccountIntegrationSource.Local
        ? Bundle(_ => _localCalendar.DeclineEventAsync(request), request, request) : MeetingResponse(request, request.Item, 3, request.ResponseMessage);

    /// <summary>
    /// Answers a meeting. On 16.x the server sends the answer, with the user's message, to the organizer and updates
    /// the calendar; the stored event takes the answer at once, as the server's next report would.
    /// </summary>
    private List<IRequestBundle<EwsRequest>> MeetingResponse(IRequestBase request, CalendarItem item, int response, string? message) => Bundle(async token =>
    {
        RequireMeetingSupport(await _engine.IsProtocol16Async(token).ConfigureAwait(false), true);
        var calendar = await _calendars.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);
        var occurrence = item.IsRecurringChild ? EasDataMapper.SplitOccurrenceId(item.RemoteEventId) : null;
        await _engine.RespondToMeetingAsync(calendar.RemoteCalendarId, occurrence?.SeriesId ?? item.RemoteEventId.GetProviderRemoteEventId(),
            occurrence?.InstanceKey, response, message, token).ConfigureAwait(false);

        var stored = await _calendars.GetCalendarItemAsync(item.Id).ConfigureAwait(false);
        if (stored == null)
            return;
        stored.Status = response switch { 1 => CalendarItemStatus.Accepted, 2 => CalendarItemStatus.Tentative, _ => CalendarItemStatus.Cancelled };
        stored.IsHidden = stored.Status == CalendarItemStatus.Cancelled;
        await _calendars.UpdateCalendarItemAsync(stored, await _calendars.GetAttendeesAsync(stored.Id).ConfigureAwait(false)).ConfigureAwait(false);
    }, request, (IUIChangeRequest)request);

    /// <summary>Whether the message has an ActiveSync item id; a draft kept on a server without drafts has none.</summary>
    private bool TryRemoteMail(MailCopy mail, out string remoteId)
    {
        remoteId = string.Empty;
        var prefix = Account.Id.ToString("N") + "/";
        if (string.IsNullOrEmpty(mail.Id) || !mail.Id.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var separator = mail.Id.IndexOf('/', prefix.Length);
        if (separator < 0 || separator == mail.Id.Length - 1)
            return false;
        remoteId = Uri.UnescapeDataString(mail.Id[(separator + 1)..]);
        return true;
    }

    private static bool HasAttendees(IEnumerable<CalendarEventAttendee>? attendees)
        => attendees?.Any(attendee => !attendee.IsOrganizer && !string.IsNullOrWhiteSpace(attendee.Email)) == true;

    /// <summary>
    /// Below 16.0 the server neither sends invitations, updates and cancellations nor delivers meeting answers, so a
    /// meeting operation there is refused instead of leaving the other participants uninformed.
    /// </summary>
    private static void RequireMeetingSupport(bool isProtocol16, bool isMeeting)
    {
        if (isMeeting && !isProtocol16)
            throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncMeetingsNeedProtocol16);
    }

    private async Task<(MailItemFolder Folder, string RemoteId)> RemoteMailAsync(MailCopy mail)
    {
        var folder = await _folders.GetFolderAsync(mail.FolderId).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS mail folder is missing.");
        var prefix = EasDataMapper.Key(Account.Id, folder.RemoteFolderId, string.Empty);
        if (!mail.Id.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The message does not belong to this EAS folder.");
        return (folder, Uri.UnescapeDataString(mail.Id[prefix.Length..]));
    }

    private static List<IRequestBundle<EwsRequest>> Bundle(Func<CancellationToken, Task> action, IRequestBase request, IUIChangeRequest ui)
        => request == null ? [] : [new EwsRequestBundle(new EwsRequest((_, token) => action(token), request), request, ui)];

    public override async Task KillSynchronizerAsync()
    {
        await base.KillSynchronizerAsync().ConfigureAwait(false);
        await CloseChangeListenerAsync().ConfigureAwait(false);
        _engine.Dispose();
    }
}
