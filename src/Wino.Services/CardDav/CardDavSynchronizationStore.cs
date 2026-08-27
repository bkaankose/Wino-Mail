using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Services.CardDav;

public sealed class CardDavSynchronizationStore : BaseDatabaseService, ICardDavSynchronizationStore
{
    private readonly ICardDavPayloadStore _payloadStore;
    private readonly IVCardCodec _codec;

    public CardDavSynchronizationStore(
        IDatabaseService databaseService,
        ICardDavPayloadStore payloadStore,
        IVCardCodec codec) : base(databaseService)
    {
        _payloadStore = payloadStore;
        _codec = codec;
    }

    public Task<CardDavAccountState> GetAccountStateAsync(Guid accountId)
        => Connection.FindAsync<CardDavAccountState>(accountId);

    public async Task SaveDiscoveryAsync(Guid accountId, CardDavDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.InsertOrReplace(new CardDavAccountState
            {
                AccountId = accountId,
                ContextHref = discovery.ContextUri?.ToString(),
                PrincipalHref = discovery.PrincipalUri?.ToString(),
                AddressBookHomeHref = discovery.AddressBookHomeUri?.ToString(),
                SupportsAddressBookCreation = discovery.SupportsAddressBookCreation,
                DiscoveryExpiresUtc = DateTime.UtcNow.AddDays(7),
                CapabilitiesExpireUtc = DateTime.UtcNow.AddDays(1),
                RequiresRediscovery = false
            }, typeof(CardDavAccountState));

            foreach (var remote in discovery.AddressBooks)
            {
                var book = transaction.Query<ContactAddressBook>(
                    "SELECT * FROM ContactAddressBook WHERE MailAccountId = ? AND SourceKind = ? AND RemoteId = ? LIMIT 1",
                    accountId, (int)ContactSourceKind.CardDav, remote.ExactHref).FirstOrDefault();
                if (book is null)
                {
                    book = new ContactAddressBook
                    {
                        Id = Guid.NewGuid(),
                        MailAccountId = accountId,
                        SourceKind = ContactSourceKind.CardDav,
                        RemoteId = remote.ExactHref,
                        ParentRemoteId = discovery.AddressBookHomeUri?.ToString(),
                        DisplayName = string.IsNullOrWhiteSpace(remote.DisplayName) ? "Address book" : remote.DisplayName,
                        IsDefault = !transaction.Table<ContactAddressBook>().Any(item => item.MailAccountId == accountId && item.SourceKind == ContactSourceKind.CardDav),
                        IsReadOnly = remote.IsReadOnly
                    };
                    transaction.Insert(book, typeof(ContactAddressBook));
                }
                else
                {
                    book.DisplayName = string.IsNullOrWhiteSpace(remote.DisplayName) ? book.DisplayName : remote.DisplayName;
                    book.ParentRemoteId = discovery.AddressBookHomeUri?.ToString();
                    book.IsReadOnly = remote.IsReadOnly;
                    transaction.Update(book, typeof(ContactAddressBook));
                }

                var currentState = transaction.Find<CardDavAddressBookState>(book.Id);
                transaction.InsertOrReplace(new CardDavAddressBookState
                {
                    AddressBookId = book.Id,
                    AccountId = accountId,
                    ExactHref = remote.ExactHref,
                    SyncToken = currentState?.SyncToken ?? remote.SyncToken,
                    CollectionTag = remote.CollectionTag,
                    SupportsSyncCollection = remote.SupportsSyncCollection,
                    SupportsMultiget = remote.SupportsMultiget,
                    SupportsAddressBookQuery = remote.SupportsAddressBookQuery,
                    SupportsInlineAddressData = currentState?.SupportsInlineAddressData ?? true,
                    SupportsVCard3 = remote.SupportsVCard3,
                    SupportsVCard4 = remote.SupportsVCard4,
                    SupportsExtendedMkCol = remote.SupportsExtendedMkCol,
                    SupportsAddMember = remote.SupportsAddMember,
                    SupportsPreferMinimal = currentState?.SupportsPreferMinimal ?? false,
                    MaximumResourceSize = remote.MaximumResourceSize,
                    IsReadOnly = remote.IsReadOnly,
                    LearnedMultigetBatchSize = currentState?.LearnedMultigetBatchSize ?? 100,
                    Quirks = currentState?.Quirks,
                    ReconciliationGeneration = currentState?.ReconciliationGeneration ?? 0,
                    RequiresFullReconciliation = currentState?.RequiresFullReconciliation ?? string.IsNullOrWhiteSpace(remote.SyncToken),
                    IsUnavailable = false,
                    LastFullSyncUtc = currentState?.LastFullSyncUtc,
                    LastIncrementalSyncUtc = currentState?.LastIncrementalSyncUtc
                }, typeof(CardDavAddressBookState));
            }
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CardDavBookBinding>> GetAddressBooksAsync(Guid accountId, Guid? addressBookId = null)
    {
        var books = await Connection.Table<ContactAddressBook>()
            .Where(book => book.MailAccountId == accountId && book.SourceKind == ContactSourceKind.CardDav)
            .ToListAsync().ConfigureAwait(false);
        if (addressBookId.HasValue) books = books.Where(book => book.Id == addressBookId.Value).ToList();
        var states = await Connection.Table<CardDavAddressBookState>().Where(state => state.AccountId == accountId).ToListAsync().ConfigureAwait(false);
        var byId = states.ToDictionary(state => state.AddressBookId);
        return books.Where(book => byId.ContainsKey(book.Id)).Select(book => new CardDavBookBinding(book, byId[book.Id])).ToList();
    }

    public Task<CardDavResourceShadow> GetShadowByHrefAsync(Guid addressBookId, string exactHref)
        => Connection.Table<CardDavResourceShadow>()
            .FirstOrDefaultAsync(shadow => shadow.AddressBookId == addressBookId && shadow.ExactHref == exactHref);

    public Task<CardDavResourceShadow> GetShadowByContactAsync(Guid contactId)
        => Connection.Table<CardDavResourceShadow>().FirstOrDefaultAsync(shadow => shadow.ContactId == contactId);

    public async Task<long> BeginFullReconciliationAsync(Guid addressBookId)
    {
        var state = await Connection.FindAsync<CardDavAddressBookState>(addressBookId).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("CardDAV address-book state was not found.");
        state.ReconciliationGeneration++;
        state.RequiresFullReconciliation = true;
        await Connection.UpdateAsync(state, typeof(CardDavAddressBookState)).ConfigureAwait(false);
        return state.ReconciliationGeneration;
    }

    public Task ApplyRemotePageAsync(CardDavRemotePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var href in page.SeenHrefs.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                transaction.Execute(
                    "UPDATE CardDavResourceShadow SET LastSeenGeneration = ? WHERE AddressBookId = ? AND ExactHref = ?",
                    page.ReconciliationGeneration, page.AddressBookId, href);
            }

            foreach (var href in page.DeletedHrefs.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
                ApplyRemoteDeletion(transaction, page.AddressBookId, href);

            foreach (var upsert in page.Upserts)
                ApplyRemoteUpsert(transaction, page.AddressBookId, upsert);

            foreach (var quarantine in page.Quarantines)
            {
                var current = transaction.Query<CardDavQuarantine>(
                    "SELECT * FROM CardDavQuarantine WHERE AddressBookId = ? AND ExactHref = ? LIMIT 1",
                    page.AddressBookId, quarantine.ExactHref).FirstOrDefault();
                quarantine.Id = current?.Id ?? quarantine.Id;
                transaction.InsertOrReplace(quarantine, typeof(CardDavQuarantine));
            }

            if (page.CommitSyncToken)
            {
                var state = transaction.Find<CardDavAddressBookState>(page.AddressBookId)
                            ?? throw new InvalidOperationException("CardDAV address-book state was not found.");
                state.SyncToken = page.NextSyncToken;
                state.RequiresFullReconciliation = page.IsFullReconciliation;
                if (page.IsFullReconciliation) state.LastFullSyncUtc = null;
                else state.LastIncrementalSyncUtc = DateTime.UtcNow;
                transaction.Update(state, typeof(CardDavAddressBookState));
            }
        });
    }

    public Task CompleteFullReconciliationAsync(Guid addressBookId, long generation, string syncToken = null)
        => Connection.RunInTransactionAsync(transaction =>
        {
            var stale = transaction.Query<CardDavResourceShadow>(
                "SELECT * FROM CardDavResourceShadow WHERE AddressBookId = ? AND LastSeenGeneration <> ? AND Status = ?",
                addressBookId, generation, (int)CardDavResourceStatus.Active);
            foreach (var shadow in stale)
                ApplyRemoteDeletion(transaction, addressBookId, shadow.ExactHref);
            var state = transaction.Find<CardDavAddressBookState>(addressBookId);
            if (state is not null)
            {
                state.RequiresFullReconciliation = false;
                state.LastFullSyncUtc = DateTime.UtcNow;
                if (syncToken is not null) state.SyncToken = syncToken;
                transaction.Update(state, typeof(CardDavAddressBookState));
            }
        });

    public async Task<CardDavOutboxItem> StageMutationAsync(ContactOperationPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Contact);
        CardDavOutboxItem result = null;
        await Connection.RunInTransactionAsync(transaction =>
        {
            var contact = request.Contact;
            contact.Id = contact.Id == Guid.Empty ? Guid.NewGuid() : contact.Id;
            contact.SourceKind = ContactSourceKind.CardDav;
            contact.ModifiedAtUtc = DateTime.UtcNow;
            if (contact.CreatedAtUtc == default) contact.CreatedAtUtc = contact.ModifiedAtUtc;
            contact.IsAutoCollected = false;
            contact.PendingMutation = request.Operation switch
            {
                ContactSynchronizerOperation.Create => ContactPendingMutation.Create,
                ContactSynchronizerOperation.Delete => ContactPendingMutation.Delete,
                ContactSynchronizerOperation.SetPhoto => ContactPendingMutation.SetPhoto,
                ContactSynchronizerOperation.DeletePhoto => ContactPendingMutation.DeletePhoto,
                _ => ContactPendingMutation.Update
            };

            var existingContact = transaction.Find<AccountContact>(contact.Id);
            if (existingContact is null) InsertContact(transaction, contact);
            else
            {
                DeleteChildRows(transaction, contact.Id);
                NormalizeChildren(contact);
                transaction.Update(contact, typeof(AccountContact));
                InsertChildren(transaction, contact);
            }

            var current = transaction.Query<CardDavOutboxItem>(
                "SELECT * FROM CardDavOutboxItem WHERE ContactId = ? AND State IN (?, ?, ?) LIMIT 1",
                contact.Id, (int)CardDavOutboxState.Pending, (int)CardDavOutboxState.Leased, (int)CardDavOutboxState.BlockedByConflict).FirstOrDefault();
            var shadow = transaction.Query<CardDavResourceShadow>(
                "SELECT * FROM CardDavResourceShadow WHERE ContactId = ? LIMIT 1", contact.Id).FirstOrDefault();
            if (current?.Operation == CardDavOutboxOperation.CreateContact && request.Operation == ContactSynchronizerOperation.Delete)
            {
                DeleteContact(transaction, contact.Id);
                transaction.Delete<CardDavOutboxItem>(current.Id);
                result = null;
                return;
            }
            result = Coalesce(current, request, shadow);
            transaction.InsertOrReplace(result, typeof(CardDavOutboxItem));
        }).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<CardDavOutboxItem>> LeaseDueOutboxAsync(
        Guid accountId,
        Guid? addressBookId,
        int maximumCount,
        TimeSpan leaseDuration)
    {
        var now = DateTime.UtcNow;

        var candidates = await Connection.Table<CardDavOutboxItem>()
            .Where(item => item.AccountId == accountId &&
                           (item.State == CardDavOutboxState.Pending ||
                            (item.State == CardDavOutboxState.Leased && item.LeaseExpiresUtc < now)))
            .ToListAsync().ConfigureAwait(false);

        candidates = candidates.Where(item => !addressBookId.HasValue || item.AddressBookId == addressBookId)
            .Where(item => !item.NextAttemptUtc.HasValue || item.NextAttemptUtc <= now)
            .OrderBy(item => item.CreatedAtUtc)
            .Take(Math.Max(1, maximumCount))
            .ToList();

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var item in candidates)
            {
                item.State = CardDavOutboxState.Leased;
                item.LeaseExpiresUtc = now.Add(leaseDuration);
                item.ModifiedAtUtc = now;
                transaction.Update(item, typeof(CardDavOutboxItem));
            }
        }).ConfigureAwait(false);

        return candidates;
    }

    public Task UpdateOutboxTargetAsync(Guid outboxItemId, string intendedHref)
        => Connection.ExecuteAsync(
            "UPDATE CardDavOutboxItem SET IntendedHref = ?, ModifiedAtUtc = ? WHERE Id = ?",
            intendedHref, DateTime.UtcNow, outboxItemId);

    public Task CompleteOutboxAsync(Guid outboxItemId, AccountContact serverContact, CardDavResourceShadow shadow, bool deleted)
        => Connection.RunInTransactionAsync(transaction =>
        {
            var outbox = transaction.Find<CardDavOutboxItem>(outboxItemId);
            if (outbox is null) return;
            if (outbox.ContactId is Guid contactId)
            {
                var current = transaction.Find<AccountContact>(contactId);
                if (deleted)
                {
                    if (current is not null) DeleteContact(transaction, current.Id);
                    if (shadow is not null) transaction.Delete<CardDavResourceShadow>(shadow.Id);
                }
                else if (serverContact is not null)
                {
                    serverContact.Id = contactId;
                    serverContact.MailAccountId = current?.MailAccountId ?? outbox.AccountId;
                    serverContact.AddressBookId = current?.AddressBookId ?? outbox.AddressBookId.GetValueOrDefault();
                    serverContact.SourceKind = ContactSourceKind.CardDav;
                    serverContact.IsFavorite = current?.IsFavorite ?? false;
                    serverContact.PendingMutation = ContactPendingMutation.None;
                    if (current is null) InsertContact(transaction, serverContact);
                    else
                    {
                        DeleteChildRows(transaction, contactId);
                        NormalizeChildren(serverContact);
                        transaction.Update(serverContact, typeof(AccountContact));
                        InsertChildren(transaction, serverContact);
                    }
                    if (shadow is not null)
                    {
                        shadow.ContactId = contactId;
                        if (outbox.Operation == CardDavOutboxOperation.MoveContact)
                            transaction.Execute("DELETE FROM CardDavResourceShadow WHERE ContactId = ? AND Id <> ?", contactId, shadow.Id);
                        transaction.InsertOrReplace(shadow, typeof(CardDavResourceShadow));
                    }
                }
            }
            transaction.Delete<CardDavOutboxItem>(outboxItemId);
        });

    public Task RescheduleOutboxAsync(Guid outboxItemId, string errorCode, DateTime nextAttemptUtc)
        => Connection.ExecuteAsync(
            "UPDATE CardDavOutboxItem SET State = ?, LeaseExpiresUtc = NULL, AttemptCount = AttemptCount + 1, NextAttemptUtc = ?, LastErrorCode = ?, ModifiedAtUtc = ? WHERE Id = ?",
            (int)CardDavOutboxState.Pending, nextAttemptUtc, errorCode, DateTime.UtcNow, outboxItemId);

    public Task BlockOutboxWithConflictAsync(CardDavOutboxItem outboxItem, CardDavConflict conflict)
        => Connection.RunInTransactionAsync(transaction =>
        {
            outboxItem.State = CardDavOutboxState.BlockedByConflict;
            outboxItem.LeaseExpiresUtc = null;
            outboxItem.ModifiedAtUtc = DateTime.UtcNow;
            conflict.OutboxItemId = outboxItem.Id;
            transaction.Update(outboxItem, typeof(CardDavOutboxItem));
            transaction.Insert(conflict, typeof(CardDavConflict));
        });

    public async Task<IReadOnlyList<CardDavConflict>> GetUnresolvedConflictsAsync(Guid? accountId = null)
    {
        var conflicts = await Connection.Table<CardDavConflict>()
            .Where(conflict => conflict.Resolution == CardDavConflictResolution.Unresolved)
            .ToListAsync().ConfigureAwait(false);
        return accountId.HasValue ? conflicts.Where(conflict => conflict.AccountId == accountId.Value).ToList() : conflicts;
    }

    public async Task<CardDavConflictDetails> GetConflictDetailsAsync(Guid conflictId)
    {
        var conflict = await Connection.FindAsync<CardDavConflict>(conflictId).ConfigureAwait(false);
        if (conflict is null) return null;
        var local = await LoadContactAsync(conflict.ContactId).ConfigureAwait(false);
        AccountContact remote = null;
        if (!string.IsNullOrWhiteSpace(conflict.RemotePayloadReference))
            remote = _codec.Project(_codec.Parse(await _payloadStore.ReadAsync(conflict.RemotePayloadReference).ConfigureAwait(false)));
        var differences = new List<CardDavFieldDifference>();
        AddDifference(differences, "DisplayName", local?.DisplayName, remote?.DisplayName);
        AddDifference(differences, "GivenName", local?.GivenName, remote?.GivenName);
        AddDifference(differences, "Surname", local?.Surname, remote?.Surname);
        AddDifference(differences, "Company", local?.CompanyName, remote?.CompanyName);
        AddDifference(differences, "JobTitle", local?.JobTitle, remote?.JobTitle);
        AddDifference(differences, "Email", JoinEmails(local), JoinEmails(remote));
        AddDifference(differences, "Phone", JoinPhones(local), JoinPhones(remote));
        AddDifference(differences, "Website", local?.Website, remote?.Website);
        AddDifference(differences, "Notes", local?.Notes, remote?.Notes);
        return new CardDavConflictDetails(conflict, local?.DisplayValue ?? remote?.DisplayValue ?? string.Empty, differences);
    }

    public async Task ResolveConflictAsync(Guid conflictId, CardDavConflictResolution resolution)
    {
        var conflict = await Connection.FindAsync<CardDavConflict>(conflictId).ConfigureAwait(false);
        if (conflict is null || resolution == CardDavConflictResolution.Unresolved) return;

        VCardDocument remoteDocument = null;
        AccountContact remoteContact = null;
        if (!string.IsNullOrWhiteSpace(conflict.RemotePayloadReference))
        {
            var raw = await _payloadStore.ReadAsync(conflict.RemotePayloadReference).ConfigureAwait(false);
            remoteDocument = _codec.Parse(raw);
            remoteContact = _codec.Project(remoteDocument);
        }

        var localContact = await LoadContactAsync(conflict.ContactId).ConfigureAwait(false);
        await Connection.RunInTransactionAsync(transaction =>
        {
            conflict.Resolution = resolution;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            transaction.Update(conflict, typeof(CardDavConflict));
            var outbox = conflict.OutboxItemId is Guid outboxId ? transaction.Find<CardDavOutboxItem>(outboxId) : null;

            if (resolution is CardDavConflictResolution.UseServer or CardDavConflictResolution.KeepBoth)
            {
                if (remoteContact is null)
                {
                    if (localContact is not null) DeleteContact(transaction, localContact.Id);
                    if (conflict.ResourceShadowId is Guid shadowId) transaction.Delete<CardDavResourceShadow>(shadowId);
                }
                else
                {
                    ApplyConflictRemote(transaction, conflict, localContact, remoteContact, remoteDocument);
                }

                if (outbox is not null) transaction.Delete<CardDavOutboxItem>(outbox.Id);
            }

            if (resolution == CardDavConflictResolution.KeepBoth && localContact is not null)
            {
                var clone = _codec.Project(_codec.Create(localContact, remoteDocument?.Version ?? "3.0"));
                clone.Id = Guid.NewGuid();
                clone.MailAccountId = conflict.AccountId;
                clone.AddressBookId = conflict.AddressBookId;
                clone.SourceKind = ContactSourceKind.CardDav;
                clone.PendingMutation = ContactPendingMutation.Create;
                clone.CreatedAtUtc = clone.ModifiedAtUtc = DateTime.UtcNow;
                InsertContact(transaction, clone);
                transaction.Insert(new CardDavOutboxItem
                {
                    AccountId = conflict.AccountId,
                    ContactId = clone.Id,
                    AddressBookId = conflict.AddressBookId,
                    Operation = CardDavOutboxOperation.CreateContact,
                    State = CardDavOutboxState.Pending,
                    NextAttemptUtc = DateTime.UtcNow
                }, typeof(CardDavOutboxItem));
            }
            else if (resolution == CardDavConflictResolution.UseLocal && outbox is not null)
            {
                outbox.State = CardDavOutboxState.Pending;
                outbox.LeaseExpiresUtc = null;
                outbox.NextAttemptUtc = DateTime.UtcNow;
                outbox.BaseETag = conflict.RemoteETag;
                outbox.BasePayloadReference = conflict.RemotePayloadReference;
                outbox.LastErrorCode = "ConflictResolution:UseLocal";
                transaction.Update(outbox, typeof(CardDavOutboxItem));
                if (localContact is not null)
                {
                    localContact.PendingMutation = ContactPendingMutation.Update;
                    transaction.Update(localContact, typeof(AccountContact));
                }
            }
        }).ConfigureAwait(false);
    }

    public Task DeleteAccountStateAsync(Guid accountId)
        => Connection.RunInTransactionAsync(transaction =>
        {
            var bookIds = transaction.Query<ContactAddressBook>(
                "SELECT * FROM ContactAddressBook WHERE MailAccountId = ? AND SourceKind = ?",
                accountId, (int)ContactSourceKind.CardDav).Select(book => book.Id).ToList();
            foreach (var bookId in bookIds)
            {
                transaction.Execute("DELETE FROM CardDavConflict WHERE AddressBookId = ?", bookId);
                transaction.Execute("DELETE FROM CardDavQuarantine WHERE AddressBookId = ?", bookId);
                transaction.Execute("DELETE FROM CardDavResourceShadow WHERE AddressBookId = ?", bookId);
                transaction.Delete<CardDavAddressBookState>(bookId);
            }
            transaction.Execute("DELETE FROM CardDavOutboxItem WHERE AccountId = ?", accountId);
            transaction.Delete<CardDavAccountState>(accountId);
        });

    public Task DeleteAddressBookStateAsync(Guid addressBookId)
        => Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM CardDavConflict WHERE AddressBookId = ?", addressBookId);
            transaction.Execute("DELETE FROM CardDavQuarantine WHERE AddressBookId = ?", addressBookId);
            transaction.Execute("DELETE FROM CardDavResourceShadow WHERE AddressBookId = ?", addressBookId);
            transaction.Execute("DELETE FROM CardDavOutboxItem WHERE AddressBookId = ? OR DestinationAddressBookId = ?", addressBookId, addressBookId);
            transaction.Delete<CardDavAddressBookState>(addressBookId);
        });

    private async Task<AccountContact> LoadContactAsync(Guid contactId)
    {
        var contact = await Connection.FindAsync<AccountContact>(contactId).ConfigureAwait(false);
        if (contact is null) return null;
        contact.EmailAddresses = await Connection.Table<ContactEmailAddress>().Where(item => item.ContactId == contactId).ToListAsync().ConfigureAwait(false);
        contact.PhoneNumbers = await Connection.Table<ContactPhoneNumber>().Where(item => item.ContactId == contactId).ToListAsync().ConfigureAwait(false);
        contact.PostalAddresses = await Connection.Table<ContactPostalAddress>().Where(item => item.ContactId == contactId).ToListAsync().ConfigureAwait(false);
        contact.ImAddresses = await Connection.Table<ContactImAddress>().Where(item => item.ContactId == contactId).ToListAsync().ConfigureAwait(false);
        contact.Relations = await Connection.Table<ContactRelation>().Where(item => item.ContactId == contactId).ToListAsync().ConfigureAwait(false);
        return contact;
    }

    private static void AddDifference(List<CardDavFieldDifference> differences, string fieldKey, string local, string remote)
    {
        if (!string.Equals(local ?? string.Empty, remote ?? string.Empty, StringComparison.Ordinal))
            differences.Add(new CardDavFieldDifference(fieldKey, local ?? string.Empty, remote ?? string.Empty));
    }

    private static string JoinEmails(AccountContact contact)
        => string.Join(", ", contact?.EmailAddresses?.OrderBy(item => item.Order).Select(item => item.Address) ?? []);

    private static string JoinPhones(AccountContact contact)
        => string.Join(", ", contact?.PhoneNumbers?.OrderBy(item => item.Order).Select(item => item.Number) ?? []);

    private void ApplyConflictRemote(
        SQLiteConnection transaction,
        CardDavConflict conflict,
        AccountContact current,
        AccountContact remote,
        VCardDocument remoteDocument)
    {
        remote.Id = conflict.ContactId;
        remote.MailAccountId = conflict.AccountId;
        remote.AddressBookId = conflict.AddressBookId;
        remote.SourceKind = ContactSourceKind.CardDav;
        remote.PendingMutation = ContactPendingMutation.None;
        remote.IsFavorite = current?.IsFavorite ?? false;
        remote.CreatedAtUtc = current?.CreatedAtUtc ?? DateTime.UtcNow;
        remote.ModifiedAtUtc = DateTime.UtcNow;
        var shadow = conflict.ResourceShadowId is Guid shadowId
            ? transaction.Find<CardDavResourceShadow>(shadowId)
            : null;
        remote.RemoteId = shadow?.ExactHref;
        remote.RemoteVersion = conflict.RemoteETag;
        if (current is null) InsertContact(transaction, remote);
        else
        {
            DeleteChildRows(transaction, current.Id);
            NormalizeChildren(remote);
            transaction.Update(remote, typeof(AccountContact));
            InsertChildren(transaction, remote);
        }

        if (shadow is not null)
        {
            var hashes = _codec.ComputeHashes(remoteDocument, remote);
            shadow.ContactId = remote.Id;
            shadow.ETag = conflict.RemoteETag;
            shadow.PayloadReference = conflict.RemotePayloadReference;
            shadow.VCardVersion = remoteDocument.Version;
            shadow.Uid = remoteDocument.Properties.FirstOrDefault(property => property.Name == "UID")?.Value;
            shadow.RawHash = hashes.RawHash;
            shadow.SemanticHash = hashes.SemanticHash;
            shadow.DomainHash = hashes.DomainHash;
            shadow.Status = CardDavResourceStatus.Active;
            transaction.Update(shadow, typeof(CardDavResourceShadow));
        }
    }

    private static void ApplyRemoteUpsert(SQLiteConnection transaction, Guid addressBookId, CardDavRemoteUpsert upsert)
    {
        var incoming = upsert.Contact;
        var shadow = upsert.Shadow;
        var currentShadow = transaction.Query<CardDavResourceShadow>(
            "SELECT * FROM CardDavResourceShadow WHERE AddressBookId = ? AND ExactHref = ? LIMIT 1",
            addressBookId, shadow.ExactHref).FirstOrDefault();
        var current = currentShadow?.ContactId is Guid contactId ? transaction.Find<AccountContact>(contactId) : null;
        if (current?.PendingMutation != ContactPendingMutation.None)
        {
            shadow.Id = currentShadow?.Id ?? shadow.Id;
            shadow.ContactId = current.Id;
            transaction.InsertOrReplace(shadow, typeof(CardDavResourceShadow));
            return;
        }

        incoming.Id = current?.Id ?? (incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id);
        incoming.AddressBookId = addressBookId;
        incoming.MailAccountId = current?.MailAccountId ?? incoming.MailAccountId;
        incoming.SourceKind = ContactSourceKind.CardDav;
        incoming.RemoteId = shadow.ExactHref;
        incoming.RemoteVersion = shadow.ETag;
        incoming.PendingMutation = ContactPendingMutation.None;
        incoming.IsFavorite = current?.IsFavorite ?? incoming.IsFavorite;
        incoming.IsAutoCollected = false;
        incoming.ModifiedAtUtc = DateTime.UtcNow;
        if (incoming.CreatedAtUtc == default) incoming.CreatedAtUtc = current?.CreatedAtUtc ?? incoming.ModifiedAtUtc;
        if (current is null) InsertContact(transaction, incoming);
        else
        {
            DeleteChildRows(transaction, current.Id);
            NormalizeChildren(incoming);
            transaction.Update(incoming, typeof(AccountContact));
            InsertChildren(transaction, incoming);
        }
        shadow.Id = currentShadow?.Id ?? shadow.Id;
        shadow.ContactId = incoming.Id;
        shadow.Status = CardDavResourceStatus.Active;
        transaction.InsertOrReplace(shadow, typeof(CardDavResourceShadow));
        transaction.Execute("DELETE FROM CardDavQuarantine WHERE AddressBookId = ? AND ExactHref = ?", addressBookId, shadow.ExactHref);
    }

    private static void ApplyRemoteDeletion(SQLiteConnection transaction, Guid addressBookId, string href)
    {
        var shadow = transaction.Query<CardDavResourceShadow>(
            "SELECT * FROM CardDavResourceShadow WHERE AddressBookId = ? AND ExactHref = ? LIMIT 1",
            addressBookId, href).FirstOrDefault();
        if (shadow is null) return;
        var contact = shadow.ContactId is Guid contactId ? transaction.Find<AccountContact>(contactId) : null;
        if (contact?.PendingMutation != ContactPendingMutation.None)
        {
            shadow.Status = CardDavResourceStatus.Deleted;
            transaction.Update(shadow, typeof(CardDavResourceShadow));
            return;
        }
        if (contact is not null) DeleteContact(transaction, contact.Id);
        transaction.Delete<CardDavResourceShadow>(shadow.Id);
    }

    private static CardDavOutboxItem Coalesce(CardDavOutboxItem current, ContactOperationPreparationRequest request, CardDavResourceShadow shadow)
    {
        var operation = request.Operation switch
        {
            ContactSynchronizerOperation.Create => CardDavOutboxOperation.CreateContact,
            ContactSynchronizerOperation.Delete => CardDavOutboxOperation.DeleteContact,
            _ => CardDavOutboxOperation.UpdateContact
        };
        if (shadow is not null && shadow.AddressBookId != request.Contact.AddressBookId &&
            request.Operation != ContactSynchronizerOperation.Delete)
            operation = CardDavOutboxOperation.MoveContact;
        if (current is not null)
        {
            if (current.Operation == CardDavOutboxOperation.CreateContact && operation == CardDavOutboxOperation.UpdateContact)
            {
                operation = CardDavOutboxOperation.CreateContact;
                current.AddressBookId = request.Contact.AddressBookId;
            }
            current.Operation = operation;
            if (operation == CardDavOutboxOperation.MoveContact)
            {
                current.AddressBookId = shadow.AddressBookId;
                current.DestinationAddressBookId = request.Contact.AddressBookId;
                current.IntendedHref = null;
            }
            current.State = CardDavOutboxState.Pending;
            current.LeaseExpiresUtc = null;
            current.NextAttemptUtc = DateTime.UtcNow;
            current.ModifiedAtUtc = DateTime.UtcNow;
            return current;
        }
        return new CardDavOutboxItem
        {
            AccountId = request.Contact.MailAccountId,
            ContactId = request.Contact.Id,
            AddressBookId = operation == CardDavOutboxOperation.MoveContact ? shadow.AddressBookId : request.Contact.AddressBookId,
            DestinationAddressBookId = operation == CardDavOutboxOperation.MoveContact ? request.Contact.AddressBookId : null,
            Operation = operation,
            State = CardDavOutboxState.Pending,
            IntendedHref = shadow?.ExactHref,
            BaseETag = shadow?.ETag,
            BasePayloadReference = shadow?.PayloadReference,
            NextAttemptUtc = DateTime.UtcNow
        };
    }

    private static void InsertContact(SQLiteConnection transaction, AccountContact contact)
    {
        NormalizeChildren(contact);
        transaction.Insert(contact, typeof(AccountContact));
        InsertChildren(transaction, contact);
    }

    private static void DeleteContact(SQLiteConnection transaction, Guid contactId)
    {
        DeleteChildRows(transaction, contactId);
        transaction.Execute("DELETE FROM ContactListMember WHERE ContactId = ?", contactId);
        transaction.Delete<AccountContact>(contactId);
    }

    private static void DeleteChildRows(SQLiteConnection transaction, Guid contactId)
    {
        transaction.Execute("DELETE FROM ContactEmailAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactPhoneNumber WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactPostalAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactImAddress WHERE ContactId = ?", contactId);
        transaction.Execute("DELETE FROM ContactRelation WHERE ContactId = ?", contactId);
    }

    private static void InsertChildren(SQLiteConnection transaction, AccountContact contact)
    {
        NormalizeChildren(contact);
        transaction.InsertAll(contact.EmailAddresses, typeof(ContactEmailAddress));
        transaction.InsertAll(contact.PhoneNumbers, typeof(ContactPhoneNumber));
        transaction.InsertAll(contact.PostalAddresses, typeof(ContactPostalAddress));
        transaction.InsertAll(contact.ImAddresses, typeof(ContactImAddress));
        transaction.InsertAll(contact.Relations, typeof(ContactRelation));
    }

    private static void NormalizeChildren(AccountContact contact)
    {
        contact.EmailAddresses ??= [];
        contact.PhoneNumbers ??= [];
        contact.PostalAddresses ??= [];
        contact.ImAddresses ??= [];
        contact.Relations ??= [];
        contact.EmailAddresses = contact.EmailAddresses.Where(item => !string.IsNullOrWhiteSpace(item.Address))
            .GroupBy(item => ContactEmailAddress.Normalize(item.Address), StringComparer.Ordinal).Select(group => group.First()).ToList();
        for (var index = 0; index < contact.EmailAddresses.Count; index++)
        {
            var item = contact.EmailAddresses[index];
            item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            item.ContactId = contact.Id;
            item.Address = item.Address.Trim();
            item.NormalizedAddress = ContactEmailAddress.Normalize(item.Address);
            item.Order = index;
        }
        foreach (var item in contact.PhoneNumbers) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.PostalAddresses) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.ImAddresses) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        foreach (var item in contact.Relations) { item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id; item.ContactId = contact.Id; }
        contact.SortKey = contact.DisplayValue?.ToLowerInvariant() ?? string.Empty;
    }
}
