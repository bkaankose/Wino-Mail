using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests;
using Wino.Core.Requests.Contact;

namespace Wino.Core.Synchronizers.CardDav;

public sealed class CardDavSynchronizationEngine : ICardDavSynchronizationEngine
{
    private const int PageSize = 250;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> CollectionLocks = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OriginGetLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICardDavClient _client;
    private readonly ICardDavSynchronizationStore _store;
    private readonly ICardDavPayloadStore _payloadStore;
    private readonly IVCardCodec _codec;
    private readonly IContactService _contactService;
    private readonly IWinoLogger _logger;
    private readonly IDavCredentialStore _credentialStore;
    private readonly IAccountService _accountService;
    private readonly ICardDavAddressBookService _addressBookService;

    public CardDavSynchronizationEngine(
        ICardDavClient client,
        ICardDavSynchronizationStore store,
        ICardDavPayloadStore payloadStore,
        IVCardCodec codec,
        IContactService contactService,
        IWinoLogger logger,
        IDavCredentialStore credentialStore,
        IAccountService accountService,
        ICardDavAddressBookService addressBookService = null)
    {
        _client = client;
        _store = store;
        _payloadStore = payloadStore;
        _codec = codec;
        _contactService = contactService;
        _logger = logger;
        _credentialStore = credentialStore;
        _accountService = accountService;
        _addressBookService = addressBookService;
    }

