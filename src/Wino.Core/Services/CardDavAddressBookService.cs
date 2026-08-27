using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Services;

public sealed class CardDavAddressBookService : ICardDavAddressBookService
{
    private readonly ICardDavClient _client;
    private readonly ICardDavSynchronizationStore _store;
    private readonly IDavCredentialStore _credentials;
    private readonly IAccountService _accounts;
    private readonly IContactService _contacts;

    public CardDavAddressBookService(ICardDavClient client, ICardDavSynchronizationStore store,
        IDavCredentialStore credentials, IAccountService accounts, IContactService contacts)
    {
        _client = client;
        _store = store;
        _credentials = credentials;
        _accounts = accounts;
        _contacts = contacts;
    }

    public async Task<ContactAddressBook> CreateAsync(Guid accountId, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var account = await RequireAccountAsync(accountId).ConfigureAwait(false);
        var settings = await SettingsAsync(account, cancellationToken).ConfigureAwait(false);
        var state = await _store.GetAccountStateAsync(accountId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(state?.AddressBookHomeHref))
            await RediscoverAsync(account, settings, cancellationToken).ConfigureAwait(false);
        state = await _store.GetAccountStateAsync(accountId).ConfigureAwait(false);
        if (state?.SupportsAddressBookCreation != true)
            throw new InvalidOperationException(Translator.ContactsPage_AddressBookCreationUnsupported);

        var created = await _client.CreateAddressBookAsync(settings, state.AddressBookHomeHref, Guid.NewGuid().ToString("N"), displayName.Trim(), cancellationToken).ConfigureAwait(false);
        await RediscoverAsync(account, settings, cancellationToken).ConfigureAwait(false);
        return (await _store.GetAddressBooksAsync(accountId).ConfigureAwait(false))
            .Select(item => item.AddressBook).FirstOrDefault(book => string.Equals(book.RemoteId, created.ExactHref, StringComparison.Ordinal));
    }

    public async Task RenameAsync(Guid addressBookId, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var binding = await RequireBookAsync(addressBookId).ConfigureAwait(false);
        if (binding.State.IsReadOnly) throw new InvalidOperationException("The CardDAV address book is read-only.");
        var account = await RequireAccountAsync(binding.State.AccountId).ConfigureAwait(false);
        var settings = await SettingsAsync(account, cancellationToken).ConfigureAwait(false);
        await _client.RenameAddressBookAsync(settings, binding.State.ExactHref, displayName.Trim(), cancellationToken).ConfigureAwait(false);
        await RediscoverAsync(account, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid addressBookId, bool destructiveConfirmation, CancellationToken cancellationToken = default)
    {
        if (!destructiveConfirmation) throw new InvalidOperationException("Deleting a remote CardDAV address book requires explicit confirmation.");
        var binding = await RequireBookAsync(addressBookId).ConfigureAwait(false);
        if (binding.State.IsReadOnly) throw new InvalidOperationException("The CardDAV address book is read-only.");
        var account = await RequireAccountAsync(binding.State.AccountId).ConfigureAwait(false);
        var settings = await SettingsAsync(account, cancellationToken).ConfigureAwait(false);
        await _client.DeleteAddressBookAsync(settings, binding.State.ExactHref, cancellationToken).ConfigureAwait(false);
        var discovery = await _client.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false);
        if (discovery.AddressBooks.Any(book => string.Equals(book.ExactHref, binding.State.ExactHref, StringComparison.Ordinal)))
            throw new InvalidOperationException("The CardDAV server still reports the address book after deletion.");
        await _store.DeleteAddressBookStateAsync(addressBookId).ConfigureAwait(false);
        await _contacts.DeleteAddressBookAsync(addressBookId).ConfigureAwait(false);
        await _store.SaveDiscoveryAsync(account.Id, discovery).ConfigureAwait(false);
    }

    private async Task RediscoverAsync(MailAccount account, CardDavConnectionSettings settings, CancellationToken cancellationToken)
        => await _store.SaveDiscoveryAsync(account.Id, await _client.DiscoverAsync(settings, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    private async Task<MailAccount> RequireAccountAsync(Guid accountId)
        => await _accounts.GetAccountAsync(accountId).ConfigureAwait(false) ?? throw new InvalidOperationException("The CardDAV account was not found.");

    private async Task<CardDavBookBinding> RequireBookAsync(Guid addressBookId)
    {
        var books = await _contacts.GetAddressBooksAsync().ConfigureAwait(false);
        var book = books.FirstOrDefault(item => item.Id == addressBookId && item.SourceKind == ContactSourceKind.CardDav)
                   ?? throw new InvalidOperationException("The CardDAV address book was not found.");
        return (await _store.GetAddressBooksAsync(book.MailAccountId, addressBookId).ConfigureAwait(false)).Single();
    }

    private async Task<CardDavConnectionSettings> SettingsAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var server = account.ServerInformation ?? await _accounts.GetAccountCustomServerInformationAsync(account.Id).ConfigureAwait(false);
        var password = await _credentials.GetPasswordAsync(account.Id, cancellationToken).ConfigureAwait(false) ?? server?.CalDavPassword;
        return new CardDavConnectionSettings
        {
            ServiceUri = string.IsNullOrWhiteSpace(server?.CardDavServiceUrl) ? null : new Uri(server.CardDavServiceUrl),
            AccountAddress = account.Address,
            Authentication = new DavAuthenticationProfile
            {
                Kind = DavAuthenticationKind.Basic,
                Username = string.IsNullOrWhiteSpace(server?.CalDavUsername) ? account.Address : server.CalDavUsername,
                Password = password
            }
        };
    }
}
