using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.MailItem;
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
    public async Task ContactList_RefusesContactsOfAnotherAddressBook_AndGoesWithItsAddressBook()
    {
        var own = await CreateLocalContactAsync("Anna");
        var otherBook = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Gmail, "people/me/connections", "Gmail", true);
        var foreign = await _contactService.StageCreateAsync(new AccountContact { Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = otherBook.Id, SourceKind = ContactSourceKind.Gmail, DisplayName = "Foreign" });
        var list = new ContactList { Name = "Family", MailAccountId = _accountId, AddressBookId = own.AddressBookId };
        await _contactService.SaveContactListAsync(list);

        var add = () => _contactService.AddContactsToListAsync(list.Id, [own.Id, foreign.Id]);

        await add.Should().ThrowAsync<InvalidOperationException>();
        (await _contactService.GetContactListCountsAsync()).Should().BeEmpty();

        await _contactService.AddContactsToListAsync(list.Id, [own.Id]);
        await _contactService.DeleteAddressBookAsync(own.AddressBookId);

        (await _contactService.GetContactListsAsync()).Should().BeEmpty();
        (await _contactService.GetContactListCountsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ContactCategories_UnknownNameBecomesACategoryWithAnUnusedColor()
    {
        var contact = await CreateLocalContactAsync("Anna");
        await _databaseService.Connection.InsertAsync(new MailCategory
        {
            Id = Guid.NewGuid(), MailAccountId = _accountId, Name = "Existing",
            BackgroundColorHex = MailCategoryPalette.DefaultOptions[0].BackgroundColorHex,
            TextColorHex = MailCategoryPalette.DefaultOptions[0].TextColorHex
        }, typeof(MailCategory));

        await _contactService.SetContactCategoriesAsync(contact.Id, ["existing", "Family"]);

        var categories = await _databaseService.Connection.Table<MailCategory>().ToListAsync();
        categories.Should().HaveCount(2, "a name that differs only in case is the same category");
        var family = categories.Single(category => category.Name == "Family");
        family.Source.Should().Be(MailCategorySource.Local);
        family.BackgroundColorHex.Should().Be(MailCategoryPalette.DefaultOptions[1].BackgroundColorHex);
        (await _contactService.GetContactAsync(contact.Id))!.Categories.Select(category => category.Name)
            .Should().Equal("Existing", "Family");
        (await _contactService.GetContactsByCategoryAsync(family.Id)).Single().Id.Should().Be(contact.Id);

        var page = await _contactService.GetContactsPageAsync(new ContactQueryFilter(CategoryId: family.Id), 0, 10);
        page.Contacts.Single().Id.Should().Be(contact.Id);
    }

    [Fact]
    public async Task ContactCategories_FollowTheProviderOnlyWhenItReportsThem()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Outlook, "default", "Outlook", true);
        AccountContact Remote(List<string>? names) => new()
        {
            Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = book.Id, SourceKind = ContactSourceKind.Outlook,
            RemoteId = "remote-1", DisplayName = "Anna", CategoryNames = names
        };

        await _contactService.ReplaceAddressBookAsync(book.Id, [Remote(["Family", "Work"])], "token-1");
        var contact = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        contact.Categories.Select(category => category.Name).Should().Equal("Family", "Work");

        // A provider without categories reports none, and the stored ones stay.
        await _contactService.ReplaceAddressBookAsync(book.Id, [Remote(null)], "token-2");
        (await _contactService.GetContactAsync(contact.Id))!.Categories.Should().HaveCount(2);

        await _contactService.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch([Remote(["Work"])], [], null), commitDeltaToken: false);
        (await _contactService.GetContactAsync(contact.Id))!.Categories.Select(category => category.Name).Should().Equal("Work");

        await _contactService.ReplaceAddressBookAsync(book.Id, [], "token-3");
        (await _databaseService.Connection.Table<ContactCategoryAssignment>().CountAsync()).Should().Be(0);
        (await _databaseService.Connection.Table<MailCategory>().CountAsync()).Should().Be(2, "a category outlives the contacts that carried it");
    }

    [Fact]
    public async Task MailCategories_ProviderListKeepsCategoriesThatOnlyExistOnTheDevice()
    {
        var contact = await CreateLocalContactAsync("Anna");
        await _contactService.SetContactCategoriesAsync(contact.Id, ["Family"]);
        var categoryService = new MailCategoryService(_databaseService);
        await categoryService.CreateCategoryAsync(new MailCategory { MailAccountId = _accountId, Name = "Stale", RemoteId = "stale", Source = MailCategorySource.Outlook });

        await categoryService.ReplaceCategoriesAsync(_accountId, [new MailCategory { Name = "Blue", RemoteId = "blue", Source = MailCategorySource.Outlook }]);

        (await categoryService.GetCategoriesAsync(_accountId)).Select(category => category.Name).Should().BeEquivalentTo("Blue", "Family");
        (await _contactService.GetContactAsync(contact.Id))!.Categories.Single().Name.Should().Be("Family");

        var family = (await categoryService.GetCategoriesAsync(_accountId)).Single(category => category.Name == "Family");
        await categoryService.DeleteCategoryAsync(family.Id);
        (await _contactService.GetContactAsync(contact.Id))!.Categories.Should().BeEmpty();
    }

    [Fact]
    public async Task ProviderLists_FollowTheProvider_AndLeaveOtherListsAlone()
    {
        var book = await _contactService.GetOrCreateProviderAddressBookAsync(_accountId, ContactSourceKind.Gmail, "people/me/connections", "Gmail", true);
        AccountContact Remote(List<string>? listIds) => new()
        {
            Id = Guid.NewGuid(), MailAccountId = _accountId, AddressBookId = book.Id, SourceKind = ContactSourceKind.Gmail,
            RemoteId = "people/c1", DisplayName = "Anna", ListRemoteIds = listIds
        };
        var pending = new ContactList { Name = "Not at Google yet", MailAccountId = _accountId, AddressBookId = book.Id };
        await _contactService.SaveContactListAsync(pending);

        await _contactService.ReplaceRemoteListsAsync(_accountId, book.Id,
            [new ContactList { Name = "Family", RemoteId = "contactGroups/1" }, new ContactList { Name = "Work", RemoteId = "contactGroups/2" }]);
        await _contactService.ReplaceAddressBookAsync(book.Id, [Remote(["contactGroups/myContacts", "contactGroups/1"])], "token-1");

        var contact = (await _contactService.GetContactsByAddressBookAsync(book.Id)).Single();
        var lists = (await _contactService.GetContactListsAsync()).ToDictionary(list => list.Name);
        lists.Keys.Should().BeEquivalentTo("Not at Google yet", "Family", "Work");
        lists["Family"].AddressBookId.Should().Be(book.Id);
        (await _contactService.GetListIdsForContactAsync(contact.Id)).Should().Equal(lists["Family"].Id);

        // A list Google has no group for yet is not the provider's to change.
        await _contactService.AddContactsToListAsync(pending.Id, [contact.Id]);

        // A response that was not asked for memberships keeps the stored ones.
        await _contactService.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch([Remote(null)], [], null), commitDeltaToken: false);
        (await _contactService.GetListIdsForContactAsync(contact.Id)).Should().BeEquivalentTo([lists["Family"].Id, pending.Id]);

        await _contactService.ReplaceRemoteListsAsync(_accountId, book.Id, [new ContactList { Name = "Colleagues", RemoteId = "contactGroups/2" }]);
        await _contactService.ApplyDeltaAsync(book.Id, new ContactSynchronizationBatch([Remote(["contactGroups/2"])], [], null), commitDeltaToken: false);

        var afterwards = (await _contactService.GetContactListsAsync()).ToDictionary(list => list.Name);
        afterwards.Keys.Should().BeEquivalentTo("Not at Google yet", "Colleagues");
        afterwards["Colleagues"].Id.Should().Be(lists["Work"].Id, "a renamed group stays the same list");
        (await _contactService.GetListIdsForContactAsync(contact.Id)).Should().BeEquivalentTo([lists["Work"].Id, pending.Id]);

        // A rename that does not know the provider id must not lose it.
        await _contactService.UpdateContactListAsync(new ContactList { Id = lists["Work"].Id, Name = "Team", MailAccountId = _accountId, AddressBookId = book.Id });
        (await _contactService.GetContactListAsync(lists["Work"].Id)).RemoteId.Should().Be("contactGroups/2");
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
    public async Task SuppressContactPictureAsync_ClearsTheReferenceAndDeletesOnlyAnUnsharedFile()
    {
        var pictureFileId = Guid.NewGuid();
        var pictureService = new Mock<IPictureStorageService>();
        var contactService = new ContactService(_databaseService, pictureService.Object);
        var first = await CreateLocalContactAsync("First");
        var second = await CreateLocalContactAsync("Second");
        await _contactService.SetContactPictureFileIdAsync(first.Id, pictureFileId);
        await _contactService.SetContactPictureFileIdAsync(second.Id, pictureFileId);

        await contactService.SuppressContactPictureAsync(first.Id, "outlook:hidden-photo:v1");

        (await contactService.GetContactAsync(first.Id)).ContactPictureFileId.Should().BeNull();
        (await contactService.GetContactAsync(first.Id)).RemotePhotoKey.Should().Be("outlook:hidden-photo:v1");
        (await contactService.GetContactAsync(second.Id)).ContactPictureFileId.Should().Be(pictureFileId);
        pictureService.Verify(service => service.DeletePictureAsync(PictureKind.Contact, pictureFileId), Times.Never);

        await contactService.SuppressContactPictureAsync(second.Id, "outlook:hidden-photo:v1");

        pictureService.Verify(service => service.DeletePictureAsync(PictureKind.Contact, pictureFileId), Times.Once);
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
