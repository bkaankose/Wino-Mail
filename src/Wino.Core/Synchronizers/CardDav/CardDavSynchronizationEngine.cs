using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Synchronizers.CardDav;

/// <summary>
/// Synchronizes CardDAV address books. The server is the source of truth: a pull replaces
/// local state with what the server reports, and a user change is written to the server
/// first and stored locally only once the server accepted it.
/// </summary>
public sealed class CardDavSynchronizationEngine : ICardDavSynchronizationEngine
{
    private const int DownloadBatchSize = 50;
    private const int ConcurrentDownloads = 4;
    private static readonly ILogger Logger = Log.ForContext<CardDavSynchronizationEngine>();
    private readonly ICardDavContactListService _listService;

    // Collections whose server advertised a REPORT and then refused it. Discovery would
    // advertise it again, so the refusal is remembered for the process instead.
    private static readonly ConcurrentDictionary<string, byte> SyncCollectionRejected = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> MultigetRejected = new(StringComparer.Ordinal);
    private readonly ICardDavClient _client;
    private readonly ICardDavSynchronizationStore _store;
    private readonly IVCardCodec _codec;
    private readonly IContactService _contactService;
    private readonly IWinoLogger _logger;
    private readonly IDavCredentialStore _credentialStore;
    private readonly IAccountService _accountService;
    private readonly ICardDavAddressBookService _addressBookService;

    public CardDavSynchronizationEngine(
        ICardDavClient client,
        ICardDavSynchronizationStore store,
        IVCardCodec codec,
        IContactService contactService,
        IWinoLogger logger,
        IDavCredentialStore credentialStore,
        IAccountService accountService,
        ICardDavAddressBookService addressBookService = null,
        ICardDavContactListService listService = null)
    {
        _listService = listService;
        _client = client;
        _store = store;
        _codec = codec;
        _contactService = contactService;
        _logger = logger;
        _credentialStore = credentialStore;
        _accountService = accountService;
        _addressBookService = addressBookService;
    }

    #region Synchronization

    public async Task<ContactSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        ContactSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var result = ContactSynchronizationResult.Empty;

        try
        {
            var settings = await CardDavConnection.CreateSettingsAsync(account, _credentialStore, _accountService, cancellationToken).ConfigureAwait(false);
            var legacyPassword = account.ServerInformation?.CalDavPassword;
            var discovery = await RefreshAddressBooksAsync(account, settings, cancellationToken).ConfigureAwait(false);
            var remoteBooks = discovery.AddressBooks.ToDictionary(book => book.ExactHref, StringComparer.Ordinal);
            var bindings = await _store.GetAddressBooksAsync(account.Id, options?.AddressBookId).ConfigureAwait(false);

            foreach (var binding in bindings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!remoteBooks.TryGetValue(binding.State.ExactHref, out var remoteBook))
                    continue;

                await SynchronizeBookAsync(
                    new BookContext(account.Id, settings, binding, remoteBook),
                    options?.Type == ContactSynchronizationType.Full,
                    result,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(legacyPassword) && result.CompletedState != SynchronizationCompletedState.Failed)
            {
                await _credentialStore.SavePasswordAsync(account.Id, legacyPassword, cancellationToken).ConfigureAwait(false);
                account.ServerInformation.CalDavPassword = null;
                await _accountService.UpdateAccountCustomServerInformationAsync(account.ServerInformation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ContactSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "CardDavSynchronization");
            return ContactSynchronizationResult.Failed(ex).MergeIssues([Classify(ex, "Account")]);
        }

        return result;
    }

