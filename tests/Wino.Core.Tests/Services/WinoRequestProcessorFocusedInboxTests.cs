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

public sealed class WinoRequestProcessorFocusedInboxTests
{
    [Theory]
    [InlineData(MailOperation.MoveToFocused, true, false)]
    [InlineData(MailOperation.MoveToOther, false, false)]
    [InlineData(MailOperation.AlwaysMoveToFocused, true, true)]
    [InlineData(MailOperation.AlwaysMoveToOther, false, true)]
    public async Task PrepareRequestsAsync_ForOutlookFocusedInboxAction_CreatesExpectedRequest(
        MailOperation operation,
        bool moveToFocused,
        bool isAlways)
    {
        var processor = CreateProcessor();
        var mail = CreateMail(MailProviderType.Outlook);

        var requests = await processor.PrepareRequestsAsync(new MailOperationPreperationRequest(operation, mail));

        requests.Should().ContainSingle();
        if (isAlways)
        {
            requests[0].Should().BeOfType<AlwaysMoveToRequest>()
                .Which.MoveToFocused.Should().Be(moveToFocused);
        }
        else
        {
            requests[0].Should().BeOfType<MoveToFocusedRequest>()
                .Which.MoveToFocused.Should().Be(moveToFocused);
        }
    }

    [Fact]
    public async Task PrepareRequestsAsync_ForNonOutlookFocusedInboxAction_ThrowsNotSupportedException()
    {
        var processor = CreateProcessor();
        var mail = CreateMail(MailProviderType.Gmail);

        var action = () => processor.PrepareRequestsAsync(
            new MailOperationPreperationRequest(MailOperation.MoveToFocused, mail));

        await action.Should().ThrowAsync<NotSupportedException>();
    }

    private static WinoRequestProcessor CreateProcessor()
        => new(
            Mock.Of<IFolderService>(),
            Mock.Of<IKeyPressService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IMailService>());

    private static MailCopy CreateMail(MailProviderType providerType)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            Id = "message-id",
            AssignedAccount = new MailAccount
            {
                Id = Guid.NewGuid(),
                ProviderType = providerType
            },
            AssignedFolder = new MailItemFolder
            {
                Id = Guid.NewGuid(),
                SpecialFolderType = SpecialFolderType.Inbox
            }
        };
}
