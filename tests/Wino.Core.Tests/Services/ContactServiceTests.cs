using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ContactServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private ContactService _contactService = null!;
    private Guid _accountId;
    private Guid _addressBookId;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _contactService = new ContactService(_databaseService);
        _accountId = Guid.NewGuid();
        await _databaseService.Connection.InsertAsync(
            new MailAccount { Id = _accountId, Name = "Test", ProviderType = MailProviderType.IMAP4 },
            typeof(MailAccount));
        _addressBookId = (await _contactService.EnsureLocalAddressBookAsync(_accountId, "Local contacts")).Id;
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithNotificationReplyAddress_DoesNotPersistContact()
    {
        await _contactService.SaveAddressInformationAsync(_accountId,
        [
            new AccountContact
            {
                Address = "reply+ABCD1234@reply.github.com",
                Name = "[owner/repository] Issue #42"
            }
        ]);

        var contact = await _contactService.GetContactByAddressAsync(null, "reply+ABCD1234@reply.github.com");

        contact.Should().BeNull();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithHumanContact_PersistsContact()
    {
        await _contactService.SaveAddressInformationAsync(_accountId,
        [
            new AccountContact
            {
                Address = "alice@example.com",
                Name = "Alice Example"
            }
        ]);

        var contact = await _contactService.GetContactByAddressAsync(null, "alice@example.com");

        contact.Should().NotBeNull();
        contact!.Name.Should().Be("Alice Example");
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithExistingNoisyContact_RemovesAutoCapturedEntry()
    {
        var existing = new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = _accountId,
            AddressBookId = _addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = "GitHub Notifications",
            IsAutoCollected = true
        };
        await _databaseService.Connection.InsertAsync(existing, typeof(AccountContact));
        await _databaseService.Connection.InsertAsync(
            new ContactEmailAddress
            {
                Id = Guid.NewGuid(),
                ContactId = existing.Id,
                Address = "notifications@github.com",
                NormalizedAddress = "notifications@github.com",
                IsPrimary = true
            },
            typeof(ContactEmailAddress));

        await _contactService.SaveAddressInformationAsync(_accountId,
        [
            new AccountContact
            {
                Address = "notifications@github.com",
                Name = "[owner/repository] Issue #99"
            }
        ]);

        var contact = await _contactService.GetContactByAddressAsync(null, "notifications@github.com");

        contact.Should().BeNull();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_WithNoisyMimeGroup_SkipsGroupAndNoisyMembers()
    {
        var message = new MimeMessage();
        message.To.Add(new GroupAddress("[owner/repository] Issue #123", new InternetAddressList
        {
            new MailboxAddress("Alice Example", "alice@example.com"),
            new MailboxAddress("[owner/repository] Issue #123", "notifications@github.com")
        }));

        await _contactService.SaveAddressInformationAsync(_accountId, message);

        var contacts = await _contactService.ResolveRecipientCandidatesAsync(null, "alice");

        contacts.Select(c => c.Address).Should().Contain("alice@example.com");
        (await _contactService.GetContactByAddressAsync(null, "notifications@github.com")).Should().BeNull();
    }

    [Fact]
    public async Task SaveAddressInformationAsync_SameAddressInTwoAccounts_KeepsSeparateCards()
    {
        var first = new MailAccount { Id = Guid.NewGuid(), Name = "First", ProviderType = MailProviderType.IMAP4 };
        var second = new MailAccount { Id = Guid.NewGuid(), Name = "Second", ProviderType = MailProviderType.IMAP4 };
        await _databaseService.Connection.InsertAsync(first, typeof(MailAccount));
        await _databaseService.Connection.InsertAsync(second, typeof(MailAccount));

        await _contactService.SaveAddressInformationAsync(first.Id, [new AccountContact { Address = "same@example.com", Name = "First name" }]);
        await _contactService.SaveAddressInformationAsync(second.Id, [new AccountContact { Address = "same@example.com", Name = "Second name" }]);

        var cards = await _databaseService.Connection.Table<AccountContact>().ToListAsync();
        cards.Should().HaveCount(2);
        (await _contactService.ResolveRecipientCandidatesAsync(first.Id, "same@example.com"))!.Single().MailAccountId.Should().Be(first.Id);
    }

    [Fact]
    public async Task RichContact_ChildRowsRoundTrip()
    {
        var accountId = Guid.NewGuid();
        var book = await _contactService.EnsureLocalAddressBookAsync(accountId, "Local");
        var contact = new AccountContact
        {
            Id = Guid.NewGuid(), MailAccountId = accountId, AddressBookId = book.Id,
            SourceKind = ContactSourceKind.Local, DisplayName = "Alice", CompanyName = "Example",
            EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = "alice@example.com", IsPrimary = true }],
            PhoneNumbers = [new ContactPhoneNumber { Id = Guid.NewGuid(), Number = "+1 555 0100", Kind = ContactPhoneKind.Work }],
            PostalAddresses = [new ContactPostalAddress { Id = Guid.NewGuid(), Kind = ContactPostalAddressKind.Business, City = "Warsaw" }],
            ImAddresses = [new ContactImAddress { Id = Guid.NewGuid(), Address = "sip:alice@example.com" }],
            Relations = [new ContactRelation { Id = Guid.NewGuid(), Kind = ContactRelationKind.Manager, Name = "Morgan" }]
        };

        await _contactService.StageCreateAsync(contact);
        var loaded = await _contactService.GetContactAsync(contact.Id);

        loaded.Should().NotBeNull();
        loaded!.EmailAddresses.Should().ContainSingle();
        loaded.PhoneNumbers.Should().ContainSingle();
        loaded.PostalAddresses.Single().City.Should().Be("Warsaw");
        loaded.ImAddresses.Should().ContainSingle();
        loaded.Relations.Single().Name.Should().Be("Morgan");
    }

    [Fact]
    public async Task ReplaceAddressBookAsync_UnchangedPhotoKey_PreservesCachedPicture()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(
            _accountId,
            ContactSourceKind.Outlook,
            "default",
            "Contacts",
            true);
        var pictureId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        await _contactService.ReplaceAddressBookAsync(book.Id,
        [
            new AccountContact
            {
                Id = originalId,
                MailAccountId = _accountId,
                AddressBookId = book.Id,
                SourceKind = ContactSourceKind.Outlook,
                RemoteId = "remote-contact",
                RemotePhotoKey = "photo-version",
                ContactPictureFileId = pictureId,
                DisplayName = "Alice"
            }
        ], null);

        await _contactService.ReplaceAddressBookAsync(book.Id,
        [
            new AccountContact
            {
                MailAccountId = _accountId,
                AddressBookId = book.Id,
                SourceKind = ContactSourceKind.Outlook,
                RemoteId = "remote-contact",
                RemotePhotoKey = "photo-version",
                DisplayName = "Alice Updated"
            }
        ], null);

        var contact = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        contact.Id.Should().Be(originalId);
        contact.ContactPictureFileId.Should().Be(pictureId);
    }

    [Fact]
    public async Task ApplyDeltaAsync_WithoutANextToken_KeepsTheStoredDeltaToken()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Gmail, "people/me/connections", "Gmail", true);
        await _contactService.ApplyDeltaAsync(book.Id, new Wino.Core.Domain.Models.Contacts.ContactSynchronizationBatch([], [], "token-1"), true);
        await _contactService.ApplyDeltaAsync(book.Id, new Wino.Core.Domain.Models.Contacts.ContactSynchronizationBatch([], [], null), true);

        (await _contactService.GetAddressBooksAsync(_accountId)).Single(item => item.Id == book.Id).DeltaToken.Should().Be("token-1");
    }

    [Fact]
    public async Task GetContactsPageAsync_PagesInStableAlphabeticalOrder()
    {
        foreach (var name in new[] { "Delta", "alpha", "Charlie", "bravo" })
            await _contactService.StageCreateAsync(new AccountContact { Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = _addressBookId, SourceKind = ContactSourceKind.Local, DisplayName = name, EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = $"{name.ToLowerInvariant()}@example.com" }] });

        var first = await _contactService.GetContactsPageAsync(ContactQueryFilter.All, 0, 2);
        var second = await _contactService.GetContactsPageAsync(ContactQueryFilter.All, 2, 2);

        first.Contacts.Select(contact => contact.DisplayName).Should().Equal("alpha", "bravo");
        second.Contacts.Select(contact => contact.DisplayName).Should().Equal("Charlie", "Delta");
    }

    [Fact]
    public async Task GetContactAsync_LoadsOnlyTheRequestedContactsChildRows()
    {
        var wanted = await _contactService.StageCreateAsync(new AccountContact { Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = _addressBookId, SourceKind = ContactSourceKind.Local, DisplayName = "Wanted", EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = "wanted@example.com" }], PhoneNumbers = [new ContactPhoneNumber { Id = Guid.NewGuid(), Number = "+100", Kind = ContactPhoneKind.Mobile }] });
        await _contactService.StageCreateAsync(new AccountContact { Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = _addressBookId, SourceKind = ContactSourceKind.Local, DisplayName = "Other", EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = "other@example.com" }] });

        var loaded = await _contactService.GetContactAsync(wanted.Id);

        loaded!.EmailAddresses.Should().ContainSingle().Which.Address.Should().Be("wanted@example.com");
        loaded.PhoneNumbers.Should().ContainSingle().Which.Number.Should().Be("+100");
    }

    [Fact]
    public async Task GetContactByAddressAsync_ResolvesContactsMatchedOnASecondaryAddress()
    {
        await _contactService.StageCreateAsync(new AccountContact { Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = _addressBookId, SourceKind = ContactSourceKind.Local, DisplayName = "Multi Address", EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = "primary@example.com", IsPrimary = true }, new ContactEmailAddress { Id = Guid.NewGuid(), Address = "secondary@example.com" }] });

        var resolved = await _contactService.GetContactByAddressAsync(_accountId, "secondary@example.com");

        resolved.Should().NotBeNull();
        resolved!.DisplayName.Should().Be("Multi Address");
    }

    [Fact]
    public async Task SaveAddressInformationAsync_UpdatesAnExistingAutoCollectedNameWithoutDuplicating()
    {
        await _contactService.SaveAddressInformationAsync(_accountId, [new AccountContact { Address = "carol@example.com", Name = "carol@example.com" }]);
        await _contactService.SaveAddressInformationAsync(_accountId, [new AccountContact { Address = "carol@example.com", Name = "Carol Example" }, new AccountContact { Address = "dave@example.com", Name = "Dave Example" }]);

        var page = await _contactService.GetContactsPageAsync(ContactQueryFilter.All, 0, 50);

        page.TotalCount.Should().Be(2);
        page.Contacts.Single(contact => contact.PrimaryEmailAddress == "carol@example.com").DisplayName.Should().Be("Carol Example");
    }

    [Fact]
    public async Task SetContactFavoriteAsync_RoundTripsThroughTheDatabase()
    {
        var contact = await CreateLocalContactAsync("Favorite Target");

        await _contactService.SetContactFavoriteAsync(contact.Id, true);

        (await _contactService.GetContactAsync(contact.Id))!.IsFavorite.Should().BeTrue();
        (await _contactService.GetFavoriteContactsCountAsync()).Should().Be(1);

        await _contactService.SetContactFavoriteAsync(contact.Id, false);

        (await _contactService.GetContactAsync(contact.Id))!.IsFavorite.Should().BeFalse();
        (await _contactService.GetFavoriteContactsCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetContactsPageAsync_FavoritesOnly_ReturnsOnlyFavorites()
    {
        var favorite = await CreateLocalContactAsync("Anna");
        await CreateLocalContactAsync("Boris");
        await _contactService.SetContactFavoriteAsync(favorite.Id, true);

        var page = await _contactService.GetContactsPageAsync(new ContactQueryFilter(FavoritesOnly: true), 0, 50);

        page.TotalCount.Should().Be(1);
        page.Contacts.Single().DisplayName.Should().Be("Anna");
    }

    [Fact]
    public async Task ContactLists_SupportCreateRenameMembershipAndDelete()
    {
        var first = await CreateLocalContactAsync("Anna");
        var second = await CreateLocalContactAsync("Boris");

        var list = await _contactService.CreateContactListAsync("Beta testers");
        list.Should().NotBeNull();

        await _contactService.AddContactsToListAsync(list!.Id, [first.Id, second.Id]);

        // Adding the same contact twice must not create a second membership row.
        await _contactService.AddContactsToListAsync(list.Id, [first.Id]);

        (await _contactService.GetContactListCountsAsync())[list.Id].Should().Be(2);
        (await _contactService.GetListIdsForContactAsync(first.Id)).Should().Equal(list.Id);

        list.Name = "Store reviewers";
        await _contactService.UpdateContactListAsync(list);
        (await _contactService.GetContactListsAsync()).Single().Name.Should().Be("Store reviewers");

        await _contactService.RemoveContactsFromListAsync(list.Id, [second.Id]);
        (await _contactService.GetContactListCountsAsync())[list.Id].Should().Be(1);

        await _contactService.DeleteContactListAsync(list.Id);

        (await _contactService.GetContactListsAsync()).Should().BeEmpty();
        (await _contactService.GetContactListCountsAsync()).Should().BeEmpty();

        // Deleting a list must not delete the contacts in it.
        (await _contactService.GetContactsPageAsync(ContactQueryFilter.All, 0, 50)).TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetContactsPageAsync_FiltersByListAndByAddressBook()
    {
        var listed = await CreateLocalContactAsync("Anna");
        await CreateLocalContactAsync("Boris");
        var list = await _contactService.CreateContactListAsync("Family");
        await _contactService.AddContactsToListAsync(list!.Id, [listed.Id]);

        var byList = await _contactService.GetContactsPageAsync(new ContactQueryFilter(ListId: list.Id), 0, 50);
        byList.Contacts.Select(contact => contact.DisplayName).Should().Equal("Anna");

        var byBook = await _contactService.GetContactsPageAsync(new ContactQueryFilter(AddressBookId: _addressBookId), 0, 50);
        byBook.TotalCount.Should().Be(2);

        var byOtherBook = await _contactService.GetContactsPageAsync(new ContactQueryFilter(AddressBookId: Guid.NewGuid()), 0, 50);
        byOtherBook.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ReplaceAddressBookAsync_KeepsFavoritesAndListMembershipForContactsTheServerStillReturns()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Gmail, "people/me/connections", "Gmail", true);
        await _contactService.ReplaceAddressBookAsync(book.Id, [RemoteContact("people/1", "Anna"), RemoteContact("people/2", "Boris")], "token-1");

        var stored = await _contactService.GetContactsByAddressBookAsync(book.Id);
        var anna = stored.Single(contact => contact.DisplayName == "Anna");
        var boris = stored.Single(contact => contact.DisplayName == "Boris");

        var list = await _contactService.CreateContactListAsync("Beta testers");
        await _contactService.SetContactFavoriteAsync(anna.Id, true);
        await _contactService.SetContactFavoriteAsync(boris.Id, true);
        await _contactService.AddContactsToListAsync(list!.Id, [anna.Id, boris.Id]);

        // A full sync where Boris has disappeared server-side.
        await _contactService.ReplaceAddressBookAsync(book.Id, [RemoteContact("people/1", "Anna Renamed")], "token-2");

        var afterSync = await _contactService.GetContactsByAddressBookAsync(book.Id);
        var survivor = afterSync.Should().ContainSingle().Subject;

        survivor.DisplayName.Should().Be("Anna Renamed");
        survivor.Id.Should().Be(anna.Id);
        survivor.IsFavorite.Should().BeTrue("a refresh must not clear favorites");

        (await _contactService.GetListIdsForContactAsync(anna.Id)).Should().Equal(list.Id);
        (await _contactService.GetContactListCountsAsync())[list.Id].Should().Be(1, "the removed contact's membership is cleaned up");
    }

    [Fact]
    public async Task ApplyDeltaAsync_KeepsFavoritesOnUpdatedContacts()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Gmail, "people/me/connections", "Gmail", true);
        await _contactService.ReplaceAddressBookAsync(book.Id, [RemoteContact("people/1", "Anna")], "token-1");

        var anna = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        await _contactService.SetContactFavoriteAsync(anna.Id, true);

        await _contactService.ApplyDeltaAsync(
            book.Id,
            new ContactSynchronizationBatch([RemoteContact("people/1", "Anna Renamed")], [], "token-2"),
            true);

        var updated = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        updated.DisplayName.Should().Be("Anna Renamed");
        updated.IsFavorite.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveRecipientListsAsync_MatchesListNamesAndExpandsToMemberAddresses()
    {
        var anna = await CreateLocalContactAsync("Anna");
        var boris = await CreateLocalContactAsync("Boris");
        var list = await _contactService.CreateContactListAsync("Beta testers");
        await _contactService.AddContactsToListAsync(list!.Id, [anna.Id, boris.Id]);

        var matches = await _contactService.ResolveRecipientListsAsync("beta");

        var match = matches.Should().ContainSingle().Subject;
        match.List.Id.Should().Be(list.Id);
        match.ExpandRecipients().Select(contact => contact.PrimaryEmailAddress)
            .Should().BeEquivalentTo("anna@example.com", "boris@example.com");

        // A one-character query and a non-matching query both return nothing.
        (await _contactService.ResolveRecipientListsAsync("b")).Should().BeEmpty();
        (await _contactService.ResolveRecipientListsAsync("nothing")).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveRecipientListsAsync_SkipsListsWithNoAddressableMembers()
    {
        var list = await _contactService.CreateContactListAsync("Empty list");

        (await _contactService.ResolveRecipientListsAsync("Empty")).Should().BeEmpty();

        var phoneOnly = await _contactService.StageCreateAsync(new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = _accountId,
            AddressBookId = _addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = "Phone Only",
            PhoneNumbers = [new ContactPhoneNumber { Id = Guid.NewGuid(), Number = "+100", Kind = ContactPhoneKind.Mobile }]
        });
        await _contactService.AddContactsToListAsync(list!.Id, [phoneOnly.Id]);

        (await _contactService.ResolveRecipientListsAsync("Empty")).Should().BeEmpty();
    }

    private Task<AccountContact> CreateLocalContactAsync(string displayName)
        => _contactService.StageCreateAsync(new AccountContact
        {
            Id = Guid.NewGuid(),
            MailAccountId = _accountId,
            AddressBookId = _addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = displayName,
            EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = $"{displayName.Replace(" ", string.Empty).ToLowerInvariant()}@example.com" }]
        });

    private static AccountContact RemoteContact(string remoteId, string displayName)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = Guid.Empty,
            SourceKind = ContactSourceKind.Gmail,
            RemoteId = remoteId,
            DisplayName = displayName,
            EmailAddresses = [new ContactEmailAddress { Id = Guid.NewGuid(), Address = $"{remoteId.Replace("/", "-")}@example.com" }]
        };
}
