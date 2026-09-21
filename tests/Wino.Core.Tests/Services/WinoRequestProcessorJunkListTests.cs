using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Requests.Mail;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>Block sender / Never block sender: the local list bookkeeping plus the per-provider request.</summary>
public sealed class WinoRequestProcessorJunkListTests
{
    private readonly Mock<IJunkSenderService> _junkSenders = new();

    [Theory]
    [InlineData(MailProviderType.Exchange)]
    [InlineData(MailProviderType.Outlook)]
    [InlineData(MailProviderType.Gmail)]
    public async Task BlockSender_RecordsTheSenderAndChangesJunkStateForServerProviders(MailProviderType providerType)
    {
        var account = CreateAccount(providerType);
        var inbox = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var junk = CreateFolder(account.Id, SpecialFolderType.Junk);
        var processor = CreateProcessor(FolderServiceWith(account.Id, junk));

        var requests = await processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(MailOperation.BlockSender, CreateMail(account, inbox, "spam@example.com")));

        _junkSenders.Verify(s => s.AddSenderAsync(account.Id, "spam@example.com", JunkListType.Blocked), Times.Once);
        var request = requests.Should().ContainSingle().Which.Should().BeOfType<ChangeJunkStateRequest>().Subject;
        request.IsJunk.Should().BeTrue();
        request.TargetFolder.Should().BeSameAs(junk);
    }

    [Theory]
    [InlineData(MailProviderType.IMAP4)]
    [InlineData(MailProviderType.POP3)]
    public async Task BlockSender_JustMovesForProvidersWithoutAJunkApi(MailProviderType providerType)
    {
        var account = CreateAccount(providerType);
        var inbox = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var junk = CreateFolder(account.Id, SpecialFolderType.Junk);
        var processor = CreateProcessor(FolderServiceWith(account.Id, junk));

        var requests = await processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(MailOperation.BlockSender, CreateMail(account, inbox, "spam@example.com")));

        _junkSenders.Verify(s => s.AddSenderAsync(account.Id, "spam@example.com", JunkListType.Blocked), Times.Once);
        var request = requests.Should().ContainSingle().Which.Should().BeOfType<MoveRequest>().Subject;
        request.ToFolder.Should().BeSameAs(junk);
    }

    [Fact]
    public async Task NeverBlockSender_FromJunk_MarksSafeAndMovesBackToInbox()
    {
        var account = CreateAccount(MailProviderType.Exchange);
        var inbox = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var junk = CreateFolder(account.Id, SpecialFolderType.Junk);
        var processor = CreateProcessor(FolderServiceWith(account.Id, inbox));

        var requests = await processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(MailOperation.NeverBlockSender, CreateMail(account, junk, "friend@example.com")));

        _junkSenders.Verify(s => s.AddSenderAsync(account.Id, "friend@example.com", JunkListType.Safe), Times.Once);
        var request = requests.Should().ContainSingle().Which.Should().BeOfType<ChangeJunkStateRequest>().Subject;
        request.IsJunk.Should().BeFalse();
        request.TargetFolder.Should().BeSameAs(inbox);
    }

    [Fact]
    public async Task NeverBlockSender_OutsideJunk_OnlyRecordsTheSender()
    {
        var account = CreateAccount(MailProviderType.Exchange);
        var inbox = CreateFolder(account.Id, SpecialFolderType.Inbox);
        var processor = CreateProcessor(FolderServiceWith(account.Id, inbox));

        var requests = await processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(MailOperation.NeverBlockSender, CreateMail(account, inbox, "friend@example.com")));

        _junkSenders.Verify(s => s.AddSenderAsync(account.Id, "friend@example.com", JunkListType.Safe), Times.Once);
        requests.Should().BeEmpty();
    }

    private WinoRequestProcessor CreateProcessor(IFolderService folderService)
        => new(
            folderService,
            Mock.Of<IKeyPressService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IMailService>(),
            _junkSenders.Object);

    private static IFolderService FolderServiceWith(Guid accountId, MailItemFolder specialFolder)
    {
        var folderService = new Mock<IFolderService>();
        folderService
            .Setup(service => service.GetSpecialFolderByAccountIdAsync(accountId, specialFolder.SpecialFolderType))
            .ReturnsAsync(specialFolder);
        return folderService.Object;
    }

    private static MailAccount CreateAccount(MailProviderType providerType)
        => new() { Id = Guid.NewGuid(), ProviderType = providerType };

    private static MailItemFolder CreateFolder(Guid accountId, SpecialFolderType specialFolderType)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            SpecialFolderType = specialFolderType,
            FolderName = specialFolderType.ToString()
        };

    private static MailCopy CreateMail(MailAccount account, MailItemFolder folder, string fromAddress)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString("N"),
            FolderId = folder.Id,
            FromAddress = fromAddress,
            AssignedAccount = account,
            AssignedFolder = folder
        };
}
