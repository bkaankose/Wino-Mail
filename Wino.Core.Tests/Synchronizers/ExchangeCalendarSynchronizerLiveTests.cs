using FluentAssertions;
using Microsoft.Exchange.WebServices.Data;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers.Exchange;
using Xunit;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// Live calendar tests against an on-premises Exchange mailbox over EWS (NTLM credentials).
/// Configure via environment variables to run; skipped automatically when absent:
///   EXCHANGE_EWS_URL, EXCHANGE_EMAIL, EXCHANGE_PASSWORD
/// The change processor is mocked with in-memory calendar state (mirrors the mail live tests).
/// These tests create and delete events on the configured mailbox.
/// </summary>
public sealed class ExchangeCalendarSynchronizerLiveTests
{
    private static (string Url, string Email, string Password)? ReadCredentials()
    {
        var url = Environment.GetEnvironmentVariable("EXCHANGE_EWS_URL");
        var email = Environment.GetEnvironmentVariable("EXCHANGE_EMAIL");
        var password = Environment.GetEnvironmentVariable("EXCHANGE_PASSWORD");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        return (url, email, password);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task CalendarSync_DiscoversCalendars()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return; // not configured — skip

        var context = CreateContext(credentials.Value);

        var result = await context.Synchronizer.SynchronizeCalendarEventsAsync(new CalendarSynchronizationOptions
        {
            AccountId = context.Account.Id,
            Type = CalendarSynchronizationType.CalendarEvents
        });

        result.CompletedState.Should().Be(SynchronizationCompletedState.Success);
        context.Calendars.Should().Contain(c => c.IsPrimary, "folder sync should discover the default Calendar");
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task CreateReadDelete_RoundTrip()
    {
        var credentials = ReadCredentials();
        if (credentials is null)
            return;

        var context = CreateContext(credentials.Value);
        var options = new CalendarSynchronizationOptions { AccountId = context.Account.Id, Type = CalendarSynchronizationType.CalendarEvents };

        // Discover calendars first so we have a real RemoteCalendarId to create into.
        await context.Synchronizer.SynchronizeCalendarEventsAsync(options);
        var calendar = context.Calendars.First(c => c.IsPrimary);

        var subject = "Exora EWS calendar live test " + Guid.NewGuid().ToString("N");
        var start = DateTime.UtcNow.AddHours(1);

        var composeResult = new CalendarEventComposeResult
        {
            CalendarId = calendar.Id,
            AccountId = context.Account.Id,
            Title = subject,
            Location = "Exora test",
            HtmlNotes = "Exora EWS calendar round-trip test.",
            StartDate = start,
            EndDate = start.AddHours(1),
            IsAllDay = false,
            TimeZoneId = "UTC",
            ShowAs = CalendarItemShowAs.Busy
        };

        var createBundles = context.Synchronizer.CreateCalendarEvent(new CreateCalendarEventRequest(composeResult, calendar));
        await context.Synchronizer.ExecuteNativeRequestsAsync(createBundles);

        await context.Synchronizer.SynchronizeCalendarEventsAsync(options);
        var created = context.Events.Values.FirstOrDefault(e => e.Title == subject);
        created.Should().NotBeNull("the created event should sync back from the server");

        var deleteBundles = context.Synchronizer.DeleteCalendarEvent(new DeleteCalendarEventRequest(created!));
        await context.Synchronizer.ExecuteNativeRequestsAsync(deleteBundles);

        await context.Synchronizer.SynchronizeCalendarEventsAsync(options);
        context.Events.Values.Should().NotContain(e => e.Title == subject, "the deleted event should be reconciled out of the local store");
    }

    private static TestContext CreateContext((string Url, string Email, string Password) credentials)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Exchange Calendar Live Test",
            Address = credentials.Email,
            ProviderType = MailProviderType.Exchange,
            ServerInformation = new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                IncomingServer = credentials.Url,
                IncomingServerUsername = credentials.Email,
                IncomingServerPassword = credentials.Password,
            }
        };

        var calendars = new List<AccountCalendar>();
        var events = new Dictionary<string, CalendarItem>();

        var changeProcessor = new Mock<IExchangeChangeProcessor>();

        changeProcessor.Setup(x => x.GetAccountCalendarsAsync(account.Id))
            .ReturnsAsync(() => calendars.ToList());

        changeProcessor.Setup(x => x.InsertAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns((AccountCalendar calendar) => { calendars.Add(calendar); return Task.CompletedTask; });

        changeProcessor.Setup(x => x.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns(Task.CompletedTask);

        changeProcessor.Setup(x => x.DeleteAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns((AccountCalendar calendar) => { calendars.RemoveAll(c => c.Id == calendar.Id); return Task.CompletedTask; });

        changeProcessor.Setup(x => x.ManageCalendarEventAsync(It.IsAny<Appointment>(), It.IsAny<AccountCalendar>(), It.IsAny<MailAccount>()))
            .Returns((Appointment appointment, AccountCalendar calendar, MailAccount _) =>
            {
                var remoteId = appointment.Id.UniqueId;
                if (events.TryGetValue(remoteId, out var existing))
                {
                    existing.Title = appointment.Subject;
                }
                else
                {
                    events[remoteId] = new CalendarItem
                    {
                        Id = Guid.NewGuid(),
                        RemoteEventId = remoteId,
                        Title = appointment.Subject,
                        CalendarId = calendar.Id
                    };
                }

                return Task.CompletedTask;
            });

        changeProcessor.Setup(x => x.GetCalendarItemsInRangeAsync(It.IsAny<AccountCalendar>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns((AccountCalendar calendar, DateTime _, DateTime __) =>
                Task.FromResult(events.Values.Where(e => e.CalendarId == calendar.Id).ToList()));

        changeProcessor.Setup(x => x.DeleteCalendarItemAsync(It.IsAny<Guid>()))
            .Returns((Guid id) =>
            {
                var match = events.FirstOrDefault(kvp => kvp.Value.Id == id);
                if (match.Key != null)
                    events.Remove(match.Key);
                return Task.CompletedTask;
            });

        var synchronizer = new ExchangeSynchronizer(
            account,
            new ExchangeNtlmAuthenticator(),
            changeProcessor.Object,
            Mock.Of<IExchangeSynchronizerErrorHandlerFactory>());

        return new TestContext(account, calendars, events, synchronizer);
    }

    private sealed record TestContext(
        MailAccount Account,
        List<AccountCalendar> Calendars,
        Dictionary<string, CalendarItem> Events,
        ExchangeSynchronizer Synchronizer);
}
