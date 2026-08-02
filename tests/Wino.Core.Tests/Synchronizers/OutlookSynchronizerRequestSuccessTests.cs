using System.Net;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Calendar;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoveToFocused_CreatesMessagePatchRequest(bool moveToFocused)
    {
        var synchronizer = CreateSynchronizer(Mock.Of<IOutlookChangeProcessor>());
        var request = new MoveToFocusedRequest(CreateMailCopy(), moveToFocused);

        var bundles = synchronizer.MoveToFocused(new BatchMoveToFocusedRequest([request]));

        bundles.Should().ContainSingle();
        bundles[0].NativeRequest.HttpMethod.Should().Be(Method.PATCH);
        bundles[0].Request.Should().BeSameAs(request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AlwaysMoveTo_CreatesCurrentMessagePatchAndFutureSenderOverride(bool moveToFocused)
    {
        var synchronizer = CreateSynchronizer(Mock.Of<IOutlookChangeProcessor>());
        var mail = CreateMailCopy();
        mail.FromName = "Sender";
        mail.FromAddress = "sender@example.com";
        var request = new AlwaysMoveToRequest(mail, moveToFocused);

        var bundles = synchronizer.AlwaysMoveTo(new BatchAlwaysMoveToRequest([request]));

        bundles.Should().HaveCount(2);
        bundles.Select(bundle => bundle.NativeRequest.HttpMethod)
            .Should().ContainInOrder(Method.PATCH, Method.POST);
        bundles.Should().OnlyContain(bundle => ReferenceEquals(bundle.Request, request));
    }

    [Fact]
    public async Task HandleSuccessfulResponseAsync_CreateCalendarEvent_PersistsEveryLocalReminder()
    {
        var changeProcessor = new Mock<IOutlookChangeProcessor>(MockBehavior.Strict);
        var request = CreateCalendarEventRequest();
        var expectedRemoteEventId = $"remote-event::{request.PreparedItem.Id:N}";

        changeProcessor
            .Setup(x => x.PersistCreatedCalendarEventAsync(
                request.PreparedItem,
                request.PreparedEvent.Attendees,
                It.Is<List<Reminder>>(reminders =>
                    reminders.Count == 2
                    && reminders.Any(r => r.DurationInSeconds == 15 * 60)
                    && reminders.Any(r => r.DurationInSeconds == 30 * 60)),
                expectedRemoteEventId))
            .Returns(Task.CompletedTask);

        var synchronizer = CreateSynchronizer(changeProcessor.Object);
        var bundle = new HttpRequestBundle<RequestInformation>(new RequestInformation(), request, request);
        using var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"remote-event"}""")
        };

        await InvokeHandleSuccessfulResponseAsync(synchronizer, bundle, response);

        changeProcessor.VerifyAll();
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
