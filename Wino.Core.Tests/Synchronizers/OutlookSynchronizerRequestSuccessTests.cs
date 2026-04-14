using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class OutlookSynchronizerRequestSuccessTests
{
    [Fact]
    public async Task HandleSuccessfulResponseAsync_MarkReadRequest_PersistsLocalReadStateEvenWithoutResponseBody()
    {
        var changeProcessor = new Mock<IOutlookChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.ChangeMailReadStatusAsync("mail-id", true))
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var request = new MarkReadRequest(CreateMailCopy(), IsRead: true);
        var bundle = new HttpRequestBundle<RequestInformation>(new RequestInformation(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };

        await InvokeHandleSuccessfulResponseAsync(synchronizer, bundle, response);

        changeProcessor.Verify(x => x.ChangeMailReadStatusAsync("mail-id", true), Times.Once);
    }

    [Fact]
    public async Task HandleSuccessfulResponseAsync_ChangeFlagRequest_PersistsLocalFlagStateEvenWithoutResponseBody()
    {
        var changeProcessor = new Mock<IOutlookChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.ChangeFlagStatusAsync("mail-id", true))
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var request = new ChangeFlagRequest(CreateMailCopy(), IsFlagged: true);
        var bundle = new HttpRequestBundle<RequestInformation>(new RequestInformation(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };

        await InvokeHandleSuccessfulResponseAsync(synchronizer, bundle, response);

        changeProcessor.Verify(x => x.ChangeFlagStatusAsync("mail-id", true), Times.Once);
    }

    private static OutlookSynchronizer CreateSynchronizer(IOutlookChangeProcessor changeProcessor)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook",
            Address = "user@example.com"
        };

        var authenticator = new Mock<IAuthenticator>(MockBehavior.Loose);
        var errorFactory = new Mock<IOutlookSynchronizerErrorHandlerFactory>(MockBehavior.Loose);
        var mailCategoryService = new Mock<IMailCategoryService>(MockBehavior.Loose);

        return new OutlookSynchronizer(account, authenticator.Object, changeProcessor, errorFactory.Object, mailCategoryService.Object);
    }

    private static MailCopy CreateMailCopy() =>
        new()
        {
            UniqueId = Guid.NewGuid(),
            Id = "mail-id",
            FolderId = Guid.NewGuid(),
            IsRead = false,
            IsFlagged = false
        };

    private static async Task InvokeHandleSuccessfulResponseAsync(
        OutlookSynchronizer synchronizer,
        HttpRequestBundle<RequestInformation> bundle,
        HttpResponseMessage response)
    {
        var method = typeof(OutlookSynchronizer).GetMethod(
            "HandleSuccessfulResponseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = method!.Invoke(synchronizer, [bundle, response]) as Task;
        task.Should().NotBeNull();
        await task!;
    }
}
