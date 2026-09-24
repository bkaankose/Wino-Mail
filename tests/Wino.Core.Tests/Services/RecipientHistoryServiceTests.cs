using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class RecipientHistoryServiceTests : IAsyncLifetime
{
    private static readonly DateTime Earlier = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 3, 5, 14, 30, 0, DateTimeKind.Utc);

    private InMemoryDatabaseService _databaseService = null!;
    private RecipientHistoryService _service = null!;
    private readonly Guid _accountId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _service = new RecipientHistoryService(_databaseService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task BundledSqlite_SupportsUpsert()
    {
        var version = await _databaseService.Connection.ExecuteScalarAsync<string>("SELECT sqlite_version()");
        new Version(version).Should().BeGreaterThanOrEqualTo(new Version(3, 24));
    }

    [Fact]
    public async Task ReceivingTwice_KeepsOneRowAndCountsBoth()
    {
        await _service.RecordReceivedAsync(_accountId, "alice@example.com", "Alice Example", Earlier);
        await _service.RecordReceivedAsync(_accountId, "ALICE@example.com", "Alice Example", Later);

        var row = (await GetRowsAsync()).Should().ContainSingle().Subject;
        row.ReceivedCount.Should().Be(2);
        row.SentCount.Should().Be(0);
        row.LastReceivedUtc.Should().Be(Later);
        row.LastSentUtc.Should().BeNull();
        row.NormalizedAddress.Should().Be("ALICE@EXAMPLE.COM");
    }

    [Fact]
    public async Task SentAndReceived_AccumulateOnTheSameRow()
    {
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("bob@example.com", "Bob")], Later);
        await _service.RecordReceivedAsync(_accountId, "bob@example.com", "Bob Builder", Earlier);

        var row = (await GetRowsAsync()).Should().ContainSingle().Subject;
        row.SentCount.Should().Be(1);
        row.ReceivedCount.Should().Be(1);
        row.LastSentUtc.Should().Be(Later);
        row.LastReceivedUtc.Should().Be(Earlier);
        row.LastInteractionUtc.Should().Be(Later);
        row.DisplayName.Should().Be("Bob Builder");
    }

    [Fact]
    public async Task OlderMail_DoesNotMoveTheLastInteractionBack()
    {
        await _service.RecordReceivedAsync(_accountId, "carol@example.com", "Carol", Later);
        await _service.RecordReceivedAsync(_accountId, "carol@example.com", "Carol", Earlier);

        (await GetRowsAsync()).Single().LastReceivedUtc.Should().Be(Later);
    }

    [Fact]
    public async Task AnAddressUsedAsAName_NeverReplacesARealName()
    {
        await _service.RecordReceivedAsync(_accountId, "dave@example.com", "Dave Example", Earlier);
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("dave@example.com", "dave@example.com")], Later);
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("dave@example.com", null)], Later);

        (await GetRowsAsync()).Single().DisplayName.Should().Be("Dave Example");
    }

    [Fact]
    public async Task AutomatedSender_IsNotRecorded_ButTheSameAddressAsASentRecipientIs()
    {
        await _service.RecordReceivedAsync(_accountId, "a23asd21asdju12398asdf9nfg9hwe@google.com", null, Earlier);
        (await GetRowsAsync()).Should().BeEmpty();

        // The user chose to write to it, so it is remembered.
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("a23asd21asdju12398asdf9nfg9hwe@google.com", null)], Later);
        (await GetRowsAsync()).Should().ContainSingle().Which.SentCount.Should().Be(1);
    }

    [Fact]
    public async Task SentList_IgnoresInvalidAndDuplicateAddresses()
    {
        await _service.RecordSentAsync(_accountId,
        [
            new RecipientAddress("erin@example.com", "Erin"),
            new RecipientAddress("ERIN@example.com", "Erin"),
            new RecipientAddress("not-an-address", null),
            new RecipientAddress("", null)
        ], Later);

        (await GetRowsAsync()).Should().ContainSingle().Which.SentCount.Should().Be(1);
    }

    [Fact]
    public async Task Suppression_SurvivesNewMailAndHidesTheAddressFromSearch()
    {
        await _service.RecordReceivedAsync(_accountId, "frank@example.com", "Frank", Earlier);
        await _service.SuppressAsync(_accountId, "Frank@Example.com");
        await _service.RecordReceivedAsync(_accountId, "frank@example.com", "Frank", Later);

        (await _service.SearchAsync(_accountId, "frank")).Should().BeEmpty();
        var row = (await GetRowsAsync()).Single();
        row.IsSuppressed.Should().BeTrue();
        row.ReceivedCount.Should().Be(2);
    }

    [Fact]
    public async Task SuppressingAnUnknownAddress_PreventsItFromBeingSuggestedLater()
    {
        await _service.SuppressAsync(_accountId, "grace@example.com");
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("grace@example.com", "Grace")], Later);

        (await _service.SearchAsync(_accountId, "grace")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_MatchesNameOrAddress_MostUsedFirst()
    {
        await _service.RecordReceivedAsync(_accountId, "heidi@example.com", "Heidi Klum", Earlier);
        await _service.RecordSentAsync(_accountId, [new RecipientAddress("h.klum@work.example", "Heidi at work")], Earlier);
        await _service.RecordReceivedAsync(_accountId, "ivan@example.com", "Ivan", Earlier);

        var results = await _service.SearchAsync(_accountId, "heidi");

        results.Select(row => row.Address).Should().Equal("h.klum@work.example", "heidi@example.com");
    }

    [Fact]
    public async Task Search_TreatsLikeWildcardsAsText()
    {
        await _service.RecordReceivedAsync(_accountId, "judy@example.com", "Judy", Earlier);

        (await _service.SearchAsync(_accountId, "%")).Should().BeEmpty();
        (await _service.SearchAsync(_accountId, "_udy")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_AndClear_AreScopedToTheAccount()
    {
        var otherAccountId = Guid.NewGuid();
        await _service.RecordReceivedAsync(_accountId, "ken@example.com", "Ken", Earlier);
        await _service.RecordReceivedAsync(otherAccountId, "ken@example.com", "Ken", Earlier);

        (await _service.SearchAsync(_accountId, "ken")).Should().ContainSingle().Which.AccountId.Should().Be(_accountId);

        await _service.ClearAsync(_accountId);

        (await _service.SearchAsync(_accountId, "ken")).Should().BeEmpty();
        (await _service.SearchAsync(otherAccountId, "ken")).Should().ContainSingle();
    }

    private Task<List<RecipientHistory>> GetRowsAsync()
        => _databaseService.Connection.Table<RecipientHistory>().Where(row => row.AccountId == _accountId).ToListAsync();
}
