using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class GlobalAddressListServiceTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid(), ProviderType = MailProviderType.Exchange };
    private readonly Mock<IWinoSynchronizerBase> _synchronizer = new();
    private readonly Mock<ISynchronizerFactory> _factory = new();
    private readonly GlobalAddressListService _service;

    public GlobalAddressListServiceTests()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ReturnsAsync(_synchronizer.Object);
        _service = new GlobalAddressListService(_factory.Object);
    }

    [Fact]
    public void SupportsGlobalAddressList_IsExchangeOnly()
    {
        _service.SupportsGlobalAddressList(_account).Should().BeTrue();
        _service.SupportsGlobalAddressList(new MailAccount { ProviderType = MailProviderType.Gmail }).Should().BeFalse();
        _service.SupportsGlobalAddressList(new MailAccount { ProviderType = MailProviderType.IMAP4 }).Should().BeFalse();
        _service.SupportsGlobalAddressList(null).Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_ForwardsToTheSynchronizerWhenSupported()
    {
        var hit = new AccountContact { Address = "matt@example.com", Name = "Matt" };
        _synchronizer.SetupGet(s => s.SupportsGlobalAddressList).Returns(true);
        _synchronizer.Setup(s => s.SearchGlobalAddressListAsync("mat", 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AccountContact> { hit });

        var results = await _service.SearchAsync(_account.Id, "mat", 15);

        results.Should().ContainSingle().Which.Should().BeSameAs(hit);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWithoutADirectory()
    {
        _synchronizer.SetupGet(s => s.SupportsGlobalAddressList).Returns(false);

        (await _service.SearchAsync(_account.Id, "mat", 15)).Should().BeEmpty();
        _synchronizer.Verify(s => s.SearchGlobalAddressListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_ShortCircuitsBlankQueriesAndZeroLimits()
    {
        (await _service.SearchAsync(_account.Id, "  ", 15)).Should().BeEmpty();
        (await _service.SearchAsync(_account.Id, "mat", 0)).Should().BeEmpty();
        _factory.Verify(f => f.GetAccountSynchronizerAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SearchAsync_SwallowsDirectoryAndFactoryFailures()
    {
        _synchronizer.SetupGet(s => s.SupportsGlobalAddressList).Returns(true);
        _synchronizer.Setup(s => s.SearchGlobalAddressListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no address book endpoint"));
        (await _service.SearchAsync(_account.Id, "mat", 15)).Should().BeEmpty();

        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ThrowsAsync(new InvalidOperationException("no synchronizer"));
        (await _service.SearchAsync(_account.Id, "mat", 15)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_PropagatesCancellation()
    {
        _synchronizer.SetupGet(s => s.SupportsGlobalAddressList).Returns(true);
        _synchronizer.Setup(s => s.SearchGlobalAddressListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _service.SearchAsync(_account.Id, "mat", 15, new CancellationToken(true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
