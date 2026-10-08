using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Synchronizers.CardDav;

namespace Wino.Core.Services;

/// <summary>
/// Writes a synchronized list back to its group vCard. The group is always edited as the
/// server has it now, so members Wino cannot resolve and foreign properties survive.
/// </summary>
public sealed class CardDavContactListService : ICardDavContactListService
{
    // A contact created in the editor is written to the server by a queued request while
    // its list membership is already being saved.
    private static readonly TimeSpan PendingContactTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingContactPollInterval = TimeSpan.FromMilliseconds(400);
    private readonly ICardDavClient _client;
    private readonly ICardDavSynchronizationStore _store;
    private readonly IVCardCodec _codec;
    private readonly IDavCredentialStore _credentials;
    private readonly IAccountService _accounts;
    private readonly IContactService _contacts;

    public CardDavContactListService(ICardDavClient client, ICardDavSynchronizationStore store, IVCardCodec codec,
        IDavCredentialStore credentials, IAccountService accounts, IContactService contacts)
    {
        _client = client;
        _store = store;
        _codec = codec;
        _credentials = credentials;
        _accounts = accounts;
        _contacts = contacts;
    }

    public async Task CreateAsync(ContactList list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.AddressBookId is not Guid addressBookId) return;

        var book = (await _contacts.GetAddressBooksAsync().ConfigureAwait(false)).FirstOrDefault(item => item.Id == addressBookId);
        if (book?.SourceKind != ContactSourceKind.CardDav) return;

        var (binding, settings) = await ConnectAsync(addressBookId, cancellationToken).ConfigureAwait(false);
        using var bookLock = await CardDavConnection.LockAddressBookAsync(addressBookId, cancellationToken).ConfigureAwait(false);

        var uid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var document = _codec.CreateGroup(list.Name?.Trim(), binding.State.SupportsVCard4 ? "4.0" : "3.0", uid);
        var written = await _client.PutResourceAsync(
            settings,
            $"{binding.State.ExactHref.TrimEnd('/')}/{uid}.vcf",
            _codec.Serialize(document),
            createOnly: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Recording the group ties the next pull to this list instead of adding a second one.
        await _store.SaveResourceAsync(new CardDavResourceShadow
        {
            AddressBookId = addressBookId,
            ExactHref = written.ExactHref,
            ETag = written.ETag,
            Uid = uid,
            Kind = CardDavResourceKind.Group,
            ListId = list.Id
        }).ConfigureAwait(false);
    }

    public Task RenameAsync(Guid listId, string name, CancellationToken cancellationToken = default)
        => EditGroupAsync(listId, document => _codec.SetGroupName(document, name?.Trim()), cancellationToken);