    /// <summary>
    /// Lists the address-book home on every synchronization. One request shows added,
    /// renamed and removed address books and, through the sync token and collection tag,
    /// which of them changed at all. The principal and home are only resolved again when
    /// they are unknown, expired or no longer answer.
    /// </summary>
    private async Task<CardDavDiscoveryResult> RefreshAddressBooksAsync(
        MailAccount account,
        CardDavConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        var state = await _store.GetAccountStateAsync(account.Id).ConfigureAwait(false);
        CardDavDiscoveryResult discovery = null;

        if (state is { RequiresRediscovery: false } && !string.IsNullOrWhiteSpace(state.AddressBookHomeHref) &&
            state.DiscoveryExpiresUtc > DateTime.UtcNow)
        {
            try
            {
                discovery = await _client.ListAddressBooksAsync(settings, new Uri(state.AddressBookHomeHref), cancellationToken).ConfigureAwait(false);
            }
            catch (DavRequestException ex) when (ex.StatusCode is not (401 or 429) and < 500)
            {
                Logger.Information("CardDAV home {Home} no longer answers ({StatusCode}); rediscovering", state.AddressBookHomeHref, ex.StatusCode);
            }
        }

        discovery ??= await _client.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);

        var removedBookIds = await _store.SaveDiscoveryAsync(account.Id, discovery).ConfigureAwait(false);
        foreach (var addressBookId in removedBookIds)
            await _contactService.DeleteAddressBookAsync(addressBookId).ConfigureAwait(false);

