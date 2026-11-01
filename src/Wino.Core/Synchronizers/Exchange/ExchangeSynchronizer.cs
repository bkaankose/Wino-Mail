using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Exchange.WebServices.Data;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Core.Services;
using Wino.Messaging.UI;
using EwsMimeContent = Microsoft.Exchange.WebServices.Data.MimeContent;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Synchronizes on-premises Exchange mail over Exchange Web Services: the transport for servers that do not offer MAPI
/// over HTTP (before Exchange 2013 SP1, or with the protocol disabled) and for accounts pinned to EWS. The MAPI over
/// HTTP synchronizer derives from it and replaces the mail surface.
/// </summary>
public class ExchangeSynchronizer : WinoSynchronizer<EwsRequest, Item, Appointment, AccountContact>
{
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    // The highest schema the EWS Managed API exposes; it negotiates up against Exchange 2016, 2019 and SE.
    private const ExchangeVersion TargetExchangeVersion = ExchangeVersion.Exchange2013_SP1;

    // The folder class of mail folders. New folders are stamped with it so the hierarchy sync recognizes them.
    private const string MailFolderClass = "IPF.Note";

    private readonly ILogger _logger = Log.ForContext<ExchangeSynchronizer>();
    private readonly LocalContactSynchronizer _localContactSynchronizer;
    private readonly LocalTaskSynchronizer _localTaskSynchronizer;

    protected IExchangeAuthenticator ExchangeAuthenticator { get; }
    protected IExchangeChangeProcessor ExchangeChangeProcessor { get; }
    protected IExchangeSynchronizerErrorHandlerFactory ErrorHandlerFactory { get; }

    public ExchangeSynchronizer(MailAccount account,
                                IExchangeAuthenticator exchangeAuthenticator,
                                IExchangeChangeProcessor exchangeChangeProcessor,
                                IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory,
                                IContactService? contactService = null,
                                IPictureStorageService? contactPictureService = null,
                                ITaskService? taskService = null)
        : base(account, WeakReferenceMessenger.Default)
    {
        ExchangeAuthenticator = exchangeAuthenticator;
        ExchangeChangeProcessor = exchangeChangeProcessor;
        ErrorHandlerFactory = errorHandlerFactory;
        _localContactSynchronizer = new LocalContactSynchronizer(contactService, contactPictureService);
        _localTaskSynchronizer = new LocalTaskSynchronizer(taskService);
    }

    // EWS throws when a property that was not requested here is read.
    private static readonly PropertySet ItemMetadataPropertySet = new(
        BasePropertySet.IdOnly,
        ItemSchema.Subject,
        ItemSchema.DateTimeReceived,
        ItemSchema.Size,
        ItemSchema.HasAttachments,
        ItemSchema.Importance,
        ItemSchema.ConversationId,
        EmailMessageSchema.From,
        EmailMessageSchema.IsRead,
        ItemSchema.Flag,
        ItemSchema.ItemClass,
        EmailMessageSchema.InternetMessageId);

    protected async Task<ExchangeService> CreateServiceAsync(CancellationToken cancellationToken = default)
    {
        var serverInformation = Account.ServerInformation
            ?? throw new InvalidOperationException("The Exchange account has no server information.");

        if (!Uri.TryCreate(serverInformation.IncomingServer, UriKind.Absolute, out var serviceUrl))
            throw new InvalidOperationException("The Exchange account has no valid Exchange Web Services URL.");

        var credential = await ExchangeAuthenticator.GetCredentialAsync(Account, cancellationToken).ConfigureAwait(false);

        var service = new ExchangeService(TargetExchangeVersion)
        {
            Credentials = ToEwsCredentials(credential),
            Url = serviceUrl
        };

        // Keeps every request on the mailbox's own backend; multi-server deployments can otherwise proxy to a
        // server that does not own the mailbox.
        if (!string.IsNullOrEmpty(Account.Address))
            service.HttpHeaders["X-AnchorMailbox"] = Account.Address;

        return service;
    }