    public async Task DeleteAsync(Guid listId, CancellationToken cancellationToken = default)
    {
        var group = await FindGroupAsync(listId).ConfigureAwait(false);
        if (group is null) return;

        var (binding, settings) = await ConnectAsync(group.AddressBookId, cancellationToken).ConfigureAwait(false);
        using var bookLock = await CardDavConnection.LockAddressBookAsync(group.AddressBookId, cancellationToken).ConfigureAwait(false);

        await _client.DeleteResourceAsync(settings, group.ExactHref, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _store.DeleteResourceAsync(binding.State.AddressBookId, group.ExactHref).ConfigureAwait(false);
    }

    public async Task UpdateMembersAsync(
        Guid listId,
        IReadOnlyCollection<Guid> addedContactIds,
        IReadOnlyCollection<Guid> removedContactIds,
        CancellationToken cancellationToken = default)
    {
        if ((addedContactIds?.Count ?? 0) == 0 && (removedContactIds?.Count ?? 0) == 0)
            return;

        var group = await FindGroupAsync(listId).ConfigureAwait(false);
        if (group is null) return;

        // Resolved before the address book is locked: a member that is still being
        // created needs that lock to finish.
        var added = new List<string>();
        foreach (var contactId in addedContactIds ?? [])
            added.Add(await RequireMemberUidAsync(group, contactId, cancellationToken).ConfigureAwait(false));

        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contactId in removedContactIds ?? [])
        {
            var resource = await _store.GetResourceByContactAsync(contactId).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resource?.Uid)) removed.Add(resource.Uid);
        }

        await EditGroupAsync(listId, document => _codec.SetGroupMembers(
            document,
            _codec.GetGroupMembers(document).Where(uid => !removed.Contains(uid)).Concat(added)), cancellationToken).ConfigureAwait(false);
    }

    private async Task EditGroupAsync(Guid listId, Action<VCardDocument> edit, CancellationToken cancellationToken)
    {
        var group = await FindGroupAsync(listId).ConfigureAwait(false);
        if (group is null) return;

        var (_, settings) = await ConnectAsync(group.AddressBookId, cancellationToken).ConfigureAwait(false);
        using var bookLock = await CardDavConnection.LockAddressBookAsync(group.AddressBookId, cancellationToken).ConfigureAwait(false);

        var current = await _client.GetResourceAsync(settings, group.ExactHref, cancellationToken).ConfigureAwait(false);
        if (current.IsDeleted)
            throw new DavRequestException(404, Translator.DavError_NotFound);

        var document = _codec.Parse(current.VCard);
        edit(document);
        var written = await _client.PutResourceAsync(settings, group.ExactHref, _codec.Serialize(document), current.ETag,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        group.ETag = written.ETag;
        group.MemberUids = string.Join('\n', _codec.GetGroupMembers(document));
        await _store.SaveResourceAsync(group).ConfigureAwait(false);
    }

    private async Task<CardDavResourceShadow> FindGroupAsync(Guid listId)
    {
        var resource = await _store.GetResourceByListAsync(listId).ConfigureAwait(false);
        return resource?.Kind == CardDavResourceKind.Group ? resource : null;
    }

    private async Task<(CardDavBookBinding Binding, CardDavConnectionSettings Settings)> ConnectAsync(Guid addressBookId, CancellationToken cancellationToken)
    {
        var book = (await _contacts.GetAddressBooksAsync().ConfigureAwait(false)).FirstOrDefault(item => item.Id == addressBookId)
                   ?? throw new InvalidOperationException(Translator.DavError_NotFound);
        var binding = (await _store.GetAddressBooksAsync(book.MailAccountId, book.Id).ConfigureAwait(false)).SingleOrDefault()
                      ?? throw new InvalidOperationException(Translator.DavError_NotFound);
        if (binding.State.IsReadOnly)
            throw new InvalidOperationException(Translator.DavError_ReadOnly);

        var account = await _accounts.GetAccountAsync(book.MailAccountId).ConfigureAwait(false)
                      ?? throw new InvalidOperationException(Translator.DavError_NotFound);
        var settings = await CardDavConnection.CreateSettingsAsync(account, _credentials, _accounts, cancellationToken).ConfigureAwait(false);
        return (binding, settings);
    }

    /// <summary>
    /// A group can only name contacts of its own address book. A contact that is still
    /// being created there is waited for; any other contact is refused.
    /// </summary>
    private async Task<string> RequireMemberUidAsync(CardDavResourceShadow group, Guid contactId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(PendingContactTimeout);
        while (true)
        {
            var resource = await _store.GetResourceByContactAsync(contactId).ConfigureAwait(false);
            if (resource is not null)
            {
                return resource.AddressBookId == group.AddressBookId && !string.IsNullOrWhiteSpace(resource.Uid)
                    ? resource.Uid
                    : throw new InvalidOperationException(Translator.ContactList_SynchronizedListForeignContact);
            }

            var contact = await _contacts.GetContactAsync(contactId).ConfigureAwait(false);
            if ((contact is not null && contact.AddressBookId != group.AddressBookId) || DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(Translator.ContactList_SynchronizedListForeignContact);

            await Task.Delay(PendingContactPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
