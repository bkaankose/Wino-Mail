using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Helpers;
using Wino.Core.Requests.Calendar;
using Wino.Messaging.Client.Calendar;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CreateCalendarEventRequestTests
{
    [Fact]
    public void ApplyUiChanges_ForNonRecurringEvent_SendsOptimisticAddAndRevertDelete()
    {
        var composeResult = CreateComposeResult();
        var assignedCalendar = CreateAssignedCalendar();
        var request = new CreateCalendarEventRequest(composeResult, assignedCalendar);
        var recipient = new CalendarRequestRecipient();

        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            request.LocalCalendarItemId.Should().NotBeNull();

            request.ApplyUIChanges();
            request.RevertUIChanges();

            recipient.Added.Should().ContainSingle();
            recipient.Deleted.Should().ContainSingle();
            recipient.Added[0].CalendarItem.Id.Should().Be(request.LocalCalendarItemId!.Value);
            recipient.Deleted[0].CalendarItem.Id.Should().Be(request.LocalCalendarItemId!.Value);
            recipient.Added[0].Source.Should().Be(EntityUpdateSource.ClientUpdated);
            recipient.Deleted[0].Source.Should().Be(EntityUpdateSource.ClientReverted);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public void ApplyUiChanges_ForRecurringEvent_DoesNotSendOptimisticMessages()
    {
        var composeResult = CreateComposeResult();
        composeResult.Recurrence = "RRULE:FREQ=DAILY;INTERVAL=1";
        var request = new CreateCalendarEventRequest(composeResult, CreateAssignedCalendar());
        var recipient = new CalendarRequestRecipient();

        WeakReferenceMessenger.Default.RegisterAll(recipient);

        try
        {
            request.LocalCalendarItemId.Should().BeNull();
            request.Item.Should().BeNull();

            request.ApplyUIChanges();
            request.RevertUIChanges();

            recipient.Added.Should().BeEmpty();
            recipient.Deleted.Should().BeEmpty();
            request.PreparedItem.Should().NotBeNull();
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public void SynchronizationActionHelper_ForCreateRequest_ReturnsCalendarCreateAction()
    {
        var request = new CreateCalendarEventRequest(CreateComposeResult(), CreateAssignedCalendar());

        var actionItems = SynchronizationActionHelper.CreateActionItems([request], Guid.NewGuid(), "Test");

        actionItems.Should().ContainSingle();
        actionItems[0].Description.Should().Be(Wino.Core.Domain.Translator.SyncAction_CreatingEvent);
    }

    private static CalendarEventComposeResult CreateComposeResult()
    {
        return new CalendarEventComposeResult
        {
            CalendarId = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Title = "Planning",
            Location = "Room 4",
            HtmlNotes = "<p>Notes</p>",
            StartDate = new DateTime(2026, 3, 7, 10, 0, 0),
            EndDate = new DateTime(2026, 3, 7, 11, 0, 0),
            TimeZoneId = TimeZoneInfo.Local.Id,
            ShowAs = CalendarItemShowAs.Busy
        };
    }

    private static AccountCalendar CreateAssignedCalendar()
    {
        return new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Name = "Primary",
            DefaultShowAs = CalendarItemShowAs.Busy,
            MailAccount = new MailAccount
            {
                Id = Guid.NewGuid(),
                Address = "user@example.com",
                SenderName = "User"
            }
        };
    }

    internal sealed class CalendarRequestRecipient :
        IRecipient<CalendarItemAdded>,
        IRecipient<CalendarItemDeleted>
    {
        public List<CalendarItemAdded> Added { get; } = [];
        public List<CalendarItemDeleted> Deleted { get; } = [];

        public void Receive(CalendarItemAdded message) => Added.Add(message);

        public void Receive(CalendarItemDeleted message) => Deleted.Add(message);
    }
}
