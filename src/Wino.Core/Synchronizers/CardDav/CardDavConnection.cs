using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Synchronizers.CardDav;

/// <summary>
/// What every CardDAV operation of an account shares: how to reach the server, and the
/// lock that keeps a synchronization and a write to the same address book apart.
/// </summary>
internal static class CardDavConnection
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AddressBookLocks = new();

    public static async Task<CardDavConnectionSettings> CreateSettingsAsync(
        MailAccount account,
        IDavCredentialStore credentialStore,
        IAccountService accountService,
        CancellationToken cancellationToken)
    {
        var server = account.ServerInformation
                     ?? await accountService.GetAccountCustomServerInformationAsync(account.Id).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("CardDAV server settings are unavailable.");
        var password = await credentialStore.GetPasswordAsync(account.Id, cancellationToken).ConfigureAwait(false);

        return new CardDavConnectionSettings
        {
            ServiceUri = string.IsNullOrWhiteSpace(server.CardDavServiceUrl) ? null : new Uri(server.CardDavServiceUrl, UriKind.Absolute),
            AccountAddress = account.Address,
            Authentication = new DavAuthenticationProfile
            {
                Kind = DavAuthenticationKind.Basic,
                Username = string.IsNullOrWhiteSpace(server.CalDavUsername) ? account.Address : server.CalDavUsername,
                Password = password ?? server.CalDavPassword
            }
        };
    }

    public static async Task<IDisposable> LockAddressBookAsync(Guid addressBookId, CancellationToken cancellationToken)
    {
        var gate = AddressBookLocks.GetOrAdd(addressBookId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public static CardDavAddressBook ToProtocolBook(CardDavBookBinding binding)
        => new()
        {
            ExactHref = binding.State.ExactHref,
            DisplayName = binding.AddressBook.DisplayName,
            SyncToken = binding.State.SyncToken,
            CollectionTag = binding.State.CollectionTag,
            IsReadOnly = binding.State.IsReadOnly,
            SupportsSyncCollection = binding.State.SupportsSyncCollection,
            SupportsMultiget = binding.State.SupportsMultiget,
            SupportsVCard4 = binding.State.SupportsVCard4
        };

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
