using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class JunkSenderServiceTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private JunkSenderService _service;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _service = new JunkSenderService(_database);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddSenderAsync_NormalizesAndDeduplicates()
    {
        await _service.AddSenderAsync(_accountId, "  Spam@Example.COM ", JunkListType.Blocked);
        await _service.AddSenderAsync(_accountId, "spam@example.com", JunkListType.Blocked);

        var blocked = await _service.GetSendersAsync(_accountId, JunkListType.Blocked);

        blocked.Should().ContainSingle().Which.Address.Should().Be("spam@example.com");
        (await _service.IsListedAsync(_accountId, "SPAM@example.com", JunkListType.Blocked)).Should().BeTrue();
    }

    [Fact]
    public async Task AddSenderAsync_MovesTheAddressOffTheOppositeList()
    {
        await _service.AddSenderAsync(_accountId, "friend@example.com", JunkListType.Blocked);
        await _service.AddSenderAsync(_accountId, "friend@example.com", JunkListType.Safe);

        (await _service.GetSendersAsync(_accountId, JunkListType.Blocked)).Should().BeEmpty();
        (await _service.GetSendersAsync(_accountId, JunkListType.Safe)).Should().ContainSingle();
    }

    [Fact]
    public async Task Lists_AreScopedPerAccount()
    {
        var otherAccount = Guid.NewGuid();
        await _service.AddSenderAsync(_accountId, "a@example.com", JunkListType.Safe);
        await _service.AddSenderAsync(otherAccount, "b@example.com", JunkListType.Safe);

        (await _service.GetSendersAsync(_accountId, JunkListType.Safe)).Should().ContainSingle().Which.Address.Should().Be("a@example.com");
        (await _service.GetSendersAsync(otherAccount, JunkListType.Safe)).Should().ContainSingle().Which.Address.Should().Be("b@example.com");
    }

    [Fact]
    public async Task RemoveSenderAsync_RemovesOnlyThatList()
    {
        await _service.AddSenderAsync(_accountId, "x@example.com", JunkListType.Blocked);

        await _service.RemoveSenderAsync(_accountId, "X@example.com", JunkListType.Safe);
        (await _service.IsListedAsync(_accountId, "x@example.com", JunkListType.Blocked)).Should().BeTrue();

        await _service.RemoveSenderAsync(_accountId, "X@example.com", JunkListType.Blocked);
        (await _service.IsListedAsync(_accountId, "x@example.com", JunkListType.Blocked)).Should().BeFalse();
    }

    [Fact]
    public async Task ImportAsync_CountsOnlyNewEntriesAndSkipsBlanks()
    {
        await _service.AddSenderAsync(_accountId, "old@example.com", JunkListType.Blocked);

        var added = await _service.ImportAsync(_accountId, JunkListType.Blocked, ["old@example.com", "new@example.com", " ", null, "NEW@example.com"]);

        added.Should().Be(1);
        (await _service.GetSendersAsync(_accountId, JunkListType.Blocked)).Select(s => s.Address)
            .Should().BeEquivalentTo("new@example.com", "old@example.com");
    }

    [Fact]
    public async Task BlankAddresses_AreIgnored()
    {
        await _service.AddSenderAsync(_accountId, "   ", JunkListType.Safe);
        await _service.AddSenderAsync(_accountId, null, JunkListType.Safe);

        (await _service.GetSendersAsync(_accountId, JunkListType.Safe)).Should().BeEmpty();
        (await _service.IsListedAsync(_accountId, "", JunkListType.Safe)).Should().BeFalse();
    }
}
