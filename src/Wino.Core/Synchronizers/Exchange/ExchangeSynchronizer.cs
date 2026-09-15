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
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Helpers;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using Wino.Messaging.UI;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Synchronizes on-premises Exchange accounts over Exchange Web Services. This is the fallback
/// transport for servers that do not offer MAPI/HTTP (before Exchange 2013 SP1, or with the
/// protocol disabled) and for accounts the user pins to EWS; the MAPI synchronizer derives from
/// it and replaces every surface. Mail only for now: calendar, contacts and tasks report no
/// provider support until their surfaces are ported.
/// </summary>
public class ExchangeSynchronizer : WinoSynchronizer<EwsRequest, Item, Appointment, AccountContact>
{
    public override uint BatchModificationSize => 100;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    // Exchange2013_SP1 is the highest schema the EWS Managed API exposes; it negotiates
    // up against 2016/2019/SE. TODO: make per-account configurable for older servers.
    private const ExchangeVersion TargetExchangeVersion = ExchangeVersion.Exchange2013_SP1;

    private readonly ILogger _logger = Log.ForContext<ExchangeSynchronizer>();
    private readonly IExchangeAuthenticator _exchangeAuthenticator;
    private readonly IExchangeChangeProcessor _exchangeChangeProcessor;
    private readonly IExchangeSynchronizerErrorHandlerFactory _errorHandlerFactory;

    // Seams for the MAPI subclass (Synchronizers/Mapi), which replaces this class one surface at a time.
    protected IExchangeAuthenticator ExchangeAuthenticator => _exchangeAuthenticator;
    protected IExchangeChangeProcessor ExchangeChangeProcessor => _exchangeChangeProcessor;
    protected IExchangeSynchronizerErrorHandlerFactory ErrorHandlerFactory => _errorHandlerFactory;

    public ExchangeSynchronizer(MailAccount account,
                                IExchangeAuthenticator exchangeAuthenticator,
                                IExchangeChangeProcessor exchangeChangeProcessor,
                                IExchangeSynchronizerErrorHandlerFactory errorHandlerFactory)
        : base(account, WeakReferenceMessenger.Default)
    {
        _exchangeAuthenticator = exchangeAuthenticator;
        _exchangeChangeProcessor = exchangeChangeProcessor;
        _errorHandlerFactory = errorHandlerFactory;
    }

