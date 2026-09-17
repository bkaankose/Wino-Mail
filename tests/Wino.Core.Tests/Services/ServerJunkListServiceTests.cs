using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ServerJunkListServiceTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid(), ProviderType = MailProviderType.Exchange };
    private readonly Mock<IWinoSynchronizerBase> _synchronizer = new();
    private readonly Mock<ISynchronizerFactory> _factory = new();
    private readonly ServerJunkListService _service;

    public ServerJunkListServiceTests()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ReturnsAsync(_synchronizer.Object);
        _service = new ServerJunkListService(_factory.Object);
    }

    [Fact]
    public async Task SupportsServerJunkListsAsync_ReflectsTheSynchronizerCapability()
    {
        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(true);
        (await _service.SupportsServerJunkListsAsync(_account)).Should().BeTrue();

        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(false);
        (await _service.SupportsServerJunkListsAsync(_account)).Should().BeFalse();

        (await _service.SupportsServerJunkListsAsync(null)).Should().BeFalse();
    }

    [Fact]
    public async Task SupportsServerJunkListsAsync_SwallowsFactoryFailures()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ThrowsAsync(new InvalidOperationException("no synchronizer"));

        (await _service.SupportsServerJunkListsAsync(_account)).Should().BeFalse();
    }

    [Fact]
    public async Task TryUpdateAsync_ForwardsTheEditWhenSupported()
    {
        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(true);

        var updated = await _service.TryUpdateAsync(_account.Id, "spam@example.com", JunkListType.Blocked, add: true);

        updated.Should().BeTrue();
        _synchronizer.Verify(s => s.UpdateServerJunkListAsync("spam@example.com", JunkListType.Blocked, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryUpdateAsync_ReturnsFalseWithoutAServerList()
    {
        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(false);

        (await _service.TryUpdateAsync(_account.Id, "spam@example.com", JunkListType.Blocked, add: true)).Should().BeFalse();
        _synchronizer.Verify(s => s.UpdateServerJunkListAsync(It.IsAny<string>(), It.IsAny<JunkListType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryUpdateAsync_ReportsAFailedWriteAsFalseNotAnException()
    {
        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(true);
        _synchronizer
            .Setup(s => s.UpdateServerJunkListAsync(It.IsAny<string>(), It.IsAny<JunkListType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no junk rule"));

        (await _service.TryUpdateAsync(_account.Id, "spam@example.com", JunkListType.Safe, add: false)).Should().BeFalse();
    }

    [Fact]
    public async Task TryUpdateAsync_IgnoresBlankAddresses()
    {
        _synchronizer.SetupGet(s => s.SupportsServerJunkLists).Returns(true);

        (await _service.TryUpdateAsync(_account.Id, " ", JunkListType.Safe, add: true)).Should().BeFalse();
        _factory.Verify(f => f.GetAccountSynchronizerAsync(It.IsAny<Guid>()), Times.Never);
    }
}