        return discovery;
    }

    private async Task SynchronizeBookAsync(BookContext context, bool forceFull, ContactSynchronizationResult result, CancellationToken cancellationToken)
    {
        var state = context.Binding.State;
        try
        {
            using var bookLock = await CardDavConnection.LockAddressBookAsync(state.AddressBookId, cancellationToken).ConfigureAwait(false);

            if (!forceFull && state.LastSyncUtc.HasValue && IsUnchanged(state, context.RemoteBook))
            {
                Logger.Debug("CardDAV address book {AddressBook} is unchanged", context.Binding.AddressBook.DisplayName);
                return;
            }

            var known = (await _store.GetResourcesAsync(state.AddressBookId).ConfigureAwait(false))
                .ToDictionary(resource => resource.ExactHref, StringComparer.Ordinal);

            if (!forceFull && CanUseSyncCollection(state) && !string.IsNullOrEmpty(state.SyncToken))
            {
                try
                {
                    await PullChangesAsync(context, known, result, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (DavRequestException ex) when (ex.HasError("valid-sync-token") || ex.StatusCode is 403 or 409 or 410 or 412 || ex.IsUnsupportedRequest)
                {
                    // The server no longer knows the token (RFC 6578 section 3.2). What
                    // changed since is unknown, so compare against a complete listing.
                    Logger.Information("CardDAV sync token for {AddressBook} was refused ({StatusCode}); listing the collection", context.Binding.AddressBook.DisplayName, ex.StatusCode);
                }
            }

            await PullEverythingAsync(context, known, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "CardDavAddressBookSynchronization", new Dictionary<string, string>
            {
                ["AccountId"] = context.AccountId.ToString("D"),
                ["AddressBookId"] = state.AddressBookId.ToString("D"),
                ["StatusCode"] = (ex as DavRequestException)?.StatusCode.ToString()
            });
            result.MergeIssues([Classify(ex, context.Binding.AddressBook.DisplayName)]);
        }
    }

    /// <summary>
    /// The checkpoint stored after the last synchronization still describes the server.
    /// A server that names its sync token differently in PROPFIND and REPORT never matches
    /// here and is simply asked for changes.
    /// </summary>
    private static bool IsUnchanged(CardDavAddressBookState state, CardDavAddressBook remote)
        => (!string.IsNullOrEmpty(remote.SyncToken) && string.Equals(remote.SyncToken, state.SyncToken, StringComparison.Ordinal)) ||
           (!string.IsNullOrEmpty(remote.CollectionTag) && string.Equals(remote.CollectionTag, state.CollectionTag, StringComparison.Ordinal));

    private static bool CanUseSyncCollection(CardDavAddressBookState state)
        => state.SupportsSyncCollection && !SyncCollectionRejected.ContainsKey(state.ExactHref);

    /// <summary>Delta: asks for what changed since the stored token and applies it page by page.</summary>
    private async Task PullChangesAsync(
        BookContext context,
        Dictionary<string, CardDavResourceShadow> known,
        ContactSynchronizationResult result,
        CancellationToken cancellationToken)
    {
        var token = context.Binding.State.SyncToken;
        CardDavSyncPage page;
        do
        {
            page = await _client.SyncCollectionAsync(context.Settings, context.ProtocolBook, token, cancellationToken).ConfigureAwait(false);
            EnsureUsableToken(page, token);

            var deleted = page.Changes.Where(change => change.IsDeleted).Select(change => change.ExactHref).ToList();
            var changed = page.Changes.Where(change => !change.IsDeleted && !IsCurrent(known, change)).ToList();
            Logger.Information("CardDAV delta for {AddressBook}: {Changed} changed, {Deleted} deleted",
                context.Binding.AddressBook.DisplayName, changed.Count, deleted.Count);

            // Each page carries the token that continues after it, so a page is a
            // complete unit of work and its token can be stored with it.
            await DownloadAndApplyAsync(context, known, changed, deleted, page.NextSyncToken, result, cancellationToken).ConfigureAwait(false);
            token = page.NextSyncToken;
        } while (page.IsTruncated);
    }

    /// <summary>
    /// Initial or recovery synchronization: lists every resource, downloads the ones that
    /// are new or changed, and removes local resources the listing no longer contains.
    /// </summary>
    private async Task PullEverythingAsync(
        BookContext context,
        Dictionary<string, CardDavResourceShadow> known,
        ContactSynchronizationResult result,
        CancellationToken cancellationToken)
    {
        var state = context.Binding.State;
        List<CardDavResourceChange> listing = null;
        string token = null;

        if (CanUseSyncCollection(state))
        {
            try
            {
                // An empty token lists the whole collection and returns the token that
                // describes exactly that listing.
                listing = [];
                CardDavSyncPage page;
                do
                {
                    page = await _client.SyncCollectionAsync(context.Settings, context.ProtocolBook, token, cancellationToken).ConfigureAwait(false);
                    EnsureUsableToken(page, token);
                    listing.AddRange(page.Changes.Where(change => !change.IsDeleted));
                    token = page.NextSyncToken;
                } while (page.IsTruncated);
            }
            catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
            {
                SyncCollectionRejected.TryAdd(state.ExactHref, 0);
                Logger.Information("CardDAV server refused sync-collection for {AddressBook} ({StatusCode}); using PROPFIND", context.Binding.AddressBook.DisplayName, ex.StatusCode);
                listing = null;
                token = null;
            }
        }

        listing ??= [.. await _client.EnumerateResourcesAsync(context.Settings, context.ProtocolBook, cancellationToken).ConfigureAwait(false)];

        var listed = listing.Select(resource => resource.ExactHref).ToHashSet(StringComparer.Ordinal);
        var deleted = known.Keys.Where(href => !listed.Contains(href)).ToList();
        var changed = listing.Where(resource => !IsCurrent(known, resource)).ToList();
        Logger.Information("CardDAV listing for {AddressBook}: {Listed} resources, {Changed} to download, {Deleted} to remove",
            context.Binding.AddressBook.DisplayName, listing.Count, changed.Count, deleted.Count);

        await DownloadAndApplyAsync(context, known, changed, deleted, token, result, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureUsableToken(CardDavSyncPage page, string previousToken)
    {
        if (string.IsNullOrWhiteSpace(page.NextSyncToken) ||
            (page.IsTruncated && string.Equals(page.NextSyncToken, previousToken, StringComparison.Ordinal)))
            throw new DavRequestException(0, Translator.DavError_InvalidResponse);
    }

    /// <summary>The version already applied locally. Includes resources this client just wrote.</summary>
    private static bool IsCurrent(Dictionary<string, CardDavResourceShadow> known, CardDavResourceChange remote)
        => !string.IsNullOrEmpty(remote.ETag) &&
           known.TryGetValue(remote.ExactHref, out var resource) &&
           string.Equals(resource.ETag, remote.ETag, StringComparison.Ordinal);

    /// <summary>
    /// Downloads the changed resources in batches and applies them. The checkpoint is
    /// stored with the last batch, so an interrupted run is repeated from the old one.
    /// </summary>
    private async Task DownloadAndApplyAsync(
        BookContext context,
        Dictionary<string, CardDavResourceShadow> known,
        IReadOnlyList<CardDavResourceChange> changed,
        IReadOnlyList<string> deleted,
        string syncToken,
        ContactSynchronizationResult result,
        CancellationToken cancellationToken)
    {
        var addressBookId = context.Binding.State.AddressBookId;
        var offset = 0;
        do
        {
            var batch = changed.Skip(offset).Take(DownloadBatchSize).ToList();
            offset += batch.Count;
            var isLastBatch = offset >= changed.Count;

            var (downloaded, gone) = await DownloadAsync(context, batch, cancellationToken).ConfigureAwait(false);
            var contacts = new List<(AccountContact Contact, string Uid)>();
            var groups = new List<CardDavRemoteGroup>();

            // Deletions are held back to the last batch only because they need no download.
            var deletedHrefs = isLastBatch ? deleted.ToList() : [];
            var deletedContactHrefs = new List<string>(deletedHrefs);

            foreach (var resource in batch)
            {
                if (gone.Contains(resource.ExactHref))
                {
                    // Listed a moment ago, removed by the time it was requested.
                    deletedHrefs.Add(resource.ExactHref);
                    deletedContactHrefs.Add(resource.ExactHref);
                    continue;
                }

                if (!downloaded.TryGetValue(resource.ExactHref, out var body))
                {
                    Logger.Warning("CardDAV resource {Href} was listed but returned no vCard", resource.ExactHref);
                    continue;
                }

                VCardDocument document;
                try
                {
                    document = _codec.Parse(body.VCard);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
                {
                    // An unreadable card is left out. It is retried when the resource
                    // changes or the collection is listed again.
                    Logger.Warning(ex, "CardDAV resource {Href} is not a readable vCard", resource.ExactHref);
                    continue;
                }

                var etag = string.IsNullOrEmpty(body.ETag) ? resource.ETag : body.ETag;
                var uid = _codec.GetUid(document);

                if (_codec.IsGroup(document))
                {
                    groups.Add(new CardDavRemoteGroup(resource.ExactHref, etag, uid, _codec.GetGroupName(document), _codec.GetGroupMembers(document)));

                    // Stored as a person before groups were recognized.
                    if (known.TryGetValue(resource.ExactHref, out var previous) && previous.Kind == CardDavResourceKind.Contact)
                        deletedContactHrefs.Add(resource.ExactHref);
                    continue;
                }

                var contact = _codec.Project(document);
                if (!document.Properties.Any(property => property.Name == "FN" && !string.IsNullOrWhiteSpace(property.Value)))
                    Logger.Debug("CardDAV contact without FN; properties: {Properties}", string.Join(",", document.Properties.Select(property => property.Name)));

                contact.MailAccountId = context.AccountId;
                contact.SourceKind = ContactSourceKind.CardDav;
                contact.RemoteId = resource.ExactHref;
                contact.RemoteVersion = etag;
                contacts.Add((contact, uid));
            }

            // Contact rows first: the store records the local id each resource ended up with.
            await _contactService.ApplyDeltaAsync(
                addressBookId,
                new ContactSynchronizationBatch(contacts.Select(item => item.Contact).ToList(), deletedContactHrefs, null),
                commitDeltaToken: false).ConfigureAwait(false);
            await _store.ApplyChangesAsync(new CardDavChangeSet
            {
                AccountId = context.AccountId,
                AddressBookId = addressBookId,
                Contacts = contacts.Select(item => new CardDavRemoteContact(item.Contact.RemoteId, item.Contact.RemoteVersion, item.Uid, item.Contact.Id)).ToList(),
                Groups = groups,
                DeletedHrefs = deletedHrefs,
                CommitCheckpoint = isLastBatch,
                SyncToken = syncToken,
                CollectionTag = context.RemoteBook.CollectionTag
            }).ConfigureAwait(false);

            result.DownloadedCount += contacts.Count + groups.Count;
            result.ChangedCount += contacts.Count + groups.Count;
            result.DeletedCount += deletedHrefs.Count(known.ContainsKey);
        } while (offset < changed.Count);
    }

    /// <summary>
    /// Fetches vCards with one multiget REPORT, then with GET for whatever the REPORT did
    /// not return: every resource when the server has no multiget, single ones when it
    /// answered without a body. Also reports the resources the server no longer has.
    /// </summary>
    private async Task<(Dictionary<string, CardDavResourceChange> Downloaded, HashSet<string> Gone)> DownloadAsync(
        BookContext context,
        IReadOnlyList<CardDavResourceChange> resources,
        CancellationToken cancellationToken)
    {
        var downloaded = new Dictionary<string, CardDavResourceChange>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        if (resources.Count == 0)
            return (downloaded, gone);

        var state = context.Binding.State;

        if (state.SupportsMultiget && !MultigetRejected.ContainsKey(state.ExactHref))
        {
            try
            {
                var hrefs = resources.Select(resource => resource.ExactHref).ToList();
                foreach (var resource in await _client.MultiGetAsync(context.Settings, context.ProtocolBook, hrefs, cancellationToken).ConfigureAwait(false))
                {
                    if (resource.IsDeleted) gone.Add(resource.ExactHref);
                    else if (!string.IsNullOrEmpty(resource.VCard)) downloaded[resource.ExactHref] = resource;
                }
            }
            catch (DavRequestException ex) when (ex.IsUnsupportedRequest)
            {
                MultigetRejected.TryAdd(state.ExactHref, 0);
                Logger.Information("CardDAV server refused addressbook-multiget for {AddressBook} ({StatusCode}); using GET", context.Binding.AddressBook.DisplayName, ex.StatusCode);
            }
        }

        var remaining = resources.Select(resource => resource.ExactHref)
            .Where(href => !downloaded.ContainsKey(href) && !gone.Contains(href))
            .ToList();
        if (remaining.Count == 0)
            return (downloaded, gone);

        using var throttle = new SemaphoreSlim(ConcurrentDownloads, ConcurrentDownloads);
        var fetched = await Task.WhenAll(remaining.Select(async href =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await _client.GetResourceAsync(context.Settings, href, cancellationToken).ConfigureAwait(false); }
            finally { throttle.Release(); }
        })).ConfigureAwait(false);

        foreach (var resource in fetched)
        {
            if (resource.IsDeleted) gone.Add(resource.ExactHref);
            else if (!string.IsNullOrEmpty(resource.VCard)) downloaded[resource.ExactHref] = resource;
        }

        return (downloaded, gone);
    }

    #endregion

    #region User changes

    public async Task ExecuteRequestsAsync(
        MailAccount account,
        IReadOnlyList<IContactActionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var settings = await CardDavConnection.CreateSettingsAsync(account, _credentialStore, _accountService, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (request)
            {
                case AddressBookActionRequest addressBookRequest:
                    await ExecuteAddressBookRequestAsync(addressBookRequest, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactActionRequest contactRequest:
                    await ExecuteContactRequestAsync(settings, contactRequest, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactCategoryRequest categoryRequest:
                    await ExecuteCategoryRequestAsync(settings, categoryRequest, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactListRequest listRequest:
                    await ExecuteListRequestAsync(listRequest, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException($"CardDAV request {request.GetType().Name} is not supported.");
            }
        }
    }

    /// <summary>
    /// Writes a list change to its group on the server. The local list only follows a
    /// change the server accepted.
    /// </summary>
    private async Task ExecuteListRequestAsync(ContactListRequest request, CancellationToken cancellationToken)
    {
        if (_listService is null)
            throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable);

        switch (request.Operation)
        {
            case ContactSynchronizerOperation.CreateList:
                await _listService.CreateAsync(request.List, cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.RenameList:
                await _listService.RenameAsync(request.List.Id, request.List.Name, cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.DeleteList:
                await _listService.DeleteAsync(request.List.Id, cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.UpdateListMembers:
                await _listService.UpdateMembersAsync(request.List.Id, request.AddedContactIds, request.RemovedContactIds, cancellationToken).ConfigureAwait(false);
                break;
        }

        await request.CommitAsync(_contactService).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the categories of one contact to its card. The card is edited as the server
    /// has it now, so everything else on it is kept.
    /// </summary>
    private async Task ExecuteCategoryRequestAsync(
        CardDavConnectionSettings settings,
        ContactCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var contact = await _contactService.GetContactAsync(request.LocalContactId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(contact?.RemoteId))
            throw new InvalidOperationException(Translator.DavError_NotFound);

        var binding = (await _store.GetAddressBooksAsync(contact.MailAccountId, contact.AddressBookId).ConfigureAwait(false)).SingleOrDefault()
                      ?? throw new InvalidOperationException(Translator.DavError_NotFound);

        if (binding.State.IsReadOnly)
            throw new InvalidOperationException(Translator.DavError_ReadOnly);

        using var bookLock = await CardDavConnection.LockAddressBookAsync(binding.State.AddressBookId, cancellationToken).ConfigureAwait(false);

        var current = await _client.GetResourceAsync(settings, contact.RemoteId, cancellationToken).ConfigureAwait(false);
        if (current.IsDeleted)
            throw new DavRequestException(404, Translator.DavError_NotFound);

        var document = _codec.Parse(current.VCard);
        _codec.SetCategories(document, request.CategoryNames);
        var written = await _client.PutResourceAsync(
            settings,
            contact.RemoteId,
            _codec.Serialize(document),
            current.ETag,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var resource = await _store.GetResourceByContactAsync(contact.Id).ConfigureAwait(false);
        if (resource is not null)
        {
            resource.ETag = written.ETag;
            await _store.SaveResourceAsync(resource).ConfigureAwait(false);
        }

        await _contactService.SetContactCategoriesAsync(contact.Id, request.CategoryNames).ConfigureAwait(false);
    }

    private async Task ExecuteContactRequestAsync(
        CardDavConnectionSettings settings,
        ContactActionRequest request,
        CancellationToken cancellationToken)
    {
        var contact = RequestEntityCloner.Contact(request.Contact);
        var binding = (await _store.GetAddressBooksAsync(request.MailAccountId, request.AddressBookId).ConfigureAwait(false)).SingleOrDefault()
                      ?? throw new InvalidOperationException(Translator.DavError_NotFound);

        if (binding.State.IsReadOnly)
            throw new InvalidOperationException(Translator.DavError_ReadOnly);

        using var bookLock = await CardDavConnection.LockAddressBookAsync(binding.State.AddressBookId, cancellationToken).ConfigureAwait(false);

        switch (request.Operation)
        {
            case ContactSynchronizerOperation.Create:
            {
                var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();
                var document = _codec.Create(contact, binding.State.SupportsVCard4 ? "4.0" : "3.0", uid);
                var written = await _client.PutResourceAsync(
                    settings,
                    $"{binding.State.ExactHref.TrimEnd('/')}/{uid}.vcf",
                    _codec.Serialize(document),
                    createOnly: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await CompleteWriteAsync(binding, contact, written, uid).ConfigureAwait(false);
                break;
            }
            case ContactSynchronizerOperation.Update:
            {
                if (string.IsNullOrWhiteSpace(contact.RemoteId))
                    throw new InvalidOperationException(Translator.DavError_NotFound);

                // Edit the card as the server has it now. Properties Wino does not model
                // (photo, labels, custom fields) are kept, and the write is conditional on
                // exactly the version that was read.
                var current = await _client.GetResourceAsync(settings, contact.RemoteId, cancellationToken).ConfigureAwait(false);
                if (current.IsDeleted)
                    throw new DavRequestException(404, Translator.DavError_NotFound);

                var document = _codec.Parse(current.VCard);
                _codec.Patch(document, contact);
                var written = await _client.PutResourceAsync(
                    settings,
                    contact.RemoteId,
                    _codec.Serialize(document),
                    current.ETag,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                await CompleteWriteAsync(binding, contact, written, _codec.GetUid(document)).ConfigureAwait(false);
                break;
            }
            case ContactSynchronizerOperation.Delete:
                if (!string.IsNullOrWhiteSpace(contact.RemoteId))
                {
                    await _client.DeleteResourceAsync(settings, contact.RemoteId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    await _store.DeleteResourceAsync(binding.State.AddressBookId, contact.RemoteId).ConfigureAwait(false);
                }

                await _contactService.CompleteMutationAsync(contact.Id, null, true).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.SetPhoto:
            case ContactSynchronizerOperation.DeletePhoto:
                throw new NotSupportedException(Translator.DavError_InvalidResponse);
            default:
                throw new NotSupportedException($"CardDAV contact operation {request.Operation} is not supported.");
        }
    }

    /// <summary>
    /// Stores an accepted write. Recording the returned ETag lets the next pull recognize
    /// the resource as current; without one the pull downloads the stored card again.
    /// </summary>
    private async Task CompleteWriteAsync(CardDavBookBinding binding, AccountContact contact, CardDavWriteResult written, string uid)
    {
        contact.RemoteId = written.ExactHref;
        contact.RemoteVersion = written.ETag;
        await _contactService.CompleteMutationAsync(contact.Id, contact, false).ConfigureAwait(false);
        await _store.SaveResourceAsync(new CardDavResourceShadow
        {
            AddressBookId = binding.State.AddressBookId,
            ExactHref = written.ExactHref,
            ETag = written.ETag,
            Uid = uid,
            Kind = CardDavResourceKind.Contact,
            ContactId = contact.Id
        }).ConfigureAwait(false);
    }

    private async Task ExecuteAddressBookRequestAsync(AddressBookActionRequest request, CancellationToken cancellationToken)
    {
        if (_addressBookService is null)
            throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable);

        switch (request.Operation)
        {
            case ContactSynchronizerOperation.CreateAddressBook:
                await _addressBookService.CreateAsync(request.MailAccountId, request.AddressBook.DisplayName, cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.RenameAddressBook:
                await _addressBookService.RenameAsync(request.AddressBookId, request.AddressBook.DisplayName, cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.DeleteAddressBook:
                await _addressBookService.DeleteAsync(request.AddressBookId, destructiveConfirmation: true, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    #endregion

    private static SynchronizationIssue Classify(Exception exception, string scope) => SynchronizationIssue.FromException(
        exception,
        "CardDavSynchronization",
        exception is DavRequestException authFailure && authFailure.StatusCode is 401 or 403 ? SynchronizerErrorSeverity.AuthRequired : SynchronizerErrorSeverity.Recoverable,
        exception switch
        {
            DavRequestException dav when dav.StatusCode is 401 or 403 => SynchronizerErrorCategory.Authentication,
            DavRequestException dav when dav.StatusCode == 429 => SynchronizerErrorCategory.RateLimit,
            DavRequestException dav when dav.StatusCode >= 500 => SynchronizerErrorCategory.ServerError,
            HttpRequestException => SynchronizerErrorCategory.Network,
            FormatException => SynchronizerErrorCategory.Validation,
            _ => SynchronizerErrorCategory.ProtocolError
        },
        scope);

    private sealed record BookContext(Guid AccountId, CardDavConnectionSettings Settings, CardDavBookBinding Binding, CardDavAddressBook RemoteBook)
    {
        public CardDavAddressBook ProtocolBook { get; } = CardDavConnection.ToProtocolBook(Binding);
    }
}
