using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers.CardDav;
using Wino.Core.Requests.Contact;
using Wino.Core.Tests.Helpers;
using Wino.Services.CardDav;
using Wino.Services.Dav;
using Xunit;

namespace Wino.Core.Tests.CardDav;

public sealed class CardDavSynchronizationProtocolTests
{
    [Fact]
    public async Task Synchronize_CollectionHrefOmitsTrailingSlash_DownloadsOnlyContactResources()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Respond = (_, body) => body.Contains("sync-collection", StringComparison.Ordinal)
            ? MultiStatus(Response(fixture.BookHref.TrimEnd('/'), "<D:getetag>collection</D:getetag>") +
                Response(fixture.Href, "<D:getetag>contact</D:getetag>"), "applied")
            : body.Contains($"<D:href>{fixture.BookHref.TrimEnd('/')}</D:href>", StringComparison.Ordinal) ||
              body.Contains($"<D:href>{new Uri(fixture.BookHref).AbsolutePath.TrimEnd('/')}</D:href>", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : body.Contains("addressbook-multiget", StringComparison.Ordinal)
                    ? MultiStatus(Response(fixture.Href, $"<D:getetag>contact</D:getetag><C:address-data>{Card}</C:address-data>"))
                    : new HttpResponseMessage(HttpStatusCode.BadRequest);

        var result = await fixture.SyncAsync();

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        (await fixture.Database.Connection.Table<AccountContact>().ToListAsync())
            .Should().ContainSingle().Which.DisplayName.Should().Be("Alice");
        fixture.Methods.Should().Equal("REPORT", "REPORT");
    }

    [Theory]
    [InlineData(false, "3.0")]
    [InlineData(true, "4.0")]
    public async Task CreateContact_UsesAdvertisedVCardVersion(bool supportsVersion4, string expectedVersion)
    {
        await using var fixture = await Fixture.CreateAsync();
        var state = await fixture.StateAsync();
        state.SupportsVCard4 = supportsVersion4;
        await fixture.Database.Connection.UpdateAsync(state);
        string? written = null;
        fixture.Respond = (_, body) =>
        {
            written = body;
            return new HttpResponseMessage(HttpStatusCode.Created);
        };

        await fixture.CreateContactAsync(new AccountContact
        {
            MailAccountId = fixture.Account.Id, AddressBookId = state.AddressBookId, DisplayName = "New contact"
        });

        written.Should().Contain($"VERSION:{expectedVersion}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronize_MultigetOmitsBody_FetchesContactBeforeCommittingToken(bool propertyNotFound)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Respond = (method, body) => method == "GET"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Card) }
            : body.Contains("sync-collection", StringComparison.Ordinal)
                ? MultiStatus(Response(fixture.Href, "<D:getetag>etag</D:getetag>"), "applied")
                : MultiStatus(propertyNotFound
                    ? $"<D:response><D:href>{fixture.Href}</D:href><D:propstat><D:prop><C:address-data /></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat></D:response>"
                    : Response(fixture.Href, "<D:getetag>etag</D:getetag>"));

        var result = await fixture.SyncAsync();

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        (await fixture.Database.Connection.Table<AccountContact>().ToListAsync())
            .Should().ContainSingle().Which.DisplayName.Should().Be("Alice");
        (await fixture.StateAsync()).SyncToken.Should().Be("applied");
        fixture.Methods.Should().Contain("GET");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Synchronize_FailedMultiget_DoesNotAdvanceTokenOrDeleteContacts(int status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Respond = (_, body) => body.Contains("sync-collection", StringComparison.Ordinal)
            ? MultiStatus(Response(fixture.Href, "<D:getetag>changed</D:getetag>"), "new-token")
            : new HttpResponseMessage((HttpStatusCode)status);

        var result = await fixture.SyncAsync();

        result.Issues.Should().NotBeEmpty();
        (await fixture.StateAsync()).SyncToken.Should().Be("baseline");
        (await fixture.Database.Connection.Table<AccountContact>().CountAsync()).Should().Be(1);
        fixture.Methods.Should().NotContain("GET");
    }

    [Fact]
    public async Task Synchronize_RepeatedTruncatedToken_StopsWithoutCompletingReconciliation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requests = 0;
        fixture.Respond = (_, _) => ++requests <= 2
            ? MultiStatus($"<D:response><D:href>{fixture.BookHref}</D:href><D:status>HTTP/1.1 507 Insufficient Storage</D:status></D:response>", "same-token")
            : MultiStatus(string.Empty, "finished");

        var result = await fixture.SyncAsync();

        result.Issues.Should().NotBeEmpty();
        requests.Should().Be(2);
        (await fixture.StateAsync()).RequiresFullReconciliation.Should().BeTrue();
        (await fixture.StateAsync()).LastFullSyncUtc.Should().BeNull();
    }

    [Fact]
    public async Task Synchronize_MissingSyncToken_DoesNotCompleteReconciliation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Respond = (_, _) => MultiStatus(string.Empty);

        var result = await fixture.SyncAsync();

        result.Issues.Should().NotBeEmpty();
        (await fixture.StateAsync()).RequiresFullReconciliation.Should().BeTrue();
    }