    // EWS throws when reading properties not requested here.
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
        EmailMessageSchema.InternetMessageId);

    protected async Task<ExchangeService> CreateServiceAsync(TimeZoneInfo timeZone = null)
    {
        var serverInformation = Account.ServerInformation
            ?? throw new InvalidOperationException("Exchange account is missing server information.");

        var credentials = await _exchangeAuthenticator.GetCredentialsAsync(Account).ConfigureAwait(false);

        _logger.Debug(
            "Building EWS service. Url={Url}, OAuth={UseOAuth}, ConfiguredUser={User}, CredentialType={CredType}",
            serverInformation.IncomingServer,
            serverInformation.UseOAuthAuthentication,
            serverInformation.IncomingServerUsername,
            credentials?.GetType().Name);

        var service = timeZone == null
            ? new ExchangeService(TargetExchangeVersion)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            }
            : new ExchangeService(TargetExchangeVersion, timeZone)
            {
                Credentials = credentials,
                Url = new Uri(serverInformation.IncomingServer)
            };

        // Route requests to the owning mailbox (Microsoft-recommended). Multi-CAS/DAG deployments can
        // otherwise proxy to a non-owning server; the header keeps every request on the mailbox's backend.
        if (!string.IsNullOrEmpty(Account.Address))
            service.HttpHeaders["X-AnchorMailbox"] = Account.Address;

        return service;
    }

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(Item message, MailItemFolder assignedFolder, CancellationToken cancellationToken = default)
    {
        var mailCopy = MapToMailCopy(message, assignedFolder);
        if (mailCopy == null)
            return Task.FromResult<List<NewMailItemPackage>>(null);

        var package = new NewMailItemPackage(mailCopy, null, assignedFolder.RemoteFolderId);
        return Task.FromResult<List<NewMailItemPackage>>([package]);
    }

    /// <summary>
    /// Best-effort proxy-address discovery. EWS has no direct "my proxy addresses" call,
    /// so this resolves the mailbox's directory entry and keeps the root alias as a fallback.
    /// </summary>
    protected override async Task SynchronizeAliasesAsync()
    {
        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);

        void AddAlias(string rawAddress)
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
                // ResolveName can be ambiguous; only use the entry for this mailbox.
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
            _logger.Debug(ex, "Exchange alias (proxy-address) discovery via ResolveName failed for {Account}.", Account.Name);
        }

        await _exchangeChangeProcessor
            .UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList())
            .ConfigureAwait(false);
    }

    // Normalizes an EWS address to a usable SMTP address, or null. Strips an "SMTP:" prefix and rejects
    // non-SMTP routing (EX:, X500:, EUM:) and anything that isn't a plausible e-mail.
    private static string CleanSmtpAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var normalized = address.Trim();

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            if (!normalized[..colonIndex].Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                return null; // EX:, X500:, EUM:, etc.
            normalized = normalized[(colonIndex + 1)..].Trim();
        }

        return normalized.Contains('@') && normalized.Contains('.') ? normalized : null;
    }

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(MailSynchronizationOptions options, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        if (options.Type is MailSynchronizationType.FullFolders or MailSynchronizationType.FoldersOnly)
            await SynchronizeFoldersAsync(service, cancellationToken).ConfigureAwait(false);

        if (options.Type == MailSynchronizationType.FoldersOnly)
            return MailSynchronizationResult.Empty;

        var foldersToSync = (await _exchangeChangeProcessor.GetSynchronizationFoldersAsync(options).ConfigureAwait(false))
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
            catch (ServiceResponseException srex) when (
                srex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound or ServiceError.ErrorNonExistentMailbox
                || srex.Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase))
            {
                // The folder was deleted on the server (e.g. from OWA). Drop it locally so it stops failing
                // every sync and disappears from the tree, then refresh the navigation.
                _logger.Warning("Exchange folder {Folder} ({RemoteId}) no longer exists on the server; removing locally.",
                    folder.FolderName, folder.RemoteFolderId);
                await _exchangeChangeProcessor.DeleteFolderAsync(Account.Id, folder.RemoteFolderId).ConfigureAwait(false);
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
                    OperationType = "ExchangeFolderSync"
                };

                await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);
                CaptureSynchronizationIssue(errorContext);
                _logger.Error(ex, "Exchange folder sync failed for {Folder} ({Account}).", folder.FolderName, Account.Name);
            }
        }

        return MailSynchronizationResult.Completed(downloaded);
    }

    /// <summary>
    /// Calendar synchronization is not available for Exchange accounts yet; the account's calendar
    /// runs on the local backend until the EWS and MAPI calendar surfaces are ported.
    /// </summary>
    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(CalendarSynchronizationOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(CalendarSynchronizationResult.Empty);

    /// <summary>
    /// Reconciles the remote mail folder hierarchy into local MailItemFolders:
    /// inserts new folders, updates renamed/moved ones, and deletes folders no longer
    /// present remotely. Special-folder types are detected by binding well-known folders.
    /// </summary>
    protected virtual async Task SynchronizeFoldersAsync(ExchangeService service, CancellationToken cancellationToken)
    {
        var (specialMap, unresolvedTypes) = await BuildSpecialFolderMapAsync(service).ConfigureAwait(false);

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

        var localFolders = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
        var localByRemoteId = localFolders
            .Where(f => !string.IsNullOrEmpty(f.RemoteFolderId))
            .GroupBy(f => f.RemoteFolderId)
            .ToDictionary(g => g.Key, g => g.First());

        // Track whether the hierarchy changed (folder added/removed/renamed/reparented) so we can
        // refresh the navigation tree, as the Outlook/Gmail/IMAP synchronizers broadcast it.
        var structureChanged = false;

        foreach (var remote in remoteFolders)
        {
            var remoteId = remote.Id.UniqueId;
            // SpecialFolderType's default (0) is Inbox, so a plain TryGetValue would mistag every ordinary
            // user folder as a sticky system Inbox. Fall back to Other when the folder isn't a well-known one.
            var specialType = specialMap.TryGetValue(remoteId, out var mappedType) ? mappedType : SpecialFolderType.Other;
            var isSystem = specialType != SpecialFolderType.Other;

            if (localByRemoteId.TryGetValue(remoteId, out var existing))
            {
                var newParentId = remote.ParentFolderId?.UniqueId;
                if (existing.FolderName != remote.DisplayName || existing.ParentRemoteFolderId != newParentId)
                    structureChanged = true;

                // A well-known bind that failed this sync says nothing about the folder. Demoting a known system
                // folder on that evidence (as an earlier build did) unpinned the Inbox to a plain user folder under
                // "Folders"; the next good sync restored the type but never the pin. Keep what we know instead.
                if (existing.IsSystemFolder && !isSystem && unresolvedTypes.Contains(existing.SpecialFolderType))
                {
                    specialType = existing.SpecialFolderType;
                    isSystem = true;
                }

                // Heal folders an earlier build mistagged as system (defaulted to Inbox, wrongly sticky/system).
                // With unresolved binds excluded above, a system folder that resolves to Other really was mistagged.
                if (existing.IsSystemFolder && !isSystem)
                {
                    existing.IsSticky = false;
                    structureChanged = true;
                }

                // And the reverse: a folder first seen as a plain user folder that now resolves to a well-known
                // one (the well-known bind failed on an earlier sync, see BuildSpecialFolderMapAsync) must be
                // pinned. Without this an Inbox that missed its first bind sat under "Folders" forever.
                if (!existing.IsSystemFolder && isSystem)
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                // The Inbox is never optional: account switch lands on it and the nav only looks for it at the
                // top level, so an unpinned Inbox is a broken account, not a preference. Repairs installs that
                // went through the demote/re-promote cycle above before this build.
                if (specialType == SpecialFolderType.Inbox && !existing.IsSticky)
                {
                    existing.IsSticky = true;
                    structureChanged = true;
                }

                existing.FolderName = remote.DisplayName;
                existing.ParentRemoteFolderId = newParentId;
                existing.SpecialFolderType = specialType;
                existing.IsSystemFolder = isSystem;
                await _exchangeChangeProcessor.UpdateFolderAsync(existing).ConfigureAwait(false);
            }
            else
            {
                structureChanged = true;
                await _exchangeChangeProcessor.InsertFolderAsync(new MailItemFolder
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

        // Remove local folders that no longer exist remotely.
        var remoteIds = remoteFolders.Select(f => f.Id.UniqueId).ToHashSet();
        foreach (var local in localFolders)
        {
            if (!string.IsNullOrEmpty(local.RemoteFolderId) && !remoteIds.Contains(local.RemoteFolderId))
            {
                structureChanged = true;
                await _exchangeChangeProcessor.DeleteFolderAsync(Account.Id, local.RemoteFolderId).ConfigureAwait(false);
            }
        }

        // Refresh the navigation tree so server-side creates/moves/deletes surface without an app restart.
        if (structureChanged)
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(Account.Id));
    }

    /// <summary>
    /// Maps well-known folder ids to their special type. Types whose bind failed (after a retry) are returned
    /// separately so the caller can leave already-classified folders alone rather than demote them.
    /// </summary>
    private async Task<(Dictionary<string, SpecialFolderType> Map, HashSet<SpecialFolderType> Unresolved)> BuildSpecialFolderMapAsync(ExchangeService service)
    {
        var wellKnown = new (WellKnownFolderName Folder, SpecialFolderType Special)[]
        {
            (WellKnownFolderName.Inbox, SpecialFolderType.Inbox),
            (WellKnownFolderName.SentItems, SpecialFolderType.Sent),
            (WellKnownFolderName.Drafts, SpecialFolderType.Draft),
            (WellKnownFolderName.DeletedItems, SpecialFolderType.Deleted),
            (WellKnownFolderName.JunkEmail, SpecialFolderType.Junk),
        };

        // A failed bind here is not harmless: the folder is then inserted as a plain user folder and only the
        // upward heal in SynchronizeFoldersAsync ever corrects it. Inbox and SentItems are bound first, so a
        // cold-start auth hiccup (on-prem Exchange behind an STS does not offer Bearer until challenged) used to
        // lose exactly those two while the rest resolved. Retry each failure once, and always log.
        var map = new Dictionary<string, SpecialFolderType>();
        var unresolved = new HashSet<SpecialFolderType>();
        foreach (var (folder, special) in wellKnown)
        {
            var resolved = false;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var bound = await Folder.Bind(service, folder, new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                    map[bound.Id.UniqueId] = special;
                    resolved = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Legitimately absent folders (no Junk on some mailboxes) land here too; the retry is cheap.
                    _logger.Warning(ex, "Well-known folder {Folder} bind failed for {Account} (attempt {Attempt}).", folder, Account.Name, attempt);
                }
            }

            if (!resolved)
                unresolved.Add(special);
        }

        return (map, unresolved);
    }

    // The EWS folder class that marks a folder as holding mail items (IPF.Note). Used both to filter the
    // remote hierarchy down to mail folders and to stamp newly-created folders so they round-trip back.
    private const string MailFolderClass = "IPF.Note";

    // Mail folders carry the IPF.Note message class; skip calendar/contact/task/etc.
    private static bool IsMailFolder(Folder folder)
        => !string.IsNullOrEmpty(folder.FolderClass)
           && folder.FolderClass.StartsWith(MailFolderClass, StringComparison.OrdinalIgnoreCase);

    // --- Folder write operations ---
    // The new/renamed/removed folder is reconciled into the local MailItemFolder table by the
    // follow-up FoldersOnly sync that WinoRequestDelegator queues after create/delete, so these
    // just perform the server write (no local-id stamping needed; folders reconcile by RemoteFolderId).

    public override List<IRequestBundle<EwsRequest>> CreateRootFolder(CreateRootFolderRequest request)
    {
        var name = request.NewFolderName;
        if (string.IsNullOrWhiteSpace(name))
            return [];

        return Bundle(async service =>
        {
            // FolderClass MUST be IPF.Note so the new folder is recognized as a mail folder; otherwise
            // SynchronizeFoldersAsync's IsMailFolder filter discards it and it never reaches the local tree.
            var folder = new Folder(service) { DisplayName = name, FolderClass = MailFolderClass };
            await folder.Save(WellKnownFolderName.MsgFolderRoot).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateSubFolder(CreateSubFolderRequest request)
    {
        var name = request.NewFolderName;
        var parentId = request.Folder?.RemoteFolderId;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(parentId))
            return [];

        return Bundle(async service =>
        {
            var folder = new Folder(service) { DisplayName = name, FolderClass = MailFolderClass };
            await folder.Save(new FolderId(parentId)).ConfigureAwait(false);
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> RenameFolder(RenameFolderRequest request)
    {
        var id = request.Folder?.RemoteFolderId;
        if (string.IsNullOrEmpty(id))
            return [];

        return Bundle(async service =>
        {
            try
            {
                var folder = await Folder.Bind(service, new FolderId(id)).ConfigureAwait(false);
                folder.DisplayName = request.NewFolderName;
                await folder.Update().ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Skipping Exchange folder rename; folder {RemoteFolderId} no longer exists.", id);
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> DeleteFolder(DeleteFolderRequest request)
    {
        var id = request.Folder?.RemoteFolderId;
        if (string.IsNullOrEmpty(id))
            return [];

        return Bundle(async service =>
        {
            try
            {
                // A folder already inside Deleted Items can't be "moved to Deleted Items" again, so a second
                // delete should permanently remove it (matching OWA/Outlook). Hard-delete in that case.
                var deleteMode = await IsInDeletedItemsAsync(request.Folder).ConfigureAwait(false)
                    ? DeleteMode.HardDelete
                    : DeleteMode.MoveToDeletedItems;

                var folder = await Folder.Bind(service, new FolderId(id), new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
                await folder.Delete(deleteMode).ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode is ServiceError.ErrorFolderNotFound or ServiceError.ErrorItemNotFound)
            {
                _logger.Warning("Exchange folder {RemoteFolderId} already absent on delete; treating as done.", id);
            }
        }, request, request);
    }

    // True when the folder lives anywhere under the Deleted Items folder, walking the locally-known
    // parent chain (no extra server round-trips).
    private async Task<bool> IsInDeletedItemsAsync(MailItemFolder folder)
    {
        if (folder == null)
            return false;

        var locals = await _exchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false);
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

    private async Task<List<MailCopy>> SynchronizeFolderItemsAsync(ExchangeService service, MailItemFolder folder, CancellationToken cancellationToken)
    {
        var downloaded = new List<MailCopy>();
        var syncState = folder.DeltaToken;
        var folderId = new FolderId(folder.RemoteFolderId);
        bool moreAvailable;

        _logger.Debug("Synchronizing items for folder {FolderName} ({Account}).", folder.FolderName, Account.Name);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var changes = await service.SyncFolderItems(folderId, ItemMetadataPropertySet, null,
                (int)InitialMessageDownloadCountPerFolder, SyncFolderItemsScope.NormalItems, syncState).ConfigureAwait(false);

            _logger.Debug("SyncFolderItems returned {Count} change(s) for {FolderName}; more available: {MoreAvailable}.",
                changes.Count, folder.FolderName, changes.MoreChangesAvailable);

            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case ChangeType.Create:
                    case ChangeType.Update:
                        if (change.Item == null) break;
                        var packages = await CreateNewMailPackagesAsync(change.Item, folder, cancellationToken).ConfigureAwait(false);
                        if (packages?.Count > 0)
                        {
                            foreach (var package in packages)
                            {
                                if (await _exchangeChangeProcessor.CreateMailAsync(Account.Id, package).ConfigureAwait(false))
                                    downloaded.Add(package.Copy);
                            }
                        }
                        break;
                    case ChangeType.Delete:
                        await _exchangeChangeProcessor.DeleteMailsAsync(Account.Id, [change.ItemId.UniqueId]).ConfigureAwait(false);
                        break;
                    case ChangeType.ReadFlagChange:
                        await _exchangeChangeProcessor.ChangeMailReadStatusAsync(change.ItemId.UniqueId, change.IsRead).ConfigureAwait(false);
                        break;
                }
            }

            syncState = changes.SyncState;
            moreAvailable = changes.MoreChangesAvailable;
        }
        while (moreAvailable);

        folder.DeltaToken = syncState;
        await _exchangeChangeProcessor.UpdateFolderAsync(folder).ConfigureAwait(false);
        await _exchangeChangeProcessor.UpdateFolderLastSyncDateAsync(folder.Id).ConfigureAwait(false);

        _logger.Debug("Folder {FolderName} item sync complete. Downloaded {Count} item(s).", folder.FolderName, downloaded.Count);

        return downloaded;
    }

    /// <summary>
    /// Reads an EWS property that may not have been loaded into the item's property bag, yielding the
    /// default instead of throwing ServiceObjectPropertyException.
    /// </summary>
    private static T SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch (ServiceObjectPropertyException) { return default; }
    }

    private MailCopy MapToMailCopy(Item item, MailItemFolder assignedFolder)
    {
        if (item == null)
            return null;

        var email = item as EmailMessage;

        return new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Id = item.Id.UniqueId,
            FolderId = assignedFolder.Id,
            // Every read below goes through SafeGet: reading a property EWS didn't load throws
            // ServiceObjectPropertyException, and one such item would otherwise fail the whole folder's sync.
            ThreadId = SafeGet(() => item.ConversationId?.UniqueId),
            MessageId = SafeGet(() => email?.InternetMessageId),
            Subject = SafeGet(() => item.Subject),
            FromName = SafeGet(() => email?.From?.Name),
            FromAddress = SafeGet(() => email?.From?.Address),
            CreationDate = SafeGet(() => item.DateTimeReceived.ToUniversalTime()),
            IsRead = SafeGet(() => email?.IsRead) ?? true,
            IsFlagged = SafeGet(() => item.Flag?.FlagStatus) == ItemFlagStatus.Flagged,
            HasAttachments = SafeGet(() => (bool?)item.HasAttachments) ?? false,
            Importance = MapImportance(SafeGet(() => (Importance?)item.Importance) ?? Microsoft.Exchange.WebServices.Data.Importance.Normal),
            // Items synced from the Drafts folder must carry IsDraft so selecting one opens the composer
            // (not the read view) and the compose/send flow treats it as an editable draft.
            IsDraft = assignedFolder?.SpecialFolderType == SpecialFolderType.Draft,
        };
    }

    protected static MailImportance MapImportance(Importance importance) => importance switch
    {
        Importance.High => MailImportance.High,
        Importance.Low => MailImportance.Low,
        _ => MailImportance.Normal,
    };

    public override async Task DownloadMissingMimeMessageAsync(MailCopy mailItem, MailKit.ITransferProgress transferProgress = null, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync().ConfigureAwait(false);

        var ewsItem = await Item.Bind(service, new ItemId(mailItem.Id),
            new PropertySet(ItemSchema.MimeContent)).ConfigureAwait(false);

        using var stream = new MemoryStream(ewsItem.MimeContent.Content);
        var mimeMessage = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        await _exchangeChangeProcessor.SaveMimeFileAsync(mailItem.FileId, mimeMessage, Account.Id).ConfigureAwait(false);
    }

    #region Mail & Folder Operations

    // Wraps an EWS operation into a single request bundle (EWS is stateless HTTP; one
    // service handles the whole batch, so per-action batching collapses to one bundle).
    protected static List<IRequestBundle<EwsRequest>> Bundle(Func<ExchangeService, Task> action, IRequestBase request, IUIChangeRequest uiChangeRequest)
        => [new EwsRequestBundle(new EwsRequest((service, _) => action(service), request), request, uiChangeRequest)];

    public override List<IRequestBundle<EwsRequest>> MarkRead(BatchMarkReadRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isRead = requests[0].IsRead;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var message = await EmailMessage.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, EmailMessageSchema.IsRead)).ConfigureAwait(false);
                if (message.IsRead == isRead) continue;
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

        return Bundle(async service =>
        {
            foreach (var id in ids)
            {
                var item = await Item.Bind(service, id, new PropertySet(BasePropertySet.IdOnly, ItemSchema.Flag)).ConfigureAwait(false);
                item.Flag = new Flag { FlagStatus = flagged ? ItemFlagStatus.Flagged : ItemFlagStatus.NotFlagged };
                await item.Update(ConflictResolutionMode.AutoResolve).ConfigureAwait(false);
            }

            // EWS doesn't echo flag changes back through SyncFolderItems the way it does read state,
            // so persist the flag locally after the server update succeeds; otherwise it's lost on
            // reload and never reaches the mail-list row.
            foreach (var request in requests)
                await _exchangeChangeProcessor.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
        }, requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Move(BatchMoveRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var destination = new FolderId(requests[0].ToFolder.RemoteFolderId);
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(service => service.MoveItems(ids, destination), requests[0], requests);
    }

    // Junk on Exchange goes through EWS MarkAsJunk (2013+): it adds (isJunk) / removes the sender of each
    // item to/from the mailbox's server-side Blocked Senders list AND moves the item to Junk / Inbox in one
    // call. The optimistic local move is handled by the request flow. (MarkAsJunk only touches Blocked
    // Senders; there's no EWS surface for the Safe Senders list, so "Never block" only un-blocks here.)
    public override List<IRequestBundle<EwsRequest>> ChangeJunkState(BatchChangeJunkStateRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var isJunk = requests[0].IsJunk;
        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(service => service.MarkAsJunk(ids, isJunk, true, default), requests[0], requests);
    }

    public override List<IRequestBundle<EwsRequest>> Delete(BatchDeleteRequest requests)
    {
        if (requests == null || requests.Count == 0)
            return [];

        var ids = requests.Select(r => new ItemId(r.Item.Id)).ToList();

        return Bundle(
            service => service.DeleteItems(ids, DeleteMode.MoveToDeletedItems, SendCancellationsMode.SendToNone, AffectedTaskOccurrence.AllOccurrences),
            requests[0], requests);
    }

    // Archive moves to the local Archive special folder (Exchange has no archive action of its own on the
    // primary mailbox; the in-place archive mailbox is populated server-side by retention policy).
    public override List<IRequestBundle<EwsRequest>> Archive(BatchArchiveRequest request)
        => Move(new BatchMoveRequest(request.Select(a => new MoveRequest(a.Item, a.FromFolder, a.ToFolder))));

    public override List<IRequestBundle<EwsRequest>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(a => new DeleteRequest(a))));

    public override List<IRequestBundle<EwsRequest>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(a => new MarkReadRequest(a, true))));

    public override List<IRequestBundle<EwsRequest>> SendDraft(SendDraftRequest request)
    {
        var preparation = request.Request;

        return Bundle(async service =>
        {
            var mime = preparation.Mime;

            // Strip the local-draft marker so it never leaks to recipients.
            mime.Headers.Remove(Domain.Constants.WinoLocalDraftHeader);

            using var stream = new MemoryStream();
            await mime.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            // On-prem Exchange cannot send as proxy aliases; let transport use the mailbox primary.
            if (preparation.SentFolder != null)
                await message.SendAndSaveCopy(new FolderId(preparation.SentFolder.RemoteFolderId)).ConfigureAwait(false);
            else
                await message.SendAndSaveCopy().ConfigureAwait(false);

            // Best-effort cleanup of the server draft created by CreateDraft.
            var serverDraftId = preparation.MailItem?.Id;
            if (!string.IsNullOrWhiteSpace(serverDraftId) && !(preparation.MailItem?.IsLocalDraft ?? true))
            {
                try
                {
                    await service.DeleteItems(
                        [new ItemId(serverDraftId)],
                        DeleteMode.HardDelete,
                        SendCancellationsMode.SendToNone,
                        AffectedTaskOccurrence.AllOccurrences).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not delete server draft {DraftId} after send (it may already be gone).", serverDraftId);
                }
            }
        }, request, request);
    }

    public override List<IRequestBundle<EwsRequest>> CreateDraft(CreateDraftRequest request)
    {
        var preparation = request.DraftPreperationRequest;
        var draftsFolderId = preparation.CreatedLocalDraftCopy.AssignedFolder.RemoteFolderId;

        return Bundle(async service =>
        {
            using var stream = new MemoryStream();
            await preparation.CreatedLocalDraftMimeMessage.WriteToAsync(stream).ConfigureAwait(false);

            var message = new EmailMessage(service)
            {
                MimeContent = new Microsoft.Exchange.WebServices.Data.MimeContent("UTF-8", stream.ToArray())
            };

            await message.Save(new FolderId(draftsFolderId)).ConfigureAwait(false);

            var isMapped = await _exchangeChangeProcessor.MapLocalDraftAsync(
                Account.Id,
                preparation.CreatedLocalDraftCopy.UniqueId,
                message.Id.UniqueId,
                message.Id.UniqueId,
                preparation.CreatedLocalDraftCopy.ThreadId).ConfigureAwait(false);

            if (!isMapped)
            {
                // The local draft was discarded while the EWS save was in flight. Delete the
                // server draft right away so the next Drafts sync cannot resurrect it.
                await service.DeleteItems(
                    [message.Id],
                    DeleteMode.HardDelete,
                    SendCancellationsMode.SendToNone,
                    AffectedTaskOccurrence.AllOccurrences).ConfigureAwait(false);
            }
        }, request, request);
    }

    protected override Task MarkDraftSyncFailedAsync(Guid mailUniqueId, string error)
        => _exchangeChangeProcessor.MarkDraftSyncFailedAsync(mailUniqueId, error);

    #endregion

    public override async Task ExecuteNativeRequestsAsync(List<IRequestBundle<EwsRequest>> batchedRequests, CancellationToken cancellationToken = default)
    {
        if (batchedRequests == null || batchedRequests.Count == 0)
            return;

        ApplyOptimisticUiChanges(batchedRequests);

        var service = await CreateServiceAsync().ConfigureAwait(false);
        var errors = new List<string>();

        for (int i = 0; i < batchedRequests.Count; i++)
        {
            var bundle = batchedRequests[i];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await bundle.NativeRequest.IntegratorTask(service, bundle.NativeRequest.Request).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled before/while executing this bundle: its optimistic UI change, and those of every
                // bundle after it, was applied up front but never confirmed by the server, so revert them. The
                // bundles before i already executed successfully and stay applied.
                for (int j = i; j < batchedRequests.Count; j++)
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
            await _exchangeChangeProcessor
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
            IsEntityNotFound = IsEwsEntityNotFound(exception, bundle.UIChangeRequest),
            OperationType = "ExchangeExecuteRequest"
        };

        var handled = await _errorHandlerFactory.HandleErrorAsync(errorContext).ConfigureAwait(false);

        // Handled non-transient errors are owned by their handlers.
        if (!handled || errorContext.Severity == SynchronizerErrorSeverity.Transient)
        {
            CaptureSynchronizationIssue(errorContext);
            RequestUiChangeCoordinator.RevertBundle(bundle);
            _logger.Error(exception, "Exchange request execution failed for {Account}.", Account.Name);
            errors.Add(exception.Message);
        }
    }

    private static bool IsEwsEntityNotFound(Exception exception, IUIChangeRequest uiChangeRequest)
    {
        if (uiChangeRequest == null || !IsExistingEntityOperation(uiChangeRequest))
            return false;

        for (var current = exception; current != null; current = current.InnerException)
        {
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
}
