using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Requests.Mail;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoRequestProcessorMoveTests
{
    [Fact]
    public async Task PrepareRequestsAsync_WithValidMoveTarget_CreatesMoveRequest()
    {
        var account = CreateAccount();
        var sourceFolder = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var targetFolder = CreateFolder(account.Id, SpecialFolderType.Archive);
        var folderService = new Mock<IFolderService>();
        folderService.Setup(service => service.GetFolderAsync(targetFolder.Id)).ReturnsAsync(targetFolder);
        var processor = CreateProcessor(folderService.Object);

        var requests = await processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(
                MailOperation.Move,
                CreateMail(account, sourceFolder),
                moveTargetFolder: targetFolder));

        requests.Should().ContainSingle();
        var request = requests[0].Should().BeOfType<MoveRequest>().Subject;
        request.FromFolder.Should().BeSameAs(sourceFolder);
        request.ToFolder.Should().BeSameAs(targetFolder);
    }

    [Fact]
    public async Task PrepareRequestsAsync_WithCurrentFolderAsTarget_RejectsMove()
    {
        var account = CreateAccount();
        var sourceFolder = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var processor = CreateProcessor(Mock.Of<IFolderService>());

        var action = () => processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(
                MailOperation.Move,
                CreateMail(account, sourceFolder),
                moveTargetFolder: sourceFolder));

        var exception = await action.Should().ThrowAsync<InvalidMoveTargetException>();
        exception.Which.Reason.Should().Be(InvalidMoveTargetReason.NonMoveTarget);
    }

    [Fact]
    public async Task PrepareRequestsAsync_WithAnotherAccountsFolder_RejectsMove()
    {
        var sourceAccount = CreateAccount();
        var targetAccount = CreateAccount();
        var sourceFolder = CreateFolder(sourceAccount.Id, SpecialFolderType.Inbox);
        var targetFolder = CreateFolder(targetAccount.Id, SpecialFolderType.Archive);
        var processor = CreateProcessor(Mock.Of<IFolderService>());

        var action = () => processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(
                MailOperation.Move,
                CreateMail(sourceAccount, sourceFolder),
                moveTargetFolder: targetFolder));

        var exception = await action.Should().ThrowAsync<InvalidMoveTargetException>();
        exception.Which.Reason.Should().Be(InvalidMoveTargetReason.NonMoveTarget);
    }

    [Fact]
    public async Task PrepareRequestsAsync_WhenResolvedFolderBelongsToAnotherAccount_RejectsMove()
    {
        var sourceAccount = CreateAccount();
        var targetAccount = CreateAccount();
        var sourceFolder = CreateFolder(sourceAccount.Id, SpecialFolderType.Inbox);
        var targetSnapshot = CreateFolder(sourceAccount.Id, SpecialFolderType.Archive);
        var resolvedTarget = CreateFolder(targetAccount.Id, SpecialFolderType.Archive);
        resolvedTarget.Id = targetSnapshot.Id;
        var folderService = new Mock<IFolderService>();
        folderService.Setup(service => service.GetFolderAsync(targetSnapshot.Id)).ReturnsAsync(resolvedTarget);
        var processor = CreateProcessor(folderService.Object);

        var action = () => processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(
                MailOperation.Move,
                CreateMail(sourceAccount, sourceFolder),
                moveTargetFolder: targetSnapshot));

        var exception = await action.Should().ThrowAsync<InvalidMoveTargetException>();
        exception.Which.Reason.Should().Be(InvalidMoveTargetReason.NonMoveTarget);
    }

    [Theory]
    [InlineData(SpecialFolderType.More)]
    [InlineData(SpecialFolderType.Category)]
    public async Task PrepareRequestsAsync_WithVirtualFolder_RejectsMove(SpecialFolderType folderType)
    {
        var account = CreateAccount();
        var sourceFolder = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var targetFolder = CreateFolder(account.Id, folderType);
        var processor = CreateProcessor(Mock.Of<IFolderService>());

        var action = () => processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(
                MailOperation.Move,
                CreateMail(account, sourceFolder),
                moveTargetFolder: targetFolder));

        var exception = await action.Should().ThrowAsync<InvalidMoveTargetException>();
        exception.Which.Reason.Should().Be(InvalidMoveTargetReason.NonMoveTarget);
    }

    private static WinoRequestProcessor CreateProcessor(IFolderService folderService)
        => new(
            folderService,
            Mock.Of<IKeyPressService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IMailService>());

    private static MailAccount CreateAccount()
        => new()
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.Outlook
        };

    private static MailItemFolder CreateFolder(Guid accountId, SpecialFolderType folderType)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            RemoteFolderId = Guid.NewGuid().ToString("N"),
            SpecialFolderType = folderType
        };

    private static MailCopy CreateMail(MailAccount account, MailItemFolder folder)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString("N"),
            AssignedAccount = account,
            AssignedFolder = folder
        };
}
