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

public class PublicFolderServiceTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid(), ProviderType = MailProviderType.Exchange };
    private readonly Mock<IWinoSynchronizerBase> _synchronizer = new();
    private readonly Mock<ISynchronizerFactory> _factory = new();
    private readonly PublicFolderService _service;

    public PublicFolderServiceTests()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ReturnsAsync(_synchronizer.Object);
        _service = new PublicFolderService(_factory.Object);
    }

    [Fact]
    public void SupportsPublicFolders_IsExchangeOnly()
    {
        _service.SupportsPublicFolders(_account).Should().BeTrue();
        _service.SupportsPublicFolders(new MailAccount { ProviderType = MailProviderType.Outlook }).Should().BeFalse();
        _service.SupportsPublicFolders(null).Should().BeFalse();
    }

    [Fact]
    public async Task RootChildren_AskTheSynchronizerWithoutAParent()
    {
        var node = new PublicFolderNode { Id = "f1", Name = "Sales", Kind = PublicFolderKind.Mail };
        _synchronizer.SetupGet(s => s.SupportsPublicFolders).Returns(true);
        _synchronizer.Setup(s => s.GetPublicFolderChildrenAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PublicFolderNode> { node });

        var children = await _service.GetRootChildrenAsync(_account.Id);

        children.Should().ContainSingle().Which.Should().BeSameAs(node);
    }

    [Fact]
    public async Task Children_AndContent_ForwardTheirIds()
    {
        var copy = new MailCopy { Id = "m1" };
        var contact = new PublicFolderContact { RemoteId = "c1" };
        _synchronizer.SetupGet(s => s.SupportsPublicFolders).Returns(true);
        _synchronizer.Setup(s => s.GetPublicFolderChildrenAsync("f1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<PublicFolderNode>());
        _synchronizer.Setup(s => s.GetPublicFolderMailItemsAsync("f1", 10, 25, It.IsAny<CancellationToken>())).ReturnsAsync(new List<MailCopy> { copy });
        _synchronizer.Setup(s => s.GetPublicFolderMailMimeAsync("f1", "m1", It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 1, 2 });
        _synchronizer.Setup(s => s.GetPublicFolderContactsAsync("f2", It.IsAny<CancellationToken>())).ReturnsAsync(new List<PublicFolderContact> { contact });

        (await _service.GetChildrenAsync(_account.Id, "f1")).Should().BeEmpty();
        (await _service.GetMailItemsAsync(_account.Id, "f1", 10, 25)).Should().ContainSingle().Which.Should().BeSameAs(copy);
        (await _service.GetMailMimeAsync(_account.Id, "f1", "m1")).Should().Equal(1, 2);
        (await _service.GetContactsAsync(_account.Id, "f2")).Should().ContainSingle().Which.Should().BeSameAs(contact);
    }

    [Fact]
    public async Task Calls_ThrowWhenTheSynchronizerHasNoPublicFolders()
    {
        _synchronizer.SetupGet(s => s.SupportsPublicFolders).Returns(false);

        var act = () => _service.GetRootChildrenAsync(_account.Id);

        await act.Should().ThrowAsync<NotSupportedException>();
        _synchronizer.Verify(s => s.GetPublicFolderChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Calls_ThrowWhenNoSynchronizerExists()
    {
        _factory.Setup(f => f.GetAccountSynchronizerAsync(_account.Id)).ReturnsAsync((IWinoSynchronizerBase)null);

        var act = () => _service.GetMailItemsAsync(_account.Id, "f1", 0, 0);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
