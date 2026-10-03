using System.Net;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Contact;
using Wino.Core.Services;
using Wino.Core.Synchronizers.CardDav;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Wino.Services.CardDav;
using Wino.Services.Dav;
using Xunit;
using static Wino.Core.Tests.CardDav.FakeCardDavServer;

namespace Wino.Core.Tests.CardDav;

/// <summary>
/// Runs the real client, store, codec, contact service and engine against
/// <see cref="FakeCardDavServer"/>.
/// </summary>
public sealed class CardDavSynchronizationTests
{
    [Fact]
    public async Task InitialSync_SeparatesPeopleFromGroupsAndResolvesMembers()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice", "alice@example.test"));
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        fixture.Server.Put("c", Person("UID-C", "Carol"));
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-A"));
        fixture.Server.Put("g3", Group("UID-G3", "Third List", "UID-A", "UID-B", "UID-C", "UID-UNKNOWN"));

        var result = await fixture.SyncAsync();

        result.Issues.Should().BeEmpty();
        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice", "Bob", "Carol");
        (await fixture.ListMembersAsync()).Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["First List"] = ["Alice"],
            ["Third List"] = ["Alice", "Bob", "Carol"]
        });
        var list = (await fixture.ListsAsync()).First();
        list.AddressBookId.Should().Be((await fixture.StateAsync()).AddressBookId);
        list.MailAccountId.Should().Be(fixture.Account.Id);
        (await fixture.StateAsync()).SyncToken.Should().NotBeNullOrEmpty();
        fixture.Server.Requests.Should().Contain(request => request.EndsWith("multiget"));
        fixture.Server.Requests.Should().NotContain(request => request.StartsWith("GET"));
    }

    [Fact]
    public async Task UnchangedServer_CostsOneListingRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        fixture.Server.Requests.Clear();

        var result = await fixture.SyncAsync();

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        fixture.Server.Requests.Should().Equal($"PROPFIND {HomePath}");
    }

    [Fact]
    public async Task DeltaSync_AppliesAddedUpdatedAndDeletedContacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        await fixture.SyncAsync();
        var alice = (await fixture.ContactsAsync()).Single(contact => contact.DisplayName == "Alice");
        await fixture.Contacts.SetContactFavoriteAsync(alice.Id, true);
        fixture.Server.Requests.Clear();

        fixture.Server.Put("a", Person("UID-A", "Alice Renamed"));
        fixture.Server.Delete("b");
        fixture.Server.Put("c", Person("UID-C", "Carol"));
        var result = await fixture.SyncAsync();

        result.Issues.Should().BeEmpty();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice Renamed", "Carol");
        var renamed = (await fixture.ContactsAsync()).Single(contact => contact.DisplayName == "Alice Renamed");
        renamed.Id.Should().Be(alice.Id);
        renamed.IsFavorite.Should().BeTrue();
        result.DeletedCount.Should().Be(1);
        fixture.Server.Requests.Should().Equal(
            $"PROPFIND {HomePath}", $"REPORT {BookPath} sync-collection", $"REPORT {BookPath} multiget");
    }

    [Fact]
    public async Task DeltaSync_AppliesGroupCreationRenameMembershipAndDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-A"));
        fixture.Server.Put("g2", Group("UID-G2", "Second List", "UID-B"));
        await fixture.SyncAsync();
        var firstListId = (await fixture.ListsAsync()).Single(list => list.Name == "First List").Id;

        fixture.Server.Put("g1", Group("UID-G1", "Renamed List", "UID-A", "UID-B"));
        fixture.Server.Delete("g2");
        fixture.Server.Put("g4", Group("UID-G4", "Fourth List"));
        await fixture.SyncAsync();

        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice", "Bob");
        (await fixture.ListMembersAsync()).Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["Renamed List"] = ["Alice", "Bob"],
            ["Fourth List"] = []
        });
        (await fixture.ListsAsync()).Single(list => list.Name == "Renamed List").Id.Should().Be(firstListId);
    }

    [Fact]
    public async Task DeltaSync_MemberArrivingAfterItsGroup_JoinsTheList()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-LATE"));
        await fixture.SyncAsync();
        (await fixture.ListMembersAsync())["First List"].Should().BeEmpty();

        fixture.Server.Put("late", Person("UID-LATE", "Late"));
        await fixture.SyncAsync();

        (await fixture.ListMembersAsync())["First List"].Should().Equal("Late");
    }

    [Fact]
    public async Task ExpiredSyncToken_FallsBackToListingAndRemovesStaleResources()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        await fixture.SyncAsync();

        fixture.Server.Delete("b");
        fixture.Server.Put("c", Person("UID-C", "Carol"));
        fixture.Server.OldestValidRevision = int.MaxValue;
        fixture.Server.Requests.Clear();
        var result = await fixture.SyncAsync();

        result.Issues.Should().BeEmpty();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice", "Carol");

        // The stale token is refused, the collection is listed and only Carol is downloaded.
        fixture.Server.Requests.Count(request => request.EndsWith("sync-collection")).Should().Be(2);
        fixture.Server.Requests.Count(request => request.EndsWith("multiget")).Should().Be(1);
    }

    [Fact]
    public async Task ServerWithoutReports_SynchronizesWithPropfindAndGet()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.SupportsSyncCollection = false;
        fixture.Server.SupportsMultiget = false;
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-A"));
        await fixture.SyncAsync();

        fixture.Server.Delete("a");
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        fixture.Server.Requests.Clear();
        var result = await fixture.SyncAsync();

        result.Issues.Should().BeEmpty();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Bob");
        (await fixture.ListMembersAsync())["First List"].Should().BeEmpty();
        fixture.Server.Requests.Should().Equal($"PROPFIND {HomePath}", $"PROPFIND {BookPath}", $"GET {BookPath}b.vcf");
    }

    [Fact]
    public async Task FailedDownload_KeepsCheckpointAndContacts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var token = (await fixture.StateAsync()).SyncToken;

        fixture.Server.Put("a", Person("UID-A", "Alice Renamed"));
        fixture.Server.Intercept = request => request.Method.Method == "REPORT" && fixture.Server.Requests[^1].EndsWith("multiget")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;
        var failed = await fixture.SyncAsync();

        failed.Issues.Should().NotBeEmpty();
        (await fixture.StateAsync()).SyncToken.Should().Be(token);
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice");

        fixture.Server.Intercept = null;
        await fixture.SyncAsync();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice Renamed");
    }

    [Fact]
    public async Task ResourceStoredAsContact_ThatIsAGroup_BecomesAList()
    {
        // State left by a build that did not recognize groups, after the database migration
        // cleared versions and checkpoints.
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-A"));
        await fixture.SyncAsync();
        var state = await fixture.StateAsync();
        var group = (await fixture.Store.GetResourcesAsync(state.AddressBookId)).Single(resource => resource.Kind == CardDavResourceKind.Group);
        await fixture.Database.Connection.ExecuteAsync("DELETE FROM ContactList");
        await fixture.Database.Connection.ExecuteAsync("DELETE FROM ContactListMember");
        var legacy = new AccountContact
        {
            MailAccountId = fixture.Account.Id, AddressBookId = state.AddressBookId, SourceKind = ContactSourceKind.CardDav,
            RemoteId = group.ExactHref, DisplayName = "First List"
        };
        await fixture.Database.Connection.InsertAsync(legacy);
        await fixture.Database.Connection.ExecuteAsync(
            "UPDATE CardDavResourceShadow SET ETag = NULL, Kind = 0, ListId = NULL, ContactId = ? WHERE Id = ?", legacy.Id, group.Id);
        await fixture.Database.Connection.ExecuteAsync("UPDATE CardDavAddressBookState SET SyncToken = NULL, CollectionTag = NULL, LastSyncUtc = NULL");

        await fixture.SyncAsync();

        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice");
        (await fixture.ListMembersAsync())["First List"].Should().Equal("Alice");
    }

    [Fact]
    public async Task CreateUpdateDelete_WritesToServerAndNextSyncDownloadsNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:UID-A\r\nN:Alice;;;;\r\nFN:Alice\r\nPHOTO;ENCODING=b:AAAA\r\nX-CUSTOM:kept\r\nEND:VCARD\r\n");
        await fixture.SyncAsync();
        var bookId = (await fixture.StateAsync()).AddressBookId;

        // Create
        var created = new AccountContact
        {
            MailAccountId = fixture.Account.Id, AddressBookId = bookId, SourceKind = ContactSourceKind.CardDav,
            DisplayName = "New Person", GivenName = "New", Surname = "Person"
        };
        created.EmailAddresses.Add(new ContactEmailAddress { Address = "new@example.test", IsPrimary = true });
        await fixture.ExecuteAsync(new ContactActionRequest(created, ContactSynchronizerOperation.Create));

        var createdName = fixture.Server.Names.Single(name => name != "a");
        fixture.Server.VCardOf(createdName).Should().Contain("FN:New Person").And.Contain("new@example.test");
        var stored = (await fixture.ContactsAsync()).Single(contact => contact.DisplayName == "New Person");
        stored.Id.Should().Be(created.Id);
        stored.RemoteId.Should().Be(fixture.Server.HrefOf(createdName));
        stored.RemoteVersion.Should().NotBeNullOrEmpty();

        // Update keeps what Wino does not model.
        var alice = (await fixture.ContactsAsync()).Single(contact => contact.DisplayName == "Alice");
        var edited = await fixture.Contacts.GetContactAsync(alice.Id);
        edited.DisplayName = "Alice Edited";
        await fixture.ExecuteAsync(new ContactActionRequest(edited, ContactSynchronizerOperation.Update, alice));

        fixture.Server.VCardOf("a").Should().Contain("FN:Alice Edited").And.Contain("PHOTO;ENCODING=b:AAAA").And.Contain("X-CUSTOM:kept");
        (await fixture.Contacts.GetContactAsync(alice.Id)).DisplayName.Should().Be("Alice Edited");

        // The server changed only through this client: the delta lists both resources and
        // their versions are already known.
        fixture.Server.Requests.Clear();
        var result = await fixture.SyncAsync();
        result.Issues.Should().BeEmpty();
        fixture.Server.Requests.Should().Equal($"PROPFIND {HomePath}", $"REPORT {BookPath} sync-collection");
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice Edited", "New Person");

        // Delete
        await fixture.ExecuteAsync(new ContactActionRequest(stored, ContactSynchronizerOperation.Delete, stored));

        fixture.Server.Has(createdName).Should().BeFalse();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice Edited");
        await fixture.SyncAsync();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice Edited");
    }

    [Fact]
    public async Task Update_ContactChangedOnServerSinceLastSync_EditsTheCurrentCard()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var alice = await fixture.Contacts.GetContactAsync((await fixture.ContactsAsync()).Single().Id);

        fixture.Server.Put("a", Person("UID-A", "Alice", "added-elsewhere@example.test").Replace("END:VCARD", "X-ELSEWHERE:1\r\nEND:VCARD"));
        alice.Notes = "Edited in Wino";
        await fixture.ExecuteAsync(new ContactActionRequest(alice, ContactSynchronizerOperation.Update, alice));

        fixture.Server.VCardOf("a").Should().Contain("NOTE:Edited in Wino").And.Contain("X-ELSEWHERE:1");
    }

    [Fact]
    public async Task SynchronizedList_MembershipRenameAndDeleteAreWrittenToTheGroup()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        fixture.Server.Put("b", Person("UID-B", "Bob"));
        fixture.Server.Put("g1", Group("UID-G1", "First List", "UID-A", "UID-UNKNOWN"));
        await fixture.SyncAsync();
        var list = (await fixture.ListsAsync()).Single();
        var contacts = await fixture.ContactsAsync();
        var alice = contacts.Single(contact => contact.DisplayName == "Alice");
        var bob = contacts.Single(contact => contact.DisplayName == "Bob");

        await fixture.Lists.UpdateMembersAsync(list.Id, [bob.Id], [alice.Id]);

        fixture.Server.VCardOf("g1").Should().Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:UID-B")
            .And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:UID-UNKNOWN")
            .And.NotContain("urn:uuid:UID-A");

        await fixture.Lists.RenameAsync(list.Id, "Renamed");

        fixture.Server.VCardOf("g1").Should().Contain("FN:Renamed").And.Contain("X-ADDRESSBOOKSERVER-KIND:group");
        fixture.Server.Requests.Clear();
        await fixture.SyncAsync();
        fixture.Server.Requests.Should().NotContain(request => request.EndsWith("multiget"));
        (await fixture.ListMembersAsync())["First List"].Should().Equal("Bob");

        await fixture.Lists.DeleteAsync(list.Id);

        fixture.Server.Has("g1").Should().BeFalse();
        (await fixture.ContactNamesAsync()).Should().BeEquivalentTo("Alice", "Bob");
    }

    [Fact]
    public async Task Categories_AreReadFromTheCard_AndWrittenBackToIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice").Replace("END:VCARD", "CATEGORIES:Family\r\nX-ELSEWHERE:1\r\nEND:VCARD"));
        await fixture.SyncAsync();
        var alice = (await fixture.ContactsAsync()).Single();
        var family = await fixture.Database.Connection.Table<MailCategory>().FirstAsync();
        family.Name.Should().Be("Family");

        var loaded = await fixture.Contacts.GetContactAsync(alice.Id);
        loaded!.Categories.Single().Id.Should().Be(family.Id);

        await fixture.ExecuteAsync(new ContactCategoryRequest(loaded, [family, new MailCategory { Id = Guid.NewGuid(), Name = "Work" }]));

        fixture.Server.VCardOf("a").Should().Contain("CATEGORIES:Family,Work").And.Contain("X-ELSEWHERE:1");
        (await fixture.Contacts.GetContactAsync(alice.Id))!.Categories.Select(category => category.Name).Should().Equal("Family", "Work");

        fixture.Server.Requests.Clear();
        await fixture.SyncAsync();
        fixture.Server.Requests.Should().NotContain(request => request.EndsWith("multiget"), "the write recorded the version the server returned");
    }

    [Fact]
    public async Task ListRequests_AreWrittenToTheServerBeforeTheyAreStored()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var state = await fixture.StateAsync();
        var alice = (await fixture.ContactsAsync()).Single();
        var list = new ContactList { Name = "Team", MailAccountId = fixture.Account.Id, AddressBookId = state.AddressBookId };

        await fixture.ExecuteAsync(new ContactListRequest(ContactSynchronizerOperation.CreateList, list));
        await fixture.ExecuteAsync(new ContactListRequest(ContactSynchronizerOperation.UpdateListMembers, list, addedContactIds: [alice.Id]));
        list.Name = "Crew";
        await fixture.ExecuteAsync(new ContactListRequest(ContactSynchronizerOperation.RenameList, list));

        var group = fixture.Server.Names.Single(name => name != "a");
        fixture.Server.VCardOf(group).Should().Contain("FN:Crew").And.Contain("urn:uuid:UID-A");
        (await fixture.ListMembersAsync())["Crew"].Should().Equal("Alice");

        await fixture.ExecuteAsync(new ContactListRequest(ContactSynchronizerOperation.DeleteList, list));

        fixture.Server.Names.Should().Equal("a");
        (await fixture.ListsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NewList_IsWrittenAsAGroup_AndTheNextPullKeepsOneList()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var state = await fixture.StateAsync();
        var alice = (await fixture.ContactsAsync()).Single();
        var list = new ContactList { Name = "Created in Wino", MailAccountId = fixture.Account.Id, AddressBookId = state.AddressBookId };

        await fixture.Lists.CreateAsync(list);
        await fixture.Contacts.SaveContactListAsync(list);
        await fixture.Lists.UpdateMembersAsync(list.Id, [alice.Id], []);

        var created = fixture.Server.Names.Single(name => name != "a");
        fixture.Server.VCardOf(created).Should().Contain("FN:Created in Wino")
            .And.Contain("X-ADDRESSBOOKSERVER-KIND:group")
            .And.Contain("X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:UID-A");

        await fixture.SyncAsync();

        (await fixture.ListsAsync()).Single().Id.Should().Be(list.Id);
        (await fixture.ListMembersAsync())["Created in Wino"].Should().Equal("Alice");
    }

    [Fact]
    public async Task SynchronizedList_ContactOfAnotherAddressBook_IsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("g1", Group("UID-G1", "First List"));
        await fixture.SyncAsync();
        var foreign = new AccountContact { MailAccountId = Guid.NewGuid(), AddressBookId = Guid.NewGuid(), DisplayName = "Foreign" };
        await fixture.Database.Connection.InsertAsync(foreign);

        var listId = (await fixture.ListsAsync()).Single().Id;

        var action = () => fixture.Lists.UpdateMembersAsync(listId, [foreign.Id], []);

        await action.Should().ThrowAsync<InvalidOperationException>();
        fixture.Server.VCardOf("g1").Should().NotContain("MEMBER");
    }

    [Fact]
    public async Task LocalList_IsNeverSentToTheServer()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var local = await fixture.Contacts.CreateContactListAsync("Local");
        var alice = (await fixture.ContactsAsync()).Single();
        fixture.Server.Requests.Clear();

        await fixture.Lists.UpdateMembersAsync(local.Id, [alice.Id], []);
        await fixture.Lists.RenameAsync(local.Id, "Still local");
        await fixture.Lists.DeleteAsync(local.Id);

        fixture.Server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddressBookMovedToAnotherHost_KeepsContactIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Server.Put("a", Person("UID-A", "Alice"));
        await fixture.SyncAsync();
        var alice = (await fixture.ContactsAsync()).Single();
        await fixture.Contacts.SetContactFavoriteAsync(alice.Id, true);

        // The same account, as it looked while it lived on another shard.
        var oldBook = BookHref.Replace("p42-", "p39-");
        await fixture.Database.Connection.ExecuteAsync("UPDATE ContactAddressBook SET RemoteId = ?", oldBook);
        await fixture.Database.Connection.ExecuteAsync("UPDATE CardDavAddressBookState SET ExactHref = ?", oldBook);
        await fixture.Database.Connection.ExecuteAsync("UPDATE CardDavResourceShadow SET ExactHref = replace(ExactHref, 'p42-', 'p39-')");
        await fixture.Database.Connection.ExecuteAsync("UPDATE ContactCard SET RemoteId = replace(RemoteId, 'p42-', 'p39-')");
        fixture.Server.Requests.Clear();

        await fixture.SyncAsync();

        var moved = (await fixture.ContactsAsync()).Should().ContainSingle().Subject;
        moved.Id.Should().Be(alice.Id);
        moved.IsFavorite.Should().BeTrue();
        moved.RemoteId.Should().Be(fixture.Server.HrefOf("a"));
        fixture.Server.Requests.Should().NotContain(request => request.EndsWith("multiget"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public InMemoryDatabaseService Database { get; } = new();
        public FakeCardDavServer Server { get; } = new();
        public MailAccount Account { get; } = new()
        {
            Id = Guid.NewGuid(), Address = "user@example.test",
            ServerInformation = new CustomServerInformation { CardDavServiceUrl = Origin + "/", CalDavUsername = "user@example.test" }
        };
        public CardDavSynchronizationStore Store { get; private set; } = null!;
        public ContactService Contacts { get; private set; } = null!;
        public CardDavContactListService Lists { get; private set; } = null!;
        private CardDavSynchronizationEngine _engine = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Database.InitializeAsync();
            await fixture.Database.Connection.CreateTableAsync<CardDavAccountState>();
            await fixture.Database.Connection.CreateTableAsync<CardDavAddressBookState>();
            await fixture.Database.Connection.CreateTableAsync<CardDavResourceShadow>();
            await fixture.Database.Connection.ExecuteAsync("CREATE UNIQUE INDEX IX_ContactCard_RemoteIdentity ON ContactCard(MailAccountId, SourceKind, AddressBookId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''");
            await fixture.Database.Connection.ExecuteAsync("CREATE UNIQUE INDEX IX_CardDavResourceShadow_Book_Href ON CardDavResourceShadow(AddressBookId, ExactHref)");
            await fixture.Database.Connection.InsertAsync(fixture.Account);

            var codec = new VCardCodec();
            var client = new CardDavClient(fixture.Server, new DavMultistatusReader());
            var credentials = new Mock<IDavCredentialStore>();
            credentials.Setup(item => item.GetPasswordAsync(fixture.Account.Id, It.IsAny<CancellationToken>())).ReturnsAsync("app-password");
            var accounts = new Mock<IAccountService>();
            accounts.Setup(item => item.GetAccountAsync(fixture.Account.Id)).ReturnsAsync(fixture.Account);

            fixture.Store = new CardDavSynchronizationStore(fixture.Database);
            fixture.Contacts = new ContactService(fixture.Database);
            fixture.Lists = new CardDavContactListService(client, fixture.Store, codec, credentials.Object, accounts.Object, fixture.Contacts);
            fixture._engine = new CardDavSynchronizationEngine(client, fixture.Store, codec, fixture.Contacts,
                Mock.Of<IWinoLogger>(), credentials.Object, accounts.Object, listService: fixture.Lists);
            return fixture;
        }

        public Task<ContactSynchronizationResult> SyncAsync()
            => _engine.SynchronizeAsync(Account, new ContactSynchronizationOptions { AccountId = Account.Id, Type = ContactSynchronizationType.Delta });

        public Task ExecuteAsync(IContactActionRequest request)
            => _engine.ExecuteRequestsAsync(Account, [request]);

        public async Task<CardDavAddressBookState> StateAsync() => (await Store.GetAddressBooksAsync(Account.Id)).Single().State;
        public Task<List<AccountContact>> ContactsAsync() => Database.Connection.Table<AccountContact>().ToListAsync();
        public async Task<List<string>> ContactNamesAsync() => (await ContactsAsync()).Select(contact => contact.DisplayName).ToList();
        public Task<List<ContactList>> ListsAsync() => Database.Connection.Table<ContactList>().ToListAsync();

        public async Task<Dictionary<string, string[]>> ListMembersAsync()
        {
            var contacts = (await ContactsAsync()).ToDictionary(contact => contact.Id, contact => contact.DisplayName);
            var members = await Database.Connection.Table<ContactListMember>().ToListAsync();
            return (await ListsAsync()).ToDictionary(
                list => list.Name,
                list => members.Where(member => member.ListId == list.Id).Select(member => contacts[member.ContactId]).Order().ToArray());
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
