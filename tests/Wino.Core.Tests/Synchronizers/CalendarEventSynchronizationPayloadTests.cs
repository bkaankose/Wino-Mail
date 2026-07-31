using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Google;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalendarEventSynchronizationPayloadTests
{
    [Fact]
    public async Task GmailUpdate_MapsWallClockTimezoneAndExplicitlyClearsCollections()
    {
        var synchronizer = CreateGmailSynchronizer();
        var item = CreateCalendarItem();
        var request = new UpdateCalendarEventRequest(item, [], [])
        {
            OriginalAttendees = [CreateAttendee()]
        };

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var httpRequest = nativeRequest.CreateHttpRequestMessage();
        using var payload = await ReadJsonAsync(httpRequest.Content);

        httpRequest.Method.Should().Be(HttpMethod.Patch);
        httpRequest.RequestUri!.Query.Should().Contain("sendUpdates=all");
        payload.RootElement.GetProperty("attendees").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("recurrence").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("reminders").GetProperty("useDefault").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("reminders").GetProperty("overrides").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("start").GetProperty("timeZone").GetString().Should().Be("Europe/Warsaw");
        payload.RootElement.GetProperty("start").GetProperty("dateTime").GetString().Should().StartWith("2026-07-15T09:00:00+02:00");
        payload.RootElement.TryGetProperty("status", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GmailUpdate_MapsAttendeesRemindersAndRecurrence()
    {
        var synchronizer = CreateGmailSynchronizer();
        var item = CreateCalendarItem();
        item.Recurrence = $"RRULE:FREQ=WEEKLY;BYDAY=WE{Wino.Core.Domain.Constants.CalendarEventRecurrenceRuleSeperator}EXDATE:20260722T070000Z";

        var request = new UpdateCalendarEventRequest(
            item,
            [CreateAttendee()],
            [
                new Reminder
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = item.Id,
                    DurationInSeconds = 900,
                    ReminderType = CalendarItemReminderType.Popup
                },
                new Reminder
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = item.Id,
                    DurationInSeconds = 3600,
                    ReminderType = CalendarItemReminderType.Email
                }
            ]);

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var httpRequest = nativeRequest.CreateHttpRequestMessage();
        using var payload = await ReadJsonAsync(httpRequest.Content);

        payload.RootElement.GetProperty("attendees")[0].GetProperty("email").GetString().Should().Be("guest@example.com");
        payload.RootElement.GetProperty("attendees")[0].GetProperty("optional").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("reminders").GetProperty("overrides").EnumerateArray()
            .Select(value => (value.GetProperty("method").GetString(), value.GetProperty("minutes").GetInt32()))
            .Should()
            .BeEquivalentTo([("popup", 15), ("email", 60)]);
        payload.RootElement.GetProperty("recurrence").EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Equal("RRULE:FREQ=WEEKLY;BYDAY=WE", "EXDATE:20260722T070000Z");
    }

    [Fact]
    public async Task GmailUpdate_NullAttendeesAndReminders_PreservesProviderValues()
    {
        var synchronizer = CreateGmailSynchronizer();
        var request = new UpdateCalendarEventRequest(CreateCalendarItem(), null, null);

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var httpRequest = nativeRequest.CreateHttpRequestMessage();
        using var payload = await ReadJsonAsync(httpRequest.Content);

        payload.RootElement.TryGetProperty("attendees", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("reminders", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("declined")]
    [InlineData("tentative")]
    public async Task GmailRsvp_UsesPartialAttendeeUpdateWithoutReplacingOtherGuests(string responseStatus)
    {
        var synchronizer = CreateGmailSynchronizer();
        var item = CreateCalendarItem();
        const string comment = "See you there";

        var nativeRequest = responseStatus switch
        {
            "accepted" => synchronizer.AcceptEvent(new AcceptEventRequest(item, comment)).Single().NativeRequest,
            "declined" => synchronizer.DeclineEvent(new DeclineEventRequest(item, comment)).Single().NativeRequest,
            "tentative" => synchronizer.TentativeEvent(new TentativeEventRequest(item, comment)).Single().NativeRequest,
            _ => throw new ArgumentOutOfRangeException(nameof(responseStatus))
        };
        using var httpRequest = nativeRequest.CreateHttpRequestMessage();
        using var payload = await ReadJsonAsync(httpRequest.Content);

        payload.RootElement.GetProperty("attendeesOmitted").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("attendees").GetArrayLength().Should().Be(1);
        payload.RootElement.GetProperty("attendees")[0].GetProperty("email").GetString().Should().Be("owner@example.com");
        payload.RootElement.GetProperty("attendees")[0].GetProperty("responseStatus").GetString().Should().Be(responseStatus);
        payload.RootElement.GetProperty("attendees")[0].GetProperty("comment").GetString().Should().Be(comment);
    }

    [Theory]
    [InlineData("needsAction", CalendarItemStatus.NotResponded)]
    [InlineData("tentative", CalendarItemStatus.Tentative)]
    [InlineData("accepted", CalendarItemStatus.Accepted)]
    [InlineData("declined", CalendarItemStatus.Cancelled)]
    public void GmailInboundEvent_UsesSelfAttendeeResponseAsCalendarStatus(
        string responseStatus,
        CalendarItemStatus expectedStatus)
    {
        var calendarEvent = new global::Google.Apis.Calendar.v3.Data.Event
        {
            Status = "confirmed",
            Attendees =
            [
                new global::Google.Apis.Calendar.v3.Data.EventAttendee
                {
                    Email = "owner@example.com",
                    Self = true,
                    ResponseStatus = responseStatus
                }
            ]
        };

        GmailChangeProcessor.ResolveCalendarItemStatus(calendarEvent).Should().Be(expectedStatus);
    }

    [Fact]
    public void GmailInboundEvent_CancelledEventOverridesSelfAttendeeResponse()
    {
        var calendarEvent = new global::Google.Apis.Calendar.v3.Data.Event
        {
            Status = "cancelled",
            Attendees =
            [
                new global::Google.Apis.Calendar.v3.Data.EventAttendee
                {
                    Self = true,
                    ResponseStatus = "accepted"
                }
            ]
        };

        GmailChangeProcessor.ResolveCalendarItemStatus(calendarEvent).Should().Be(CalendarItemStatus.Cancelled);
    }

    [Fact]
    public async Task OutlookUpdate_ExplicitlyClearsAttendeesRemindersAndRecurrence()
    {
        var synchronizer = CreateOutlookSynchronizer();
        var item = CreateCalendarItem();
        var request = new UpdateCalendarEventRequest(item, [], []);

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var payload = await ReadJsonAsync(nativeRequest.Content);

        nativeRequest.HttpMethod.Should().Be(Method.PATCH);
        payload.RootElement.GetProperty("attendees").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("isReminderOn").GetBoolean().Should().BeFalse();
        payload.RootElement.TryGetProperty("recurrence", out var recurrence).Should().BeTrue();
        recurrence.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OutlookUpdate_MapsReminderRecurrenceAndAttendeeType()
    {
        var synchronizer = CreateOutlookSynchronizer();
        var item = CreateCalendarItem();
        item.Recurrence = "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=WE";

        var request = new UpdateCalendarEventRequest(
            item,
            [CreateAttendee()],
            [
                new Reminder
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = item.Id,
                    DurationInSeconds = 1800,
                    ReminderType = CalendarItemReminderType.Popup
                }
            ]);

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var payload = await ReadJsonAsync(nativeRequest.Content);

        payload.RootElement.GetProperty("attendees")[0].GetProperty("type").GetString().Should().Be("optional");
        payload.RootElement.GetProperty("isReminderOn").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("reminderMinutesBeforeStart").GetInt32().Should().Be(30);
        payload.RootElement.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString().Should().Be("weekly");
        payload.RootElement.GetProperty("recurrence").GetProperty("pattern").GetProperty("interval").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task OutlookUpdate_NullAttendeesAndReminders_PreservesProviderValues()
    {
        var synchronizer = CreateOutlookSynchronizer();
        var request = new UpdateCalendarEventRequest(CreateCalendarItem(), null, null);

        var nativeRequest = synchronizer.UpdateCalendarEvent(request).Single().NativeRequest;
        using var payload = await ReadJsonAsync(nativeRequest.Content);

        payload.RootElement.TryGetProperty("attendees", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("isReminderOn", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("reminderMinutesBeforeStart", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RecurringOccurrenceUpdate_OmitsMasterRecurrenceForBothProviders()
    {
        var item = CreateCalendarItem();
        item.RecurringCalendarItemId = Guid.NewGuid();
        item.Recurrence = "RRULE:FREQ=WEEKLY";
        var request = new UpdateCalendarEventRequest(item, null, null);

        var gmailNativeRequest = CreateGmailSynchronizer().UpdateCalendarEvent(request).Single().NativeRequest;
        using var gmailHttpRequest = gmailNativeRequest.CreateHttpRequestMessage();
        using var gmailPayload = await ReadJsonAsync(gmailHttpRequest.Content);

        var outlookNativeRequest = CreateOutlookSynchronizer().UpdateCalendarEvent(request).Single().NativeRequest;
        using var outlookPayload = await ReadJsonAsync(outlookNativeRequest.Content);

        gmailPayload.RootElement.TryGetProperty("recurrence", out _).Should().BeFalse();
        outlookPayload.RootElement.TryGetProperty("recurrence", out _).Should().BeFalse();
    }

    [Fact]
    public async Task OutlookCreate_UsesOneTimezoneForAllDayBoundsAndDisablesUnselectedReminder()
    {
        var synchronizer = CreateOutlookSynchronizer();
        var item = CreateCalendarItem();
        item.StartDate = new DateTime(2026, 7, 15);
        item.DurationInSeconds = TimeSpan.FromDays(1).TotalSeconds;
        item.StartTimeZone = "Europe/Warsaw";
        item.EndTimeZone = "America/New_York";
        var composeResult = new Wino.Core.Domain.Models.Calendar.CalendarEventComposeResult
        {
            CalendarId = item.CalendarId,
            AccountId = item.AssignedCalendar.AccountId,
            Title = item.Title,
            StartDate = item.StartDate,
            EndDate = item.EndDate,
            TimeZoneId = item.StartTimeZone,
            SelectedReminders = []
        };
        var createRequest = new CreateCalendarEventRequest(composeResult, (AccountCalendar)item.AssignedCalendar);

        var nativeRequest = synchronizer.CreateCalendarEvent(createRequest).Single().NativeRequest;
        using var payload = await ReadJsonAsync(nativeRequest.Content);

        payload.RootElement.GetProperty("isAllDay").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("start").GetProperty("timeZone").GetString().Should().Be("Europe/Warsaw");
        payload.RootElement.GetProperty("end").GetProperty("timeZone").GetString().Should().Be("Europe/Warsaw");
        payload.RootElement.GetProperty("isReminderOn").GetBoolean().Should().BeFalse();
    }

    private static CalendarItem CreateCalendarItem()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Calendar account",
            Address = "owner@example.com"
        };
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = "Primary",
            RemoteCalendarId = "primary",
            MailAccount = account
        };

        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = "remote-event-id",
            CalendarId = calendar.Id,
            AssignedCalendar = calendar,
            Title = "Planning",
            Description = "<p>Notes</p>",
            Location = "Room 4",
            StartDate = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Unspecified),
            DurationInSeconds = 3600,
            StartTimeZone = "Europe/Warsaw",
            EndTimeZone = "Europe/Warsaw",
            Status = CalendarItemStatus.Accepted,
            ShowAs = CalendarItemShowAs.Busy
        };
    }

    private static CalendarEventAttendee CreateAttendee() =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Guest",
            Email = "guest@example.com",
            IsOptionalAttendee = true
        };

    private static GmailSynchronizer CreateGmailSynchronizer()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Gmail",
            Address = "owner@example.com"
        };

        return new GmailSynchronizer(
            account,
            Mock.Of<IGmailAuthenticator>(),
            Mock.Of<IGmailChangeProcessor>(),
            Mock.Of<IGmailSynchronizerErrorHandlerFactory>());
    }

    private static OutlookSynchronizer CreateOutlookSynchronizer()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook",
            Address = "owner@example.com"
        };

        return new OutlookSynchronizer(
            account,
            Mock.Of<IAuthenticator>(),
            Mock.Of<IOutlookChangeProcessor>(),
            Mock.Of<IOutlookSynchronizerErrorHandlerFactory>(),
            Mock.Of<IMailCategoryService>());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content)
    {
        content.Should().NotBeNull();
        return JsonDocument.Parse(await content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> ReadJsonAsync(Stream stream)
    {
        stream.Should().NotBeNull();
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream);
    }
}
