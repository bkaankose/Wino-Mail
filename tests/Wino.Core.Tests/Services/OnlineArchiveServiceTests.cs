using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class OnlineArchiveServiceTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid(), ProviderType = MailProviderType.Exchange };
    private readonly Mock<IWinoSynchronizerBase> _synchronizer = new();
    private readonly Mock<ISynchronizerFactory> _factory = new();
    private readonly OnlineArchiveService _service;

    public OnlineArchiveServiceTests()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ReturnsAsync(_synchronizer.Object);
        _service = new OnlineArchiveService(_factory.Object);
    }

    [Fact]
    public void SupportsOnlineArchive_IsExchangeOnly()
    {
        _service.SupportsOnlineArchive(_account).Should().BeTrue();
        _service.SupportsOnlineArchive(new MailAccount { ProviderType = MailProviderType.Gmail }).Should().BeFalse();
        _service.SupportsOnlineArchive(null).Should().BeFalse();
    }

    [Fact]
    public async Task RootFolders_PassThroughTheNotProvisionedSentinel()
    {
        _synchronizer.SetupGet(s => s.SupportsOnlineArchive).Returns(true);
        _synchronizer.Setup(s => s.GetOnlineArchiveChildrenAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PublicFolderNode>)null);

        (await _service.GetRootFoldersAsync(_account.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Children_AndContent_ForwardTheirIds()
    {
        var copy = new MailCopy { Id = "m1" };
        _synchronizer.SetupGet(s => s.SupportsOnlineArchive).Returns(true);
        _synchronizer.Setup(s => s.GetOnlineArchiveChildrenAsync("f1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<PublicFolderNode>());
        _synchronizer.Setup(s => s.GetOnlineArchiveMailItemsAsync("f1", 0, 0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<MailCopy> { copy });
        _synchronizer.Setup(s => s.GetOnlineArchiveMailMimeAsync("f1", "m1", It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 7 });

        (await _service.GetChildrenAsync(_account.Id, "f1")).Should().BeEmpty();
        (await _service.GetMailItemsAsync(_account.Id, "f1", 0, 0)).Should().ContainSingle().Which.Should().BeSameAs(copy);
        (await _service.GetMailMimeAsync(_account.Id, "f1", "m1")).Should().Equal(7);
    }

    [Fact]
    public async Task Calls_ThrowWhenTheSynchronizerHasNoArchive()
    {
        _synchronizer.SetupGet(s => s.SupportsOnlineArchive).Returns(false);

        var act = () => _service.GetRootFoldersAsync(_account.Id);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