    public async Task ExecuteRequestsAsync(
        MailAccount account,
        IReadOnlyList<IContactActionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var settings = await CreateSettingsAsync(account, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (request is AddressBookActionRequest addressBookRequest)
            {
                await ExecuteAddressBookRequestAsync(addressBookRequest, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (request is not ContactActionRequest contactRequest)
                throw new NotSupportedException($"CardDAV request {request.GetType().Name} is not supported.");

            await ExecuteContactRequestAsync(settings, contactRequest, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteContactRequestAsync(
        CardDavConnectionSettings settings,
        ContactActionRequest request,
        CancellationToken cancellationToken)
    {
        var contact = RequestEntityCloner.Contact(request.Contact);
        var binding = (await _store.GetAddressBooksAsync(
            request.MailAccountId,
            request.AddressBookId).ConfigureAwait(false)).SingleOrDefault()
            ?? throw new InvalidOperationException(Translator.DavError_NotFound);

        if (binding.State.IsReadOnly)
            throw new InvalidOperationException(Translator.DavError_ReadOnly);

        switch (request.Operation)
        {
            case ContactSynchronizerOperation.Create:
            {
                var document = _codec.Create(contact, "4.0");
                var href = $"{binding.State.ExactHref.TrimEnd('/')}/{contact.Id:N}.vcf";
                var response = await _client.PutResourceAsync(
                    settings,
                    href,
                    _codec.Serialize(document),
                    createOnly: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                contact.RemoteId = response.ExactHref;
                contact.RemoteVersion = response.ETag;
                await _contactService.CompleteMutationAsync(contact.Id, contact, false).ConfigureAwait(false);
                break;
            }
            case ContactSynchronizerOperation.Update:
            {
                var current = await _client.GetResourceAsync(settings, contact.RemoteId, cancellationToken).ConfigureAwait(false);
                var document = _codec.Parse(current.VCard);
                _codec.Patch(document, contact);
                var response = await _client.PutResourceAsync(
                    settings,
                    contact.RemoteId,
                    _codec.Serialize(document),
                    contact.RemoteVersion,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                contact.RemoteId = response.ExactHref;
                contact.RemoteVersion = response.ETag;
                await _contactService.CompleteMutationAsync(contact.Id, contact, false).ConfigureAwait(false);
                break;
            }
            case ContactSynchronizerOperation.Delete:
                await _client.DeleteResourceAsync(
                    settings,
                    contact.RemoteId,
                    contact.RemoteVersion,
                    cancellationToken).ConfigureAwait(false);
                await _contactService.CompleteMutationAsync(contact.Id, null, true).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.SetPhoto:
            case ContactSynchronizerOperation.DeletePhoto:
                throw new NotSupportedException(Translator.DavError_InvalidResponse);
            default:
                throw new NotSupportedException($"CardDAV contact operation {request.Operation} is not supported.");
        }
    }

    private async Task ExecuteAddressBookRequestAsync(
        AddressBookActionRequest request,
        CancellationToken cancellationToken)
    {
        if (_addressBookService is null)
            throw new InvalidOperationException(Translator.Synchronizer_ContactsUnavailable);

        switch (request.Operation)
        {
            case ContactSynchronizerOperation.CreateAddressBook:
                await _addressBookService.CreateAsync(
                    request.MailAccountId,
                    request.AddressBook.DisplayName,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.RenameAddressBook:
                await _addressBookService.RenameAsync(
                    request.AddressBookId,
                    request.AddressBook.DisplayName,
                    cancellationToken).ConfigureAwait(false);
                break;
            case ContactSynchronizerOperation.DeleteAddressBook:
                await _addressBookService.DeleteAsync(
                    request.AddressBookId,
                    destructiveConfirmation: true,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public async Task<ContactSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        ContactSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var result = ContactSynchronizationResult.Empty;

        try
        {
            var settings = await CreateSettingsAsync(account, cancellationToken).ConfigureAwait(false);
            var legacyPassword = account.ServerInformation?.CalDavPassword;
            var accountState = await _store.GetAccountStateAsync(account.Id).ConfigureAwait(false);
            if (accountState is null || accountState.RequiresRediscovery || accountState.DiscoveryExpiresUtc <= DateTime.UtcNow)
            {
                var discovery = await _client.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
                await _store.SaveDiscoveryAsync(account.Id, discovery).ConfigureAwait(false);
            }

            var books = await _store.GetAddressBooksAsync(account.Id, options?.AddressBookId).ConfigureAwait(false);
            using var accountConcurrency = new SemaphoreSlim(2, 2);
            var bookResults = await Task.WhenAll(books.Select(async binding =>
            {
                await accountConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { return await SynchronizeBookAsync(account, settings, binding, cancellationToken).ConfigureAwait(false); }
                finally { accountConcurrency.Release(); }
            })).ConfigureAwait(false);

            foreach (var bookResult in bookResults)
            {
                result.DownloadedCount += bookResult.DownloadedCount;
                result.ChangedCount += bookResult.ChangedCount;
                result.DeletedCount += bookResult.DeletedCount;
                result.MergeIssues(bookResult.Issues);
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

    private async Task<ContactSynchronizationResult> SynchronizeBookAsync(
        MailAccount account,
        CardDavConnectionSettings settings,
        CardDavBookBinding binding,
        CancellationToken cancellationToken)
    {
        var gate = CollectionLocks.GetOrAdd(binding.AddressBook.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = ContactSynchronizationResult.Empty;
        try
        {
            var pulled = await PullAsync(account.Id, settings, binding, cancellationToken).ConfigureAwait(false);
            AddCounts(result, pulled);

            // User mutations execute directly through ExecuteRequestsAsync. Synchronization is
            // pull-only and never leases or retries a persistent CardDAV outbox.
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "CardDavAddressBookSynchronization", new Dictionary<string, string>
            {
                ["AccountId"] = account.Id.ToString("D"),
                ["AddressBookId"] = binding.AddressBook.Id.ToString("D")
            });
            result.MergeIssues([Classify(ex, binding.AddressBook.DisplayName)]);
        }
        finally
        {
            gate.Release();
        }

        return result;
    }

    private async Task<ContactSynchronizationResult> PullAsync(
        Guid accountId,
        CardDavConnectionSettings settings,
        CardDavBookBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.State.SupportsSyncCollection && !binding.State.RequiresFullReconciliation)
        {
            try
            {
                return await PullIncrementalAsync(accountId, settings, binding, cancellationToken).ConfigureAwait(false);
            }
            catch (DavRequestException ex) when (ex.HasError("valid-sync-token") || ex.StatusCode is 403 or 409)
            {
                // An invalid token never authorizes deletion. Rebuild from a complete listing.
            }
        }

        return await PullFullAsync(accountId, settings, binding, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContactSynchronizationResult> PullIncrementalAsync(
        Guid accountId,
        CardDavConnectionSettings settings,
        CardDavBookBinding binding,
        CancellationToken cancellationToken)
    {
        var result = ContactSynchronizationResult.Empty;
        var token = binding.State.SyncToken;
        do
        {
            var page = await _client.SyncCollectionAsync(settings, ToProtocolBook(binding), token, PageSize, cancellationToken).ConfigureAwait(false);
            var populated = await PopulateBodiesAsync(settings, binding, page.Changes, cancellationToken).ConfigureAwait(false);
            var remotePage = await BuildRemotePageAsync(accountId, binding, populated, 0, false, page.NextSyncToken, true, cancellationToken).ConfigureAwait(false);
            await _store.ApplyRemotePageAsync(remotePage).ConfigureAwait(false);
            AddPageCounts(result, remotePage);
            token = page.NextSyncToken;
            if (!page.IsTruncated) break;
        } while (true);
        return result;
    }

    private async Task<ContactSynchronizationResult> PullFullAsync(
        Guid accountId,
        CardDavConnectionSettings settings,
        CardDavBookBinding binding,
        CancellationToken cancellationToken)
    {
        var result = ContactSynchronizationResult.Empty;
        var generation = await _store.BeginFullReconciliationAsync(binding.AddressBook.Id).ConfigureAwait(false);
        if (binding.State.SupportsSyncCollection)
        {
            var seen = new List<string>();
            string token = null;
            do
            {
                var syncPage = await _client.SyncCollectionAsync(settings, ToProtocolBook(binding), token, PageSize, cancellationToken).ConfigureAwait(false);
                var populated = await PopulateBodiesAsync(settings, binding, syncPage.Changes, cancellationToken).ConfigureAwait(false);
                seen.AddRange(populated.Where(item => !item.IsDeleted).Select(item => item.ExactHref));
                var page = await BuildRemotePageAsync(accountId, binding, populated, generation, true, null, false, cancellationToken).ConfigureAwait(false);
                await _store.ApplyRemotePageAsync(page).ConfigureAwait(false);
                AddPageCounts(result, page);
                token = syncPage.NextSyncToken;
                if (!syncPage.IsTruncated) break;
            } while (true);

            await _store.ApplyRemotePageAsync(new CardDavRemotePage
            {
                AddressBookId = binding.AddressBook.Id,
                SeenHrefs = seen,
                ReconciliationGeneration = generation,
                IsFullReconciliation = true
            }).ConfigureAwait(false);
            await _store.CompleteFullReconciliationAsync(binding.AddressBook.Id, generation, token).ConfigureAwait(false);
            return result;
        }

        var listing = await _client.EnumerateResourcesAsync(settings, ToProtocolBook(binding), cancellationToken).ConfigureAwait(false);
        var changed = new List<CardDavResourceChange>();
        foreach (var remote in listing.Where(item => !item.IsDeleted))
        {
            var shadow = await _store.GetShadowByHrefAsync(binding.AddressBook.Id, remote.ExactHref).ConfigureAwait(false);
            if (shadow is null || !string.Equals(shadow.ETag, remote.ETag, StringComparison.Ordinal)) changed.Add(remote);
        }

        for (var offset = 0; offset < changed.Count; offset += PageSize)
        {
            var batch = changed.Skip(offset).Take(PageSize).ToList();
            var populated = await PopulateBodiesAsync(settings, binding, batch, cancellationToken).ConfigureAwait(false);
            var page = await BuildRemotePageAsync(accountId, binding, populated, generation, true, null, false, cancellationToken).ConfigureAwait(false);
            await _store.ApplyRemotePageAsync(page).ConfigureAwait(false);
            AddPageCounts(result, page);
        }

        await _store.ApplyRemotePageAsync(new CardDavRemotePage
        {
            AddressBookId = binding.AddressBook.Id,
            SeenHrefs = listing.Where(item => !item.IsDeleted).Select(item => item.ExactHref).ToList(),
            ReconciliationGeneration = generation,
            IsFullReconciliation = true,
            NextSyncToken = binding.State.SyncToken,
            CommitSyncToken = false
        }).ConfigureAwait(false);
        await _store.CompleteFullReconciliationAsync(binding.AddressBook.Id, generation).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<CardDavResourceChange>> PopulateBodiesAsync(
        CardDavConnectionSettings settings,
        CardDavBookBinding binding,
        IReadOnlyList<CardDavResourceChange> changes,
        CancellationToken cancellationToken)
    {
        var complete = changes.Where(change => change.IsDeleted || change.StatusCode >= 400 || !string.IsNullOrEmpty(change.VCard)).ToList();
        var missing = changes.Where(change => !change.IsDeleted && change.StatusCode < 400 && string.IsNullOrEmpty(change.VCard)).ToList();
        if (missing.Count == 0) return complete;

        IReadOnlyList<CardDavResourceChange> fetched = [];
        if (binding.State.SupportsMultiget)
        {
            var values = new List<CardDavResourceChange>();
            var batchSize = Math.Clamp(binding.State.LearnedMultigetBatchSize, 1, PageSize);
            for (var offset = 0; offset < missing.Count; offset += batchSize)
                values.AddRange(await _client.MultiGetAsync(settings, ToProtocolBook(binding), missing.Skip(offset).Take(batchSize).Select(item => item.ExactHref).ToList(), cancellationToken).ConfigureAwait(false));
            fetched = values;
        }
        else if (binding.State.SupportsAddressBookQuery)
        {
            var requested = missing.Select(item => item.ExactHref).ToHashSet(StringComparer.Ordinal);
            fetched = (await _client.QueryAsync(settings, ToProtocolBook(binding), cancellationToken).ConfigureAwait(false))
                .Where(item => requested.Contains(item.ExactHref)).ToList();
        }
        else
        {
            fetched = await Task.WhenAll(missing.Select(async item =>
                await GetResourceBoundedAsync(settings, item.ExactHref, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        }

        complete.AddRange(fetched);
        var returned = fetched.Select(item => item.ExactHref).ToHashSet(StringComparer.Ordinal);
        var unresolved = missing.Where(item => !returned.Contains(item.ExactHref)).ToList();
        if (unresolved.Count > 0)
        {
            complete.AddRange(await Task.WhenAll(unresolved.Select(async item =>
                await GetResourceBoundedAsync(settings, item.ExactHref, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false));
        }
        return complete;
    }

    private async Task<CardDavResourceChange> GetResourceBoundedAsync(
        CardDavConnectionSettings settings,
        string href,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(href);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var gate = OriginGetLocks.GetOrAdd(origin, _ => new SemaphoreSlim(4, 4));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _client.GetResourceAsync(settings, href, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CardDavRemotePage> BuildRemotePageAsync(
        Guid accountId,
        CardDavBookBinding binding,
        IReadOnlyList<CardDavResourceChange> changes,
        long generation,
        bool full,
        string nextToken,
        bool commitToken,
        CancellationToken cancellationToken)
    {
        var upserts = new List<CardDavRemoteUpsert>();
        var deleted = new List<string>();
        var quarantines = new List<CardDavQuarantine>();
        foreach (var change in changes)
        {
            if (change.IsDeleted || change.StatusCode == (int)HttpStatusCode.NotFound)
            {
                deleted.Add(change.ExactHref);
                continue;
            }

            if (change.StatusCode >= 400)
            {
                quarantines.Add(new CardDavQuarantine
                {
                    AddressBookId = binding.AddressBook.Id,
                    ExactHref = change.ExactHref,
                    ETag = change.ETag,
                    ErrorCategory = $"DavStatus:{change.StatusCode}",
                    AttemptCount = 1,
                    NextAttemptUtc = DateTime.UtcNow.AddHours(1)
                });
                continue;
            }

            try
            {
                var document = _codec.Parse(change.VCard);
                var contact = _codec.Project(document);
                var existing = await _store.GetShadowByHrefAsync(binding.AddressBook.Id, change.ExactHref).ConfigureAwait(false);
                contact.Id = existing?.ContactId ?? contact.Id;
                contact.MailAccountId = accountId;
                contact.AddressBookId = binding.AddressBook.Id;
                contact.SourceKind = ContactSourceKind.CardDav;
                contact.RemoteId = change.ExactHref;
                contact.RemoteVersion = change.ETag;
                var hashes = _codec.ComputeHashes(document, contact, change.VCard);
                var payload = await _payloadStore.SaveAsync(change.VCard, cancellationToken).ConfigureAwait(false);
                var uid = document.Properties.FirstOrDefault(property => property.Name == "UID")?.Value;
                upserts.Add(new CardDavRemoteUpsert(contact, new CardDavResourceShadow
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    AddressBookId = binding.AddressBook.Id,
                    ContactId = contact.Id,
                    ExactHref = change.ExactHref,
                    ETag = change.ETag,
                    Uid = uid,
                    VCardVersion = document.Version,
                    PayloadReference = payload,
                    RawHash = hashes.RawHash,
                    SemanticHash = hashes.SemanticHash,
                    DomainHash = hashes.DomainHash,
                    LastSeenGeneration = generation,
                    Status = CardDavResourceStatus.Active
                }));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
            {
                var payload = string.IsNullOrEmpty(change.VCard) ? null : await _payloadStore.SaveAsync(change.VCard, cancellationToken).ConfigureAwait(false);
                quarantines.Add(new CardDavQuarantine
                {
                    AddressBookId = binding.AddressBook.Id,
                    ExactHref = change.ExactHref,
                    ETag = change.ETag,
                    PayloadReference = payload,
                    ErrorCategory = "MalformedVCard",
                    AttemptCount = 1,
                    NextAttemptUtc = DateTime.UtcNow.AddHours(6)
                });
            }
        }

        return new CardDavRemotePage
        {
            AddressBookId = binding.AddressBook.Id,
            Upserts = upserts,
            DeletedHrefs = deleted,
            Quarantines = quarantines,
            ReconciliationGeneration = generation,
            IsFullReconciliation = full,
            NextSyncToken = nextToken,
            CommitSyncToken = commitToken
        };
    }

    private static CardDavAddressBook ToProtocolBook(CardDavBookBinding binding)
        => new()
        {
            ExactHref = binding.State.ExactHref,
            DisplayName = binding.AddressBook.DisplayName,
            SyncToken = binding.State.SyncToken,
            IsReadOnly = binding.State.IsReadOnly,
            SupportsSyncCollection = binding.State.SupportsSyncCollection,
            SupportsMultiget = binding.State.SupportsMultiget,
            SupportsAddressBookQuery = binding.State.SupportsAddressBookQuery,
            SupportsVCard3 = binding.State.SupportsVCard3,
            SupportsVCard4 = binding.State.SupportsVCard4,
            SupportsExtendedMkCol = binding.State.SupportsExtendedMkCol,
            SupportsAddMember = binding.State.SupportsAddMember,
            MaximumResourceSize = binding.State.MaximumResourceSize
        };

    private async Task<CardDavConnectionSettings> CreateSettingsAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var server = account.ServerInformation ?? throw new InvalidOperationException("CardDAV server settings are unavailable.");
        var protectedPassword = await _credentialStore.GetPasswordAsync(account.Id, cancellationToken).ConfigureAwait(false);
        return new CardDavConnectionSettings
        {
            ServiceUri = string.IsNullOrWhiteSpace(server.CardDavServiceUrl) ? null : new Uri(server.CardDavServiceUrl, UriKind.Absolute),
            AccountAddress = account.Address,
            Authentication = new DavAuthenticationProfile
            {
                Kind = DavAuthenticationKind.Basic,
                Username = string.IsNullOrWhiteSpace(server.CalDavUsername) ? account.Address : server.CalDavUsername,
                Password = protectedPassword ?? server.CalDavPassword
            }
        };
    }

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

    private static void AddCounts(ContactSynchronizationResult target, ContactSynchronizationResult source)
    {
        target.DownloadedCount += source.DownloadedCount;
        target.ChangedCount += source.ChangedCount;
        target.DeletedCount += source.DeletedCount;
        target.MergeIssues(source.Issues);
    }

    private static void AddPageCounts(ContactSynchronizationResult result, CardDavRemotePage page)
    {
        result.DownloadedCount += page.Upserts.Count;
        result.ChangedCount += page.Upserts.Count;
        result.DeletedCount += page.DeletedHrefs.Count;
    }
}