    [Fact]
    public async Task Synchronize_ResourceNotFound_RemovesContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Respond = (_, _) => MultiStatus(
            $"<D:response><D:href>{fixture.Href}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>", "deleted");

        var result = await fixture.SyncAsync();

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        (await fixture.Database.Connection.Table<AccountContact>().CountAsync()).Should().Be(0);
        (await fixture.StateAsync()).SyncToken.Should().Be("deleted");
    }

    [Fact]
    public async Task Synchronize_CanceledDuringReport_ReturnsCanceled()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Respond = (_, _) =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };

        var result = await fixture.SyncAsync(cancellation.Token);

        result.CompletedState.Should().Be(SynchronizationCompletedState.Canceled);
        (await fixture.StateAsync()).SyncToken.Should().BeNull();
    }

    [Fact]
    public async Task Synchronize_CollectionListingDenied_PreservesContacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var state = await fixture.StateAsync();
        state.SupportsSyncCollection = false;
        await fixture.Database.Connection.UpdateAsync(state);
        fixture.Respond = (_, _) => MultiStatus(
            $"<D:response><D:href>{fixture.BookHref}</D:href><D:status>HTTP/1.1 403 Forbidden</D:status></D:response>");

        var result = await fixture.SyncAsync();

        result.Issues.Should().NotBeEmpty();
        (await fixture.Database.Connection.Table<AccountContact>().CountAsync()).Should().Be(1);
        (await fixture.StateAsync()).RequiresFullReconciliation.Should().BeTrue();
    }

    [Fact]
    public async Task Synchronize_ExistingContactWithoutShadow_PreservesIdentityAndFavorite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(), MailAccountId = fixture.Account.Id,
            AddressBookId = (await fixture.StateAsync()).AddressBookId,
            SourceKind = ContactSourceKind.CardDav, RemoteId = fixture.Href,
            DisplayName = "Old name", IsFavorite = true
        };
        await fixture.Database.Connection.InsertAsync(contact);
        fixture.Respond = (_, _) => MultiStatus(Response(fixture.Href,
            $"<D:getetag>etag</D:getetag><C:address-data>{Card}</C:address-data>"), "applied");

        var result = await fixture.SyncAsync();

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        var saved = (await fixture.Database.Connection.Table<AccountContact>().ToListAsync()).Should().ContainSingle().Subject;
        saved.Id.Should().Be(contact.Id);
        saved.IsFavorite.Should().BeTrue();
        saved.DisplayName.Should().Be("Alice");
    }

    private const string Card = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:alice\r\nFN:Alice\r\nEND:VCARD\r\n";

    [Fact]
    public async Task Synchronize_ResourceError_DoesNotCommitTokenAndLosesNoContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Respond = (_, _) => MultiStatus(
            $"<D:response><D:href>{fixture.Href}</D:href><D:status>HTTP/1.1 503 Service Unavailable</D:status></D:response>", "new-token");

        var result = await fixture.SyncAsync();

        result.Issues.Should().NotBeEmpty();
        (await fixture.StateAsync()).SyncToken.Should().Be("baseline");
        (await fixture.Database.Connection.Table<AccountContact>().CountAsync()).Should().Be(1);
    }

    private static string Response(string href, string properties) =>
        $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{properties}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";

    private static HttpResponseMessage MultiStatus(string responses, string? token = null) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent($"<D:multistatus xmlns:D='DAV:' xmlns:C='urn:ietf:params:xml:ns:carddav'>{responses}{(token is null ? "" : $"<D:sync-token>{token}</D:sync-token>")}</D:multistatus>", Encoding.UTF8, "application/xml")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public InMemoryDatabaseService Database { get; } = new();
        public string BookHref { get; } = $"https://contacts.example.test/{Guid.NewGuid():N}/";
        public string Href => BookHref + "alice.vcf";
        public MailAccount Account { get; } = new()
        {
            Id = Guid.NewGuid(), Address = "alice@example.test",
            ServerInformation = new CustomServerInformation { CardDavServiceUrl = "https://contacts.example.test/" }
        };
        public List<string> Methods { get; } = [];
        public Func<string, string, HttpResponseMessage> Respond { get; set; } = null!;
        private CardDavSynchronizationStore _store = null!;
        private CardDavSynchronizationEngine _engine = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Database.InitializeAsync();
            await fixture.Database.Connection.CreateTableAsync<CardDavAccountState>();
            await fixture.Database.Connection.CreateTableAsync<CardDavAddressBookState>();
            await fixture.Database.Connection.CreateTableAsync<CardDavResourceShadow>();
            await fixture.Database.Connection.CreateTableAsync<CardDavQuarantine>();
            await fixture.Database.Connection.ExecuteAsync("CREATE UNIQUE INDEX IX_ContactCard_RemoteIdentity ON ContactCard(MailAccountId, SourceKind, AddressBookId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''");
            var codec = new VCardCodec();
            var payloads = new Mock<ICardDavPayloadStore>();
            payloads.Setup(item => item.SaveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("payload");
            fixture._store = new CardDavSynchronizationStore(fixture.Database, payloads.Object, codec);
            await fixture._store.SaveDiscoveryAsync(fixture.Account.Id, new CardDavDiscoveryResult
            {
                AddressBooks = new[] { new CardDavAddressBook { ExactHref = fixture.BookHref, SupportsSyncCollection = true, SupportsMultiget = true } }
            });
            var transport = new Mock<IDavTransport>();
            transport.Setup(item => item.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<DavAuthenticationProfile>(), It.IsAny<CancellationToken>()))
                .Returns(async (HttpRequestMessage request, DavAuthenticationProfile _, CancellationToken cancellation) =>
                {
                    fixture.Methods.Add(request.Method.Method);
                    var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation);
                    var response = fixture.Respond(request.Method.Method, body);
                    response.RequestMessage = request;
                    return response;
                });
            fixture._engine = new CardDavSynchronizationEngine(new CardDavClient(transport.Object, new DavMultistatusReader()),
                fixture._store, payloads.Object, codec, Mock.Of<IContactService>(), Mock.Of<IWinoLogger>(),
                Mock.Of<IDavCredentialStore>(), Mock.Of<IAccountService>());
            return fixture;
        }

        public async Task SeedAsync()
        {
            Respond = (_, _) => MultiStatus(Response(Href, $"<D:getetag>etag</D:getetag><C:address-data>{Card}</C:address-data>"), "baseline");
            (await SyncAsync()).CompletedState.Should().Be(SynchronizationCompletedState.Success);
            Methods.Clear();
        }

        public Task<ContactSynchronizationResult> SyncAsync(CancellationToken cancellation = default) => _engine.SynchronizeAsync(Account, new ContactSynchronizationOptions(), cancellation);
        public Task CreateContactAsync(AccountContact contact) => _engine.ExecuteRequestsAsync(Account,
            new IContactActionRequest[] { new ContactActionRequest(contact, ContactSynchronizerOperation.Create) });
        public async Task<CardDavAddressBookState> StateAsync() => (await _store.GetAddressBooksAsync(Account.Id)).Single().State;
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
