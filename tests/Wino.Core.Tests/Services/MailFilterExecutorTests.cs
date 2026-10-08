using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MailFilterExecutorTests
{
    [Fact]
    public async Task ShouldSuppressNewMessageAsync_DoesNotUseProviderCreationDate()
    {
        var accountId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var sourceRemoteFolderId = "inbox";
        var filter = new MailFilter
        {
            Id = filterId,
            CreatedAtUtc = DateTime.UtcNow,
            Conditions =
            [
                new()
                {
                    Field = MailFilterConditionField.Subject,
                    Operator = MailFilterConditionOperator.Contains,
                    Value = "invoice"
                }
            ],
            Actions =
            [
                new() { Type = MailFilterActionType.MarkRead }
            ]
        };
        var message = new MailCopy
        {
            Id = "message-1",
            Subject = "An old-dated invoice from a new delta",
            CreationDate = DateTime.UtcNow.AddYears(-1)
        };
        var filterService = new Mock<IMailFilterService>();
        filterService
            .Setup(service => service.GetExecutableFiltersAsync(
                accountId,
                sourceRemoteFolderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([filter]);
        filterService
            .Setup(service => service.HasExecutionAsync(
                filterId,
                message.Id,
                sourceRemoteFolderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var folderService = new Mock<IFolderService>();
        folderService
            .Setup(service => service.GetFoldersAsync(accountId))
            .ReturnsAsync([]);
        var executor = CreateExecutor(filterService.Object, folderService.Object);

        var shouldSuppress = await executor.ShouldSuppressNewMessageAsync(
            accountId,
            sourceRemoteFolderId,
            message);

        Assert.True(shouldSuppress);
    }

    [Fact]
    public async Task ShouldSuppressNewMessageAsync_DoesNotHideMessageWhenTargetFolderIsMissing()
    {
        var accountId = Guid.NewGuid();
        var sourceRemoteFolderId = "inbox";
        var filter = new MailFilter
        {
            Id = Guid.NewGuid(),
            Conditions = [],
            Actions =
            [
                new()
                {
                    Type = MailFilterActionType.Move,
                    TargetRemoteFolderId = "missing-folder"
                }
            ]
        };
        var message = new MailCopy { Id = "message-1" };
        var filterService = new Mock<IMailFilterService>();
        filterService
            .Setup(service => service.GetExecutableFiltersAsync(
                accountId,
                sourceRemoteFolderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([filter]);
        filterService
            .Setup(service => service.HasExecutionAsync(
                filter.Id,
                message.Id,
                sourceRemoteFolderId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var folderService = new Mock<IFolderService>();
        folderService
            .Setup(service => service.GetFoldersAsync(accountId))
            .ReturnsAsync([]);
        var executor = CreateExecutor(filterService.Object, folderService.Object);

        var shouldSuppress = await executor.ShouldSuppressNewMessageAsync(
            accountId,
            sourceRemoteFolderId,
            message);

        Assert.False(shouldSuppress);
    }

    [Fact]
    public void Matches_AllMode_RequiresEveryCondition()
    {
        var filter = new MailFilter
        {
            MatchMode = MailFilterMatchMode.All,
            Conditions =
            [
                new()
                {
                    Field = MailFilterConditionField.FromAddress,
                    Operator = MailFilterConditionOperator.EndsWith,
                    Value = "@example.com"
                },
                new()
                {
                    Field = MailFilterConditionField.Subject,
                    Operator = MailFilterConditionOperator.Contains,
                    Value = "invoice"
                }
            ]
        };
        var message = new MailCopy
        {
            FromAddress = "billing@example.com",
            Subject = "Your INVOICE is ready"
        };

        Assert.True(MailFilterExecutor.Matches(filter, message));

        message.Subject = "Welcome";

        Assert.False(MailFilterExecutor.Matches(filter, message));
    }

    [Fact]
    public void Matches_AnyMode_UsesCaseInsensitiveNegativeOperators()
    {
        var filter = new MailFilter
        {
            MatchMode = MailFilterMatchMode.Any,
            Conditions =
            [
                new()
                {
                    Field = MailFilterConditionField.FromName,
                    Operator = MailFilterConditionOperator.NotEquals,
                    Value = "Contoso"
                },
                new()
                {
                    Field = MailFilterConditionField.PreviewText,
                    Operator = MailFilterConditionOperator.NotContains,
                    Value = "unsubscribe"
                }
            ]
        };
        var message = new MailCopy
        {
            FromName = "CONTOSO",
            PreviewText = "Use the unsubscribe link"
        };

        Assert.False(MailFilterExecutor.Matches(filter, message));

        message.PreviewText = "A personal update";

        Assert.True(MailFilterExecutor.Matches(filter, message));
    }

    [Fact]
    public void Matches_HandlesBooleanAndImportanceOperands()
    {
        var filter = new MailFilter
        {
            MatchMode = MailFilterMatchMode.All,
            Conditions =
            [
                new()
                {
                    Field = MailFilterConditionField.HasAttachments,
                    Operator = MailFilterConditionOperator.Equals,
                    Value = "true"
                },
                new()
                {
                    Field = MailFilterConditionField.Importance,
                    Operator = MailFilterConditionOperator.Equals,
                    Value = nameof(MailImportance.High)
                }
            ]
        };
        var message = new MailCopy
        {
            HasAttachments = true,
            Importance = MailImportance.High
        };

        Assert.True(MailFilterExecutor.Matches(filter, message));
    }

    private static MailFilterExecutor CreateExecutor(
        IMailFilterService filterService,
        IFolderService folderService)
        => new(
            filterService,
            Mock.Of<IMailService>(),
            folderService,
            Mock.Of<IWinoRequestProcessor>(),
            Mock.Of<IWinoRequestDelegator>());
}
