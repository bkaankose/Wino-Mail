using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Google.Apis.Requests;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Mail;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class GmailSynchronizerRequestSuccessTests
{
    [Fact]
    public void BuildGmailSearchQuery_FormatsCutoffDateWithInvariantSlashSeparator()
    {
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            var query = GmailSynchronizer.BuildGmailSearchQuery(null, new DateTime(2026, 5, 15, 12, 30, 0, DateTimeKind.Utc));

            query.Should().Be("after:2026/05/15");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void BuildGmailSearchQuery_AppendsCutoffDateToExistingQuery()
    {
        var query = GmailSynchronizer.BuildGmailSearchQuery("in:archive", new DateTime(2026, 5, 15, 12, 30, 0, DateTimeKind.Utc));

        query.Should().Be("in:archive after:2026/05/15");
    }

    [Fact]
    public async Task UpdateAccountSyncIdentifierAsync_EmptyStoredIdentifier_PersistsFirstHistoryCursor()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(It.IsAny<Guid>(), "123"))
            .ReturnsAsync("123");

        var synchronizer = CreateSynchronizer(changeProcessor.Object, synchronizationDeltaIdentifier: string.Empty);

        await InvokeUpdateAccountSyncIdentifierAsync(synchronizer, 123);

        changeProcessor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(It.IsAny<Guid>(), "123"), Times.Once);
    }

    [Fact]
    public async Task UpdateAccountSyncIdentifierAsync_OlderHistoryCursor_DoesNotRegressStoredCursor()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        var synchronizer = CreateSynchronizer(changeProcessor.Object, synchronizationDeltaIdentifier: "456");

        await InvokeUpdateAccountSyncIdentifierAsync(synchronizer, 123);

        changeProcessor.Verify(x => x.UpdateAccountDeltaSynchronizationIdentifierAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_BatchMarkReadRequest_PersistsLocalReadStateForEachMail()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        List<MailCopyStateUpdate>? capturedUpdates = null;

        changeProcessor
            .Setup(x => x.ApplyMailStateUpdatesAsync(It.IsAny<IEnumerable<MailCopyStateUpdate>>()))
            .Callback<IEnumerable<MailCopyStateUpdate>>(updates => capturedUpdates = updates.ToList())
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var request = new BatchMarkReadRequest(
        [
            new MarkReadRequest(CreateMailCopy("mail-1"), IsRead: true),
            new MarkReadRequest(CreateMailCopy("mail-2"), IsRead: true)
        ]);
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };

        await InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response);

        capturedUpdates.Should().BeEquivalentTo(
        [
            new MailCopyStateUpdate("mail-1", IsRead: true),
            new MailCopyStateUpdate("mail-2", IsRead: true)
        ]);
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_BatchChangeFlagRequest_PersistsLocalFlagStateForEachMail()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        List<MailCopyStateUpdate>? capturedUpdates = null;

        changeProcessor
            .Setup(x => x.ApplyMailStateUpdatesAsync(It.IsAny<IEnumerable<MailCopyStateUpdate>>()))
            .Callback<IEnumerable<MailCopyStateUpdate>>(updates => capturedUpdates = updates.ToList())
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var request = new BatchChangeFlagRequest(
        [
            new ChangeFlagRequest(CreateMailCopy("mail-1"), IsFlagged: true),
            new ChangeFlagRequest(CreateMailCopy("mail-2"), IsFlagged: true)
        ]);
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };

        await InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response);

        capturedUpdates.Should().BeEquivalentTo(
        [
            new MailCopyStateUpdate("mail-1", IsFlagged: true),
            new MailCopyStateUpdate("mail-2", IsFlagged: true)
        ]);
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_HandledRequestError_DoesNotPersistLocalReadState()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        var errorFactory = new Mock<IGmailSynchronizerErrorHandlerFactory>(MockBehavior.Strict);
        errorFactory
            .Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .ReturnsAsync(true);

        var synchronizer = CreateSynchronizer(changeProcessor.Object, errorFactory.Object);
        var request = new BatchMarkReadRequest(
        [
            new MarkReadRequest(CreateMailCopy("mail-1"), IsRead: true)
        ]);
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new RequestError
        {
            Code = 429,
            Message = "rate limit"
        };

        await InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response, error);

        changeProcessor.Verify(x => x.ChangeMailReadStatusAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        errorFactory.Verify(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_HandledRequestError_RevertsOptimisticReadState()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        var errorFactory = new Mock<IGmailSynchronizerErrorHandlerFactory>(MockBehavior.Strict);
        errorFactory
            .Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .ReturnsAsync(true);

        var mail = CreateMailCopy("mail-1");
        var request = new BatchMarkReadRequest(
        [
            new MarkReadRequest(mail, IsRead: true)
        ]);
        request.ApplyUIChanges();

        var synchronizer = CreateSynchronizer(changeProcessor.Object, errorFactory.Object);
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new RequestError
        {
            Code = 429,
            Message = "rate limit"
        };

        await InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response, error);

        mail.IsRead.Should().BeFalse();
        changeProcessor.Verify(x => x.ChangeMailReadStatusAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        errorFactory.Verify(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_Generic404Error_DoesNotClassifyAsEntityNotFound()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        SynchronizerErrorContext? capturedContext = null;

        var errorFactory = new Mock<IGmailSynchronizerErrorHandlerFactory>(MockBehavior.Strict);
        errorFactory
            .Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .Callback<SynchronizerErrorContext>(context => capturedContext = context)
            .ReturnsAsync(false);

        var synchronizer = CreateSynchronizer(changeProcessor.Object, errorFactory.Object);
        var request = new DeleteRequest(CreateMailCopy("mail-1"));
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new RequestError
        {
            Code = 404,
            Message = "Not Found"
        };

        var act = () => InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response, error);

        await act.Should().ThrowAsync<SynchronizerException>();
        capturedContext.Should().NotBeNull();
        capturedContext!.IsEntityNotFound.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessSingleNativeRequestResponseAsync_Entity404Error_ClassifiesAsEntityNotFound()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        SynchronizerErrorContext? capturedContext = null;

        var errorFactory = new Mock<IGmailSynchronizerErrorHandlerFactory>(MockBehavior.Strict);
        errorFactory
            .Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .Callback<SynchronizerErrorContext>(context => capturedContext = context)
            .ReturnsAsync(false);

        var synchronizer = CreateSynchronizer(changeProcessor.Object, errorFactory.Object);
        var request = new DeleteRequest(CreateMailCopy("mail-1"));
        var bundle = new HttpRequestBundle<IClientServiceRequest>(Mock.Of<IClientServiceRequest>(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new RequestError
        {
            Code = 404,
            Message = "Requested entity was not found."
        };

        var act = () => InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response, error);

        await act.Should().ThrowAsync<SynchronizerEntityNotFoundException>();
        capturedContext.Should().NotBeNull();
        capturedContext!.IsEntityNotFound.Should().BeTrue();
    }

    private static GmailSynchronizer CreateSynchronizer(
        IGmailChangeProcessor changeProcessor,
        IGmailSynchronizerErrorHandlerFactory? errorFactory = null,
        string? synchronizationDeltaIdentifier = null)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Gmail",
            Address = "user@example.com",
            SynchronizationDeltaIdentifier = synchronizationDeltaIdentifier
        };

        var authenticator = new Mock<IGmailAuthenticator>(MockBehavior.Loose);

        return new GmailSynchronizer(account, authenticator.Object, changeProcessor, errorFactory ?? Mock.Of<IGmailSynchronizerErrorHandlerFactory>());
    }

    private static MailCopy CreateMailCopy(string id) =>
        new()
        {
            UniqueId = Guid.NewGuid(),
            Id = id,
            FolderId = Guid.NewGuid(),
            IsRead = false,
            IsFlagged = false
        };

    private static async Task InvokeProcessSingleNativeRequestResponseAsync(
        GmailSynchronizer synchronizer,
        HttpRequestBundle<IClientServiceRequest> bundle,
        HttpResponseMessage response,
        RequestError? error = null)
    {
        var method = typeof(GmailSynchronizer).GetMethod(
            "ProcessSingleNativeRequestResponseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = method!.Invoke(synchronizer, [bundle, error, response, CancellationToken.None]) as Task;
        task.Should().NotBeNull();
        await task!;
    }

    private static async Task InvokeUpdateAccountSyncIdentifierAsync(GmailSynchronizer synchronizer, ulong historyId)
    {
        var method = typeof(GmailSynchronizer).GetMethod(
            "UpdateAccountSyncIdentifierAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();

        var task = method!.Invoke(synchronizer, [historyId]) as Task;
        task.Should().NotBeNull();
        await task!;
    }
}