    private static ExchangeCredentials ToEwsCredentials(ExchangeCredential credential) => credential switch
    {
        ExchangeCredential.Password password => new WebCredentials(password.Credential),
        ExchangeCredential.Bearer bearer => new OAuthCredentials(bearer.AccessToken),
        _ => throw new InvalidOperationException($"Unsupported Exchange credential {credential.GetType().Name}.")
    };

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Item message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        List<NewMailItemPackage> packages = [new NewMailItemPackage(MapToMailCopy(message, assignedFolder), null, assignedFolder.RemoteFolderId)];
        return Task.FromResult(packages);
    }

    /// <summary>
    /// Best-effort proxy address discovery. EWS has no call for the mailbox's own proxy addresses, so the mailbox's
    /// directory entry is resolved, and the account address stays the root alias either way.
    /// </summary>
    protected override async Task SynchronizeAliasesAsync()
    {
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string? rawAddress)
        {
            var normalized = CleanSmtpAddress(rawAddress);
            if (normalized == null)
                return;

            var isAccountAddress = normalized.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);

            if (aliases.TryGetValue(normalized, out var existing))
            {
                existing.IsPrimary |= isAccountAddress;
                existing.IsRootAlias |= isAccountAddress;
                return;
            }

            aliases[normalized] = new RemoteAccountAlias
            {
                AliasAddress = normalized,
                ReplyToAddress = normalized,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = true,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Confirmed
            };
        }

        AddAlias(Account.Address);

        try
        {
            var service = await CreateServiceAsync().ConfigureAwait(false);

            var resolutions = await service
                .ResolveName(Account.Address, ResolveNameSearchLocation.DirectoryOnly, returnContactDetails: true, CancellationToken.None)
                .ConfigureAwait(false);

            foreach (var resolution in resolutions)
            {
                // ResolveName can be ambiguous; only the entry of this mailbox counts.
                if (!string.Equals(CleanSmtpAddress(resolution.Mailbox?.Address), Account.Address, StringComparison.OrdinalIgnoreCase))
                    continue;

                AddAlias(resolution.Mailbox?.Address);

                var contact = resolution.Contact;
                if (contact?.EmailAddresses == null)
                    continue;

                foreach (var key in new[] { EmailAddressKey.EmailAddress1, EmailAddressKey.EmailAddress2, EmailAddressKey.EmailAddress3 })
                {
                    if (contact.EmailAddresses.TryGetValue(key, out var emailAddress))
                        AddAlias(emailAddress?.Address);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange alias discovery through ResolveName failed for {Account}.", Account.Name);
        }

        await ExchangeChangeProcessor
            .UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList())
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A usable SMTP address, or null. Strips an "SMTP:" prefix and rejects other routing types (EX:, X500:, EUM:)
    /// and anything that does not look like an email address.
    /// </summary>
    private static string? CleanSmtpAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var normalized = address.Trim();

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            if (!normalized[..colonIndex].Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                return null;

            normalized = normalized[(colonIndex + 1)..].Trim();
        }

        return normalized.Contains('@') && normalized.Contains('.') ? normalized : null;
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);

        if (options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            await SynchronizeFoldersAsync(service, cancellationToken).ConfigureAwait(false);

        if (options.Type == MailSynchronizationType.FoldersOnly)
            return MailSynchronizationResult.Empty;

        var foldersToSync = (await ExchangeChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .ToList();

        var downloaded = new List<MailCopy>();
        foreach (var folder in foldersToSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                downloaded.AddRange(await SynchronizeFolderItemsAsync(service, folder, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ServiceResponseException ex) when (
                ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound or ServiceError.ErrorNonExistentMailbox
                || ex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
            {
                // The folder was deleted on the server. Dropping it locally stops it failing every sync.
                _logger.Warning("Exchange folder {Folder} no longer exists on the server; removing it locally.", folder.FolderName);
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
                    OperationType = "ExchangeFolderSync"
                };

                await ErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);
                _logger.Error(ex, "Exchange folder sync failed for {Folder} ({Account}).", folder.FolderName, Account.Name);
            }
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(Account.CalendarIntegrationSource == AccountIntegrationSource.Local
            ? CalendarSynchronizationResult.Empty
            : CalendarSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_CalendarUnavailable)));

    protected override Task<ContactSynchronizationResult> SynchronizeContactsInternalAsync(ContactSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource == AccountIntegrationSource.Local
            ? _localContactSynchronizer.SynchronizeAsync(options, cancellationToken)
            : Task.FromResult(ContactSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable)));

    protected override Task ExecuteContactRequestsInternalAsync(IReadOnlyList<IContactActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.ContactIntegrationSource == AccountIntegrationSource.Local
            ? _localContactSynchronizer.ExecuteRequestsAsync(requests, cancellationToken)
            : throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable);

    protected override Task<TaskSynchronizationResult> SynchronizeTasksInternalAsync(TaskSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Account.TaskIntegrationSource == AccountIntegrationSource.Local
            ? _localTaskSynchronizer.SynchronizeAsync(options, cancellationToken)
            : Task.FromResult(TaskSynchronizationResult.Failed(new InvalidOperationException(Translator.Synchronizer_TasksUnavailable)));

    protected override Task ExecuteTaskRequestsInternalAsync(IReadOnlyList<ITaskActionRequest> requests, CancellationToken cancellationToken = default)
        => Account.TaskIntegrationSource == AccountIntegrationSource.Local
            ? _localTaskSynchronizer.ExecuteRequestsAsync(requests, cancellationToken)
            : throw new InvalidOperationException(Translator.Synchronizer_TasksUnavailable);

    #region Folders

    /// <summary>
    /// Reconciles the remote mail folder hierarchy into the local folders: inserts new ones, updates renamed or moved
    /// ones and deletes the ones that are gone. Special folders are detected by binding the well-known folders.
    /// </summary>
    private async Task SynchronizeFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var (specialMap, unresolvedTypes) = await BuildSpecialFolderMapAsync(service, cancellationToken).ConfigureAwait(false);

        var view = new FolderView(500)
        {
            Traversal = FolderTraversal.Deep,
            PropertySet = new PropertySet(BasePropertySet.IdOnly, FolderSchema.DisplayName, FolderSchema.FolderClass, FolderSchema.ParentFolderId)
        };

        var remoteFolders = new List<Folder>();
        FindFoldersResults page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = await service.FindFolders(WellKnownFolderName.MsgFolderRoot, view).ConfigureAwait(false);
            remoteFolders.AddRange(page.Folders.Where(IsMailFolder));
            if (page.NextPageOffset.HasValue)
                view.Offset = page.NextPageOffset.Value;
        }
        while (page.MoreAvailable);

        var localFolders = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var localByRemoteId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        var structureChanged = false;

        foreach (var remote in remoteFolders)
        {
            var remoteId = remote.Id.UniqueId;

            // SpecialFolderType's default value is Inbox, so an unmapped folder must fall back to Other explicitly.
            var specialType = specialMap.TryGetValue(remoteId, out var mappedType) ? mappedType : SpecialFolderType.Other;
            var isSystem = specialType != SpecialFolderType.Other;

            if (localByRemoteId.TryGetValue(remoteId, out var existing))
            {
                var newParentId = remote.ParentFolderId?.UniqueId;
                if (existing.FolderName != remote.DisplayName || existing.ParentRemoteFolderId != newParentId)
                    structureChanged = true;

                // A well-known bind that failed in this pass says nothing about the folder: keep the known system
                // type instead of demoting a pinned folder such as the Inbox to a plain user folder.
                if (existing.IsSystemFolder && !isSystem && unresolvedTypes.Contains(existing.SpecialFolderType))
                {
                    specialType = existing.SpecialFolderType;
                    isSystem = true;
                }

                if (existing.IsSystemFolder && !isSystem)
                {
                    existing.IsSticky = false;
                    structureChanged = true;
                }

                // A folder first seen as a user folder (its well-known bind failed then) is pinned once it resolves.
                // The Inbox is never left unpinned: the navigation only looks for it at the top level.
                if ((!existing.IsSystemFolder && isSystem) || (specialType == SpecialFolderType.Inbox && !existing.IsSticky))
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                existing.FolderName = remote.DisplayName;
                existing.ParentRemoteFolderId = newParentId;
                existing.SpecialFolderType = specialType;
                existing.IsSystemFolder = isSystem;
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
                    ParentRemoteFolderId = remote.ParentFolderId?.UniqueId,
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

        var remoteIds = remoteFolders.Select(f => f.Id.UniqueId).ToHashSet();
        foreach (var local in localFolders)
        {
            if (!string.IsNullOrEmpty(local.RemoteFolderId) && !remoteIds.Contains(local.RemoteFolderId))
            {
                structureChanged = true;
                await ExchangeChangeProcessor.DeleteFolderAsync(Account.Id, local.RemoteFolderId).ConfigureAwait(false);
            }
        }

        if (structureChanged)
            Messenger.Send(new AccountFolderConfigurationUpdated(Account.Id));
    }

    /// <summary>
    /// Maps well-known folder ids to their special type. Types whose bind failed twice are returned separately so the
    /// caller leaves folders that are already classified alone.
    /// </summary>
    private async Task<(Dictionary<string, SpecialFolderType> Map, HashSet<SpecialFolderType> Unresolved)> BuildSpecialFolderMapAsync(
        ExchangeService service, CancellationToken cancellationToken)
    {
        var wellKnown = new (WellKnownFolderName Folder, SpecialFolderType Special)[]
        {
            (WellKnownFolderName.Inbox, SpecialFolderType.Inbox),
            (WellKnownFolderName.SentItems, SpecialFolderType.Sent),
            (WellKnownFolderName.Drafts, SpecialFolderType.Draft),
            (WellKnownFolderName.DeletedItems, SpecialFolderType.Deleted),
            (WellKnownFolderName.JunkEmail, SpecialFolderType.Junk),
        };

        // Servers behind a token service may fail the first call until challenged, which used to lose exactly the
        // Inbox and Sent Items. Each failed bind is retried once.
        var map = new Dictionary<string, SpecialFolderType>();
        var unresolved = new HashSet<SpecialFolderType>();
        foreach (var (folder, special) in wellKnown)
        {
            var resolved = false;
            for (var attempt = 1; attempt <= 2 && !resolved; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var bound = await Folder.Bind(service, folder, new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                    map[bound.Id.UniqueId] = special;
                    resolved = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Well-known folder {Folder} bind failed for {Account} (attempt {Attempt}).", folder, Account.Name, attempt);
                }
            }

            if (!resolved)
                unresolved.Add(special);
        }

        return (map, unresolved);
    }

    private static bool IsMailFolder(Folder folder)
        => !string.IsNullOrEmpty(folder.FolderClass)
           && folder.FolderClass.StartsWith(MailFolderClass, StringComparison.OrdinalIgnoreCase);

    // Folder writes only touch the server. The FoldersOnly sync the request delegator queues afterwards reconciles
    // the local tree by remote folder id.

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        return Bundle(async (service, _) =>
        {
            var folder = new Folder(service) { DisplayName = name.Trim(), FolderClass = MailFolderClass };
            await folder.Save(WellKnownFolderName.MsgFolderRoot).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var name = request.NewFolderName;
        var parentId = request.Folder?.RemoteFolderId;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(parentId))
            return [];

        return Bundle(async (service, _) =>
        {
            var folder = new Folder(service) { DisplayName = name.Trim(), FolderClass = MailFolderClass };
            await folder.Save(new FolderId(parentId)).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request)
    {
        var id = request.Folder?.RemoteFolderId;
        if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(request.NewFolderName))
            return [];

        return Bundle(async (service, _) =>
        {
            try
            {
                var folder = await Folder.Bind(service, new FolderId(id)).ConfigureAwait(false);
                folder.DisplayName = request.NewFolderName.Trim();
                await folder.Update().ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Skipping Exchange folder rename; folder {Folder} no longer exists.", request.Folder?.FolderName);
            }
        }, request, request);
    }

    /// <summary>
    /// A first delete moves the folder into Deleted Items; deleting a folder that is already inside Deleted Items
    /// removes it for good, as OWA and Outlook do.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var folderToDelete = request.Folder;
        var id = folderToDelete?.RemoteFolderId;
        if (folderToDelete == null || string.IsNullOrEmpty(id))
            return [];

        return Bundle(async (service, _) =>
        {
            try
            {
                var deleteMode = await IsInDeletedItemsAsync(folderToDelete).ConfigureAwait(false)
                    ? DeleteMode.HardDelete
                    : DeleteMode.MoveToDeletedItems;

                var folder = await Folder.Bind(service, new FolderId(id), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                await folder.Delete(deleteMode).ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Exchange folder {Folder} is already gone; treating the delete as done.", folderToDelete.FolderName);
            }
        }, request, request);
    }

    /// <summary>Whether the folder sits anywhere under Deleted Items, walking the local parent chain.</summary>
    private async Task<bool> IsInDeletedItemsAsync(MailItemFolder folder)
    {
        var locals = await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var deleted = locals.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Deleted);
        if (deleted == null || string.IsNullOrEmpty(deleted.RemoteFolderId))
            return false;

        var byRemoteId = locals
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        var parentId = folder.ParentRemoteFolderId;
        var guard = 0;
        while (!string.IsNullOrEmpty(parentId) && guard++ < 64)
        {
            if (parentId == deleted.RemoteFolderId)
                return true;

            if (!byRemoteId.TryGetValue(parentId, out var parent))
                break;

            parentId = parent.ParentRemoteFolderId;
        }

        return false;
    }

    #endregion

    #region Mail

    private async Task<List<MailCopy>> SynchronizeFolderItemsAsync(ExchangeService service, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var downloaded = new List<MailCopy>();
        var syncState = folder.DeltaToken;
        var folderId = new FolderId(folder.RemoteFolderId);
        bool moreAvailable;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await service.SyncFolderItems(folderId, ItemMetadataPropertySet, null,
                (int)InitialMessageDownloadCountPerFolder, SyncFolderItemsScope.NormalItems, syncState).ConfigureAwait(false);

            var stateUpdates = new List<MailCopyStateUpdate>();

            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case ChangeType.Create:
                    case ChangeType.Update:
                        if (change.Item == null)
                            break;

                        var packages = await CreateNewMailPackagesAsync(change.Item, folder, cancellationToken).ConfigureAwait(false);
                        foreach (var package in packages)
                        {
                            if (await ExchangeChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false))
                                downloaded.Add(package.Copy);
                            else if (change.ChangeType == ChangeType.Update)
                                stateUpdates.Add(new MailCopyStateUpdate(package.Copy.Id, package.Copy.IsRead, package.Copy.IsFlagged));
                        }
                        break;
                    case ChangeType.Delete:
                        await ExchangeChangeProcessor.DeleteMailsAsync(Account.Id, [change.ItemId.UniqueId]).ConfigureAwait(false);
                        break;
                    case ChangeType.ReadFlagChange:
                        await ExchangeChangeProcessor.ChangeMailReadStatusAsync(change.ItemId.UniqueId, change.IsRead).ConfigureAwait(false);
                        break;
                }
            }

            if (stateUpdates.Count > 0)
                await ExchangeChangeProcessor.ApplyMailStateUpdatesAsync(stateUpdates).ConfigureAwait(false);

            syncState = changes.SyncState;
            moreAvailable = changes.MoreChangesAvailable;
        }
        while (moreAvailable);

        folder.DeltaToken = syncState;
        await ExchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        await ExchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        _logger.Debug("Exchange folder {Folder} synchronized; {Count} new item(s).", folder.FolderName, downloaded.Count);

        return downloaded;
    }

    /// <summary>
    /// Reads an EWS property that may not be loaded, yielding the default instead of throwing: one unreadable item
    /// must not fail the whole folder.
    /// </summary>
    private static T? SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch (ServiceObjectPropertyException) { return default; }
    }

    private static MailCopy MapToMailCopy(Item item, MailItemFolder assignedFolder)
    {
        var email = item as EmailMessage;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = item.Id.UniqueId,
            FolderId = assignedFolder.Id,
            ThreadId = SafeGet(() => item.ConversationId?.UniqueId),
            MessageId = SafeGet(() => email?.InternetMessageId),
            Subject = SafeGet(() => item.Subject),
            FromName = SafeGet(() => email?.From?.Name),
            FromAddress = SafeGet(() => email?.From?.Address),
            CreationDate = SafeGet(() => item.DateTimeReceived.ToUniversalTime()),
            IsRead = SafeGet(() => email?.IsRead) ?? true,
            IsFlagged = SafeGet(() => item.Flag?.FlagStatus) == ItemFlagStatus.Flagged,
            HasAttachments = SafeGet(() => (bool?)item.HasAttachments) ?? false,
            Importance = MapImportance(SafeGet(() => (Importance?)item.Importance) ?? Importance.Normal),
            // Items of the Drafts folder open in the composer, not the reader.
            IsDraft = assignedFolder.SpecialFolderType == SpecialFolderType.Draft,
            ItemType = MapItemType(SafeGet(() => item.ItemClass)),
        };
    }

    /// <summary>The mail item type of a message class (the MS-OXOCAL meeting classes), plain mail otherwise.</summary>
    private static MailItemType MapItemType(string? itemClass)
    {
        if (string.IsNullOrEmpty(itemClass))
            return MailItemType.Mail;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarInvitation;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Canceled", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarCancellation;

        if (itemClass.StartsWith("IPM.Schedule.Meeting.Resp", StringComparison.OrdinalIgnoreCase))
            return MailItemType.CalendarResponse;

        return MailItemType.Mail;
    }

    private static MailImportance MapImportance(Importance importance) => importance switch
    {
        Importance.High => MailImportance.High,
        Importance.Low => MailImportance.Low,
        _ => MailImportance.Normal,
    };

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress? transferProgress = null, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);

        var ewsItem = await Item.Bind(service, new ItemId(mailItem.Id), new PropertySet(ItemSchema.MimeContent)).ConfigureAwait(false);

        using var stream = new MemoryStream(ewsItem.MimeContent.Content);
        var mimeMessage = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        await ExchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps an EWS operation into a single request bundle. EWS is stateless HTTP, so each bundle runs on a fresh
    /// service carrying the current credentials.
    /// </summary>
    protected List<IRequestBundle<EwsRequest>> Bundle(Func<ExchangeService, CancellationToken, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => CreateBundle(async (_, cancellationToken) =>
        {
            var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
            await action(service, cancellationToken).ConfigureAwait(false);
        }, request, uiChangeRequest);

    protected static List<IRequestBundle<EwsRequest>> CreateBundle(Func<IRequestBase, CancellationToken, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => [new EwsRequestBundle(new EwsRequest(action, request), request, uiChangeRequest)];

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async (service, cancellationToken) =>
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = await EmailMessage.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, EmailMessageSchema.IsRead)).ConfigureAwait(false);
                if (message.IsRead == isRead)
                    continue;

                message.IsRead = isRead;
                await message.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> ChangeFlag(BatchChangeFlagRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var flagged = requests[0].IsFlagged;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async (service, cancellationToken) =>
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = await Item.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, ItemSchema.Flag)).ConfigureAwait(false);
                item.Flag = new Flag { FlagStatus = flagged ? ItemFlagStatus.Flagged : ItemFlagStatus.NotFlagged };
                await item.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }

            // SyncFolderItems does not echo flag changes the way it echoes read state, so the flag is stored locally
            // once the server has it.
            foreach (var request in requests)
                await ExchangeChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = requests[0].ToFolder?.RemoteFolderId;
        if (string.IsNullOrEmpty(destination))
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle((service, _) => service.MoveItems(ids, new FolderId(destination)), requests[0], requests);
    }

    // A plain move to or from the junk folder. The server's blocked senders list is not edited.
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = requests[0].TargetFolder?.RemoteFolderId;
        if (string.IsNullOrEmpty(destination))
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle((service, _) => service.MoveItems(ids, new FolderId(destination)), requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(
            (service, _) => service.DeleteItems(ids, DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
            requests[0], requests);
    }

    // The mailbox has no archive action of its own; Archive moves to the account's Archive folder.
    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest request)
        => Move(new BatchMoveRequest(request.Select(a => new MoveRequest(a.Item, a.FromFolder, a.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    #endregion

    #region Drafts and send

    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var localDraft = preparation.CreatedLocalDraftCopy;
        var draftsFolderId = localDraft.AssignedFolder.RemoteFolderId;

        return Bundle(async (service, cancellationToken) =>
        {
            var message = new EmailMessage(service)
            {
                MimeContent = new EwsMimeContent("UTF-8", await ToMimeBytesAsync(preparation.CreatedLocalDraftMimeMessage, cancellationToken).ConfigureAwait(false))
            };

            await message.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

            var isMapped = await ExchangeChangeProcessor.MapLocalDraftAsync(
                Account.Id,
                localDraft.UniqueId,
                message.Id.UniqueId,
                message.Id.UniqueId,
                localDraft.ThreadId).ConfigureAwait(false);

            if (!isMapped)
            {
                // The local draft was discarded while the save was in flight. The server copy goes at once so the
                // next Drafts sync cannot bring it back.
                await HardDeleteAsync(service, message.Id).ConfigureAwait(false);
            }
        }, request, request);
    }

    /// <summary>
    /// Sends the composed MIME and keeps a copy in Sent Items. On-premises Exchange sends as the mailbox. Once the
    /// server accepted the message, removing the draft is best effort: reporting a failure after that point would
    /// bring the draft back and invite a second send.
    /// </summary>
    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;
        var draft = preparation.MailItem;

        return Bundle(async (service, cancellationToken) =>
        {
            var outgoing = MailKitSmtpTransport.CreateSmtpMessage(preparation.Mime);

            var message = new EmailMessage(service)
            {
                MimeContent = new EwsMimeContent("UTF-8", await ToMimeBytesAsync(outgoing, cancellationToken).ConfigureAwait(false))
            };

            var sentFolderId = preparation.SentFolder?.RemoteFolderId;
            if (!string.IsNullOrEmpty(sentFolderId))
                await message.SendAndSaveCopy(new FolderId(sentFolderId)).ConfigureAwait(false);
            else
                await message.SendAndSaveCopy().ConfigureAwait(false);

            if (!draft.IsLocalDraft && !string.IsNullOrEmpty(draft.Id))
            {
                try
                {
                    await HardDeleteAsync(service, new ItemId(draft.Id)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Debug(ex, "The Exchange draft {DraftId} was not deleted after sending; it may already be gone.", draft.Id);
                }
            }

            try
            {
                await ExchangeChangeProcessor.DeleteMailAsync(Account.Id, draft.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Message {MailId} was sent, but deleting its local draft failed for account {AccountId}.", draft.Id, Account.Id);
            }
        }, request, request);
    }

    protected override Task MarkDraftSyncFailedAsync(Guid mailUniqueId, string error)
        => ExchangeChangeProcessor.MarkDraftSyncFailedAsync(mailUniqueId, error);

    /// <summary>
    /// A later save of a server draft. EWS accepts MIME content only when an item is created, so the new MIME is saved
    /// beside the old item, the old item is removed, and the new id is handed back for the local row to follow.
    /// </summary>
    public override async Task<DraftUpdateIdentity> UpdateDraftAsync(DraftUpdateSnapshot snapshot, MailCopy draft, CancellationToken cancellationToken = default)
    {
        var draftsFolderId = draft.AssignedFolder?.RemoteFolderId;
        if (string.IsNullOrEmpty(draftsFolderId))
            throw new InvalidOperationException("The draft has no remote folder to be saved into.");

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);

        using var mime = snapshot.OpenMime();
        var replacement = new EmailMessage(service)
        {
            MimeContent = new EwsMimeContent("UTF-8", await ToMimeBytesAsync(mime, cancellationToken).ConfigureAwait(false))
        };

        await replacement.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

        try
        {
            await HardDeleteAsync(service, new ItemId(draft.Id)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The old draft is superseded either way; the next Drafts sync removes a leftover.
            _logger.Debug(ex, "The superseded Exchange draft {DraftId} was not deleted.", draft.Id);
        }

        return new DraftUpdateIdentity(replacement.Id.UniqueId, replacement.Id.UniqueId, draft.ThreadId);
    }

    private static Task HardDeleteAsync(ExchangeService service, ItemId itemId)
        => service.DeleteItems([itemId], DeleteMode.HardDelete, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences);

    private static async Task<byte[]> ToMimeBytesAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }

    #endregion

    #region Request execution

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        ApplyOptimisticUiChanges(batchedRequests);

        var errors = new List<string>();

        for (var i = 0; i < batchedRequests.Count; i++)
        {
            var bundle = batchedRequests[i];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await bundle.NativeRequest.IntegratorTask(bundle.NativeRequest.Request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Optimistic changes of this bundle and every later one were applied up front but never confirmed.
                for (var j = i; j < batchedRequests.Count; j++)
                    RequestUiChangeCoordinator.RevertBundle(batchedRequests[j]);

                throw;
            }
            catch (Exception ex)
            {
                await HandleFailedRequestAsync(bundle, ex, errors).ConfigureAwait(false);
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
    }

    private async Task HandleFailedRequestAsync(IRequestBundle<EwsRequest> bundle, Exception exception, List<string> errors)
    {
        if (bundle.Request is CreateDraftRequest createDraftRequest)
        {
            await ExchangeChangeProcessor
                .MarkDraftSyncFailedAsync(createDraftRequest.Item.UniqueId, exception.Message)
                .ConfigureAwait(false);
        }

        var errorContext = new SynchronizerErrorContext
        {
            Account = Account,
            ErrorMessage = exception.Message,
            Exception = exception,
            RequestBundle = bundle,
            Request = bundle.Request,
            IsEntityNotFound = IsEntityNotFound(exception, bundle.UIChangeRequest),
            OperationType = "ExchangeExecuteRequest"
        };

        var handled = await ErrorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

        // Handlers own the non-transient errors they handled.
        if (!handled || errorContext.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(errorContext);
            RequestUiChangeCoordinator.RevertBundle(bundle);
            _logger.Error(exception, "Exchange request execution failed for {Account}.", Account.Name);
            errors.Add(exception.Message);
        }
    }

    /// <summary>
    /// Whether a failed operation on an existing item failed because the item or folder is gone. The MAPI over HTTP
    /// synchronizer raises <see cref="Domain.Exceptions.SynchronizerEntityNotFoundException"/> for the same case.
    /// </summary>
    private static bool IsEntityNotFound(Exception exception, IUIChangeRequest? uiChangeRequest)
    {
        if (uiChangeRequest == null || !IsExistingEntityOperation(uiChangeRequest))
            return false;

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is Domain.Exceptions.SynchronizerEntityNotFoundException)
                return true;

            if (current is ServiceResponseException serviceResponse &&
                serviceResponse.ErrorCode is ServiceError.ErrorItemNotFound
                    or ServiceError.ErrorFolderNotFound
                    or ServiceError.ErrorNonExistentMailbox)
            {
                return true;
            }

            var message = current.Message?.ToLowerInvariant() ?? string.Empty;
            if (message.Contains("not found") || message.Contains("does not exist") || message.Contains("cannot be found"))
                return true;
        }

        return false;
    }

    private static bool IsExistingEntityOperation(IUIChangeRequest request)
        => request is BatchDeleteRequest or BatchMoveRequest or BatchChangeFlagRequest
            or BatchMarkReadRequest or BatchArchiveRequest
            or DeleteRequest or MoveRequest or ChangeFlagRequest or MarkReadRequest;

    #endregion
}
