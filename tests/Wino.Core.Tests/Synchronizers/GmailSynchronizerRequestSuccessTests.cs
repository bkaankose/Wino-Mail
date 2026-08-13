using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Google;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
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
    public async Task DownloadCalendarEventsAsync_ExpiredSyncToken_PersistsResetAndRetriesFullSync()
    {
        var persistedTokens = new List<string?>();
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        changeProcessor
            .Setup(x => x.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Callback<AccountCalendar>(calendar => persistedTokens.Add(calendar.SynchronizationDeltaToken))
            .Returns(Task.CompletedTask);

        var handler = new ExpiredCalendarSyncTokenHandler();
        var synchronizer = CreateSynchronizer(changeProcessor.Object, handler);
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            RemoteCalendarId = "primary",
            Name = "Primary",
            SynchronizationDeltaToken = "expired-token"
        };

        await InvokeDownloadCalendarEventsAsync(synchronizer, calendar);

        persistedTokens.Should().ContainSingle().Which.Should().BeNull();
        calendar.SynchronizationDeltaToken.Should().Be("fresh-token");
        handler.RequestUris.Should().HaveCount(2);
        handler.RequestUris[0].Query.Should().Contain("syncToken=expired-token");
        handler.RequestUris[1].Query.Should().NotContain("syncToken=");
        handler.RequestUris[1].Query.Should().Contain("timeMin=");
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request);
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request);
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
    public async Task ProcessSingleNativeRequestResponseAsync_CreateCalendarEvent_PersistsEveryLocalReminder()
    {
        var changeProcessor = new Mock<IGmailChangeProcessor>(MockBehavior.Strict);
        var request = CreateCalendarEventRequest();

        changeProcessor
            .Setup(x => x.PersistCreatedCalendarEventAsync(
                request.PreparedItem,
                request.PreparedEvent.Attendees,
                It.Is<List<Reminder>>(reminders =>
                    reminders.Count == 2
                    && reminders.Any(r => r.DurationInSeconds == 15 * 60)
                    && reminders.Any(r => r.DurationInSeconds == 30 * 60)),
                "remote-event"))
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var bundle = new HttpRequestBundle<IGoogleApiRequest, global::Google.Apis.Calendar.v3.Data.Event>(
            Mock.Of<IGoogleApiRequest>(),
            request);
        using var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"remote-event"}""")
        };

        await InvokeProcessSingleNativeRequestResponseAsync(synchronizer, bundle, response);

        changeProcessor.VerifyAll();
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new GoogleRequestError
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new GoogleRequestError
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new GoogleRequestError
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
        var bundle = new HttpRequestBundle<IGoogleApiRequest>(Mock.Of<IGoogleApiRequest>(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var error = new GoogleRequestError
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

    private static GmailSynchronizer CreateSynchronizer(
        IGmailChangeProcessor changeProcessor,
        HttpMessageHandler messageHandler)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Gmail",
            Address = "user@example.com"
        };

        return new GmailSynchronizer(
            account,
            changeProcessor,
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>(),
            messageHandler);
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

    private static CreateCalendarEventRequest CreateCalendarEventRequest()
    {
        var accountId = Guid.NewGuid();
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            RemoteCalendarId = "calendar",
            Name = "Calendar"
        };

        var composeResult = new CalendarEventComposeResult
        {
            AccountId = accountId,
            CalendarId = calendar.Id,
            Title = "Planning",
            StartDate = new DateTime(2026, 7, 28, 10, 0, 0),
            EndDate = new DateTime(2026, 7, 28, 11, 0, 0),
            TimeZoneId = TimeZoneInfo.Local.Id,
            ShowAs = CalendarItemShowAs.Busy,
            SelectedReminders =
            [
                new Reminder { DurationInSeconds = 15 * 60, ReminderType = CalendarItemReminderType.Popup },
                new Reminder { DurationInSeconds = 30 * 60, ReminderType = CalendarItemReminderType.Popup }
            ]
        };

        return new CreateCalendarEventRequest(composeResult, calendar);
    }

    private static async Task InvokeProcessSingleNativeRequestResponseAsync(
        GmailSynchronizer synchronizer,
        HttpRequestBundle<IGoogleApiRequest> bundle,
        HttpResponseMessage response,
        GoogleRequestError? error = null)
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

    private static async Task InvokeDownloadCalendarEventsAsync(
        GmailSynchronizer synchronizer,
        AccountCalendar calendar)
    {
        var method = typeof(GmailSynchronizer).GetMethod(
            "DownloadCalendarEventsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(AccountCalendar), typeof(CancellationToken)],
            modifiers: null);

        method.Should().NotBeNull();

        var task = method!.Invoke(synchronizer, [calendar, CancellationToken.None]) as Task;
        task.Should().NotBeNull();
        await task!;
    }

    private sealed class ExpiredCalendarSyncTokenHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);

            if (RequestUris.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent(
                        """
                        {"error":{"code":410,"message":"Sync token is no longer valid, a full sync is required."}}
                        """)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"items":[],"nextSyncToken":"fresh-token"}
                    """)
            });
        }
    }
}
