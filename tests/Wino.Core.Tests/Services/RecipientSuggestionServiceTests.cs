using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class RecipientSuggestionServiceTests : IAsyncLifetime
{
    private static readonly DateTime Recently = DateTime.UtcNow.AddDays(-2);

    private InMemoryDatabaseService _databaseService = null!;
    private ContactService _contactService = null!;
    private RecipientHistoryService _historyService = null!;
    private RecipientSuggestionService _service = null!;
    private readonly Guid _accountId = Guid.NewGuid();
    private Guid _addressBookId;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _contactService = new ContactService(_databaseService);
        _historyService = new RecipientHistoryService(_databaseService);
        _service = new RecipientSuggestionService(_contactService, _historyService);

        await _databaseService.Connection.InsertAsync(
            new MailAccount { Id = _accountId, Name = "Work", ProviderType = MailProviderType.IMAP4 },
            typeof(MailAccount));
        _addressBookId = (await _contactService.EnsureLocalAddressBookAsync(_accountId, "Work contacts")).Id;
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task ShortQueries_ReturnNothing()
    {
        await CreateContactAsync("Alice Example", "alice@example.com");

        (await _service.SuggestAsync(_accountId, "a")).Should().BeEmpty();
        (await _service.SuggestAsync(_accountId, " ")).Should().BeEmpty();
    }

    [Fact]
    public async Task SavedContact_WithoutHistory_IsSuggested()
    {
        await CreateContactAsync("Alice Example", "alice@example.com");

        var suggestion = (await _service.SuggestAsync(_accountId, "ali")).Should().ContainSingle().Subject;

        suggestion.Source.Should().Be(RecipientSuggestionSource.Contact);
        suggestion.Address.Should().Be("alice@example.com");
        suggestion.DisplayName.Should().Be("Alice Example");
        suggestion.SourceLabel.Should().Be("Work contacts");
        suggestion.CanSuppress.Should().BeFalse();
    }

    [Fact]
    public async Task ContactAndHistory_ForTheSameAddress_MergeIntoTheContact()
    {
        var card = await CreateContactAsync("Bob Builder", "bob@example.com");
        await _historyService.RecordReceivedAsync(_accountId, "BOB@example.com", "bob", Recently);

        var suggestion = (await _service.SuggestAsync(_accountId, "bob")).Should().ContainSingle().Subject;

        suggestion.Source.Should().Be(RecipientSuggestionSource.Contact);
        suggestion.ContactCardId.Should().Be(card.Id);
        suggestion.DisplayName.Should().Be("Bob Builder");
    }

    [Fact]
    public async Task HistoryOnlyPeople_AreSuggestedAndCanBeHidden()
    {
        await _historyService.RecordReceivedAsync(_accountId, "carol@example.com", "Carol Danvers", Recently);

        var suggestion = (await _service.SuggestAsync(_accountId, "carol")).Should().ContainSingle().Subject;

        suggestion.Source.Should().Be(RecipientSuggestionSource.History);
        suggestion.SourceLabel.Should().NotBeNullOrWhiteSpace();
        suggestion.CanSuppress.Should().BeTrue();
        suggestion.Address.Should().Be("carol@example.com");
    }

    [Fact]
    public async Task PeopleWrittenToOften_RankAboveEqualMatches()
    {
        await CreateContactAsync("Dana Rarely", "dana.r@example.com");
        await _historyService.RecordReceivedAsync(_accountId, "dana.o@example.com", "Dana Often", Recently);
        for (var i = 0; i < 5; i++)
            await _historyService.RecordSentAsync(_accountId, [new RecipientAddress("dana.o@example.com", "Dana Often")], Recently);

        var suggestions = await _service.SuggestAsync(_accountId, "dana");

        suggestions.Select(suggestion => suggestion.Address).Should().Equal("dana.o@example.com", "dana.r@example.com");
    }

    [Fact]
    public async Task ContactMatchedByASecondaryAddress_OffersThatAddress()
    {
        var card = new AccountContact
        {
            MailAccountId = _accountId,
            AddressBookId = _addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = "Erin Home",
            EmailAddresses =
            [
                new ContactEmailAddress { Address = "erin@home.example", IsPrimary = true },
                new ContactEmailAddress { Address = "erin.w@work.example", Order = 1 }
            ]
        };
        await _contactService.StageCreateAsync(card);

        var suggestion = (await _service.SuggestAsync(_accountId, "work.ex")).Should().ContainSingle().Subject;

        suggestion.Address.Should().Be("erin.w@work.example");
    }

    [Fact]
    public async Task ContactLists_ComeFirst_AndCarryTheirMembers()
    {
        var member = await CreateContactAsync("Frank Team", "frank@example.com");
        var list = await _contactService.CreateContactListAsync("Frontend crew");
        await _contactService.AddContactsToListAsync(list.Id, [member.Id]);

        var suggestions = await _service.SuggestAsync(_accountId, "fr");

        suggestions.First().IsList.Should().BeTrue();
        suggestions.First().ListMembers.Should().ContainSingle().Which.PrimaryEmailAddress.Should().Be("frank@example.com");
        (await _service.SuggestAsync(_accountId, "fr", includeLists: false)).Should().NotContain(suggestion => suggestion.IsList);
    }

    [Fact]
    public async Task WithoutAComposingAccount_OnlyContactsAreSuggested()
    {
        await CreateContactAsync("Gina Contact", "gina@example.com");
        await _historyService.RecordReceivedAsync(_accountId, "gina.h@example.com", "Gina History", Recently);

        var suggestions = await _service.SuggestAsync(null, "gina");

        suggestions.Should().ContainSingle().Which.Address.Should().Be("gina@example.com");
    }

    [Fact]
    public async Task SuppressedHistory_IsNotSuggested()
    {
        await _historyService.RecordReceivedAsync(_accountId, "hank@example.com", "Hank Pym", Recently);
        await _historyService.SuppressAsync(_accountId, "hank@example.com");

        (await _service.SuggestAsync(_accountId, "hank")).Should().BeEmpty();
    }

    private Task<AccountContact> CreateContactAsync(string name, string address)
        => _contactService.StageCreateAsync(new AccountContact
        {
            MailAccountId = _accountId,
            AddressBookId = _addressBookId,
            SourceKind = ContactSourceKind.Local,
            DisplayName = name,
            Address = address
        });
}
