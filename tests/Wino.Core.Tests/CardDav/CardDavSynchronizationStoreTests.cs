using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers.CardDav;
using Wino.Core.Tests.Helpers;
using Wino.Services.CardDav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class CardDavSynchronizationStoreTests
{
    [Fact]
    public async Task Synchronize_ExistingDiscoveryTokenWithoutBaseline_DownloadsContactsAndThenUsesDeltas()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<CardDavAccountState>();
        await database.Connection.CreateTableAsync<CardDavAddressBookState>();
        await database.Connection.CreateTableAsync<CardDavResourceShadow>();
        await database.Connection.CreateTableAsync<CardDavQuarantine>();
        var codec = new VCardCodec();
        var payloads = new Mock<ICardDavPayloadStore>();
        payloads.Setup(item => item.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("payload");
        var store = new CardDavSynchronizationStore(database, payloads.Object, codec);
        var account = new MailAccount
        {
            Id = Guid.NewGuid(), Address = "alice@example.test",
            ServerInformation = new CustomServerInformation { CardDavServiceUrl = "http://localhost/dav/" }
        };
        const string bookHref = "http://localhost/addressbooks/alice/contacts/";
        await store.SaveDiscoveryAsync(account.Id, new CardDavDiscoveryResult
        {
            AddressBooks = new[] { new CardDavAddressBook { ExactHref = bookHref, SupportsSyncCollection = true } }
        });
        var binding = (await store.GetAddressBooksAsync(account.Id)).Single();
        // Reproduce the persisted state left by the old discovery implementation.
        binding.State.SyncToken = "discovered-token";
        binding.State.RequiresFullReconciliation = false;
        binding.State.LastIncrementalSyncUtc = DateTime.UtcNow;
        await database.Connection.UpdateAsync(binding.State);
        var client = new Mock<ICardDavClient>();
        client.Setup(item => item.SyncCollectionAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CardDavConnectionSettings settings, CardDavAddressBook book, string token, int limit, CancellationToken cancellation) =>
                new CardDavSyncPage
                {
                    NextSyncToken = "applied-token",
                    Changes = token is null ? new[] { new CardDavResourceChange
                    {
                        ExactHref = bookHref + "minimal.vcf", ETag = "etag", StatusCode = 200,
                        VCard = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:lab-minimal\r\nFN:Lab Minimal\r\nEND:VCARD\r\n"
                    } } : Array.Empty<CardDavResourceChange>()
                });
        var engine = new CardDavSynchronizationEngine(client.Object, store, payloads.Object, codec,
            Mock.Of<IContactService>(), Mock.Of<IWinoLogger>(), Mock.Of<IDavCredentialStore>(), Mock.Of<IAccountService>());

        var result = await engine.SynchronizeAsync(account, new ContactSynchronizationOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        var contacts = await database.Connection.Table<AccountContact>().ToListAsync();
        contacts.Should().ContainSingle().Which.DisplayName.Should().Be("Lab Minimal");
        var state = (await store.GetAddressBooksAsync(account.Id)).Single().State;
        state.LastFullSyncUtc.Should().NotBeNull();
        state.RequiresFullReconciliation.Should().BeFalse();
        state.SyncToken.Should().Be("applied-token");

        await engine.SynchronizeAsync(account, new ContactSynchronizationOptions());

        client.Verify(item => item.SyncCollectionAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
            null, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(item => item.SyncCollectionAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
            "applied-token", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        (await database.Connection.Table<AccountContact>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Synchronize_ServerRejectsAdvertisedReports_FallsBackToListingAndGet()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<CardDavAccountState>();
        await database.Connection.CreateTableAsync<CardDavAddressBookState>();
        await database.Connection.CreateTableAsync<CardDavResourceShadow>();
        await database.Connection.CreateTableAsync<CardDavQuarantine>();
        var codec = new VCardCodec();
        var payloads = new Mock<ICardDavPayloadStore>();
        payloads.Setup(item => item.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("payload");
        var store = new CardDavSynchronizationStore(database, payloads.Object, codec);
        var account = new MailAccount
        {
            Id = Guid.NewGuid(), Address = "alice@example.test",
            ServerInformation = new CustomServerInformation { CardDavServiceUrl = "http://localhost/dav/" }
        };
        // Unique per test: the engine remembers refused reports per collection for the process.
        var bookHref = $"http://localhost/addressbooks/{Guid.NewGuid():N}/contacts/";
        await store.SaveDiscoveryAsync(account.Id, new CardDavDiscoveryResult
        {
            AddressBooks = new[] { new CardDavAddressBook { ExactHref = bookHref, SupportsSyncCollection = true, SupportsMultiget = true } }
        });
        var client = new Mock<ICardDavClient>();
        client.Setup(item => item.SyncCollectionAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DavRequestException(400, "Bad Request"));
        client.Setup(item => item.MultiGetAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DavRequestException(400, "Bad Request"));
        client.Setup(item => item.EnumerateResourcesAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new CardDavResourceChange { ExactHref = bookHref + "minimal.vcf", ETag = "etag", StatusCode = 200 } });
        client.Setup(item => item.GetResourceAsync(It.IsAny<CardDavConnectionSettings>(), bookHref + "minimal.vcf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CardDavResourceChange
            {
                ExactHref = bookHref + "minimal.vcf", ETag = "etag", StatusCode = 200,
                VCard = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:lab-minimal\r\nFN:Lab Minimal\r\nEND:VCARD\r\n"
            });
        var engine = new CardDavSynchronizationEngine(client.Object, store, payloads.Object, codec,
            Mock.Of<IContactService>(), Mock.Of<IWinoLogger>(), Mock.Of<IDavCredentialStore>(), Mock.Of<IAccountService>());

        var result = await engine.SynchronizeAsync(account, new ContactSynchronizationOptions());
        await engine.SynchronizeAsync(account, new ContactSynchronizationOptions());

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        result.Issues.Should().BeEmpty();
        var contacts = await database.Connection.Table<AccountContact>().ToListAsync();
        contacts.Should().ContainSingle().Which.DisplayName.Should().Be("Lab Minimal");
        // The refusal is remembered, so the second pass goes straight to the listing.
        client.Verify(item => item.SyncCollectionAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(item => item.EnumerateResourcesAsync(It.IsAny<CardDavConnectionSettings>(), It.IsAny<CardDavAddressBook>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Discovery_ServerTokenDoesNotSkipInitialContactDownload()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<CardDavAccountState>();
        await database.Connection.CreateTableAsync<CardDavAddressBookState>();
        var store = new CardDavSynchronizationStore(database, Mock.Of<ICardDavPayloadStore>(), Mock.Of<IVCardCodec>());
        var accountId = Guid.NewGuid();
        var discovery = new CardDavDiscoveryResult
        {
            AddressBooks = new[] { new CardDavAddressBook
            {
                ExactHref = "http://localhost/addressbooks/alice/contacts/",
                SyncToken = "server-current-token",
                SupportsSyncCollection = true
            } }
        };

        await store.SaveDiscoveryAsync(accountId, discovery);

        var binding = (await store.GetAddressBooksAsync(accountId)).Should().ContainSingle().Subject;
        binding.State.SyncToken.Should().BeNull();
        binding.State.RequiresFullReconciliation.Should().BeTrue();
    }

    [Fact]
    public async Task Rediscovery_PreservesPreviouslyAppliedTokenAndCompletedBaseline()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<CardDavAccountState>();
        await database.Connection.CreateTableAsync<CardDavAddressBookState>();
        var store = new CardDavSynchronizationStore(database, Mock.Of<ICardDavPayloadStore>(), Mock.Of<IVCardCodec>());
        var accountId = Guid.NewGuid();
        var discovery = new CardDavDiscoveryResult
        {
            AddressBooks = new[] { new CardDavAddressBook
            {
                ExactHref = "http://localhost/addressbooks/alice/contacts/",
                SyncToken = "server-newer-token",
                SupportsSyncCollection = true
            } }
        };
        await store.SaveDiscoveryAsync(accountId, discovery);
        var binding = (await store.GetAddressBooksAsync(accountId)).Single();
        binding.State.SyncToken = "applied-token";
        binding.State.RequiresFullReconciliation = false;
        binding.State.LastFullSyncUtc = DateTime.UtcNow;
        await database.Connection.UpdateAsync(binding.State);

        await store.SaveDiscoveryAsync(accountId, discovery);

        var state = (await store.GetAddressBooksAsync(accountId)).Single().State;
        state.SyncToken.Should().Be("applied-token");
        state.RequiresFullReconciliation.Should().BeFalse();
        state.LastFullSyncUtc.Should().Be(binding.State.LastFullSyncUtc);
    }
}
