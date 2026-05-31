using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarEventEditPolicyTests
{
    [Fact]
    public void OrganizerOnWritableUnlockedEventCanEditDetailsAndPersonalOptions()
    {
        var item = CreateItem(isReadOnly: false, isLocked: false, organizerEmail: "me@example.com");

        var policy = CalendarEventEditPolicy.From(item);

        Assert.True(policy.IsCurrentUserOrganizer);
        Assert.True(policy.CanEditEventDetails);
        Assert.True(policy.CanEditPersonalOptions);
        Assert.True(policy.CanDeleteEvent);
        Assert.False(policy.CanRespond);
    }

    [Fact]
    public void InviteeLockedEventCanOnlyEditPersonalOptionsAndRespond()
    {
        var item = CreateItem(isReadOnly: false, isLocked: true, organizerEmail: "organizer@example.com");

        var policy = CalendarEventEditPolicy.From(item);

        Assert.False(policy.IsCurrentUserOrganizer);
        Assert.False(policy.CanEditEventDetails);
        Assert.True(policy.CanEditPersonalOptions);
        Assert.False(policy.CanDeleteEvent);
        Assert.True(policy.CanRespond);
    }

    [Fact]
    public void ReadOnlyCalendarAllowsNoEdits()
    {
        var item = CreateItem(isReadOnly: true, isLocked: false, organizerEmail: "me@example.com");

        var policy = CalendarEventEditPolicy.From(item);

        Assert.False(policy.CanEditEventDetails);
        Assert.False(policy.CanEditPersonalOptions);
        Assert.False(policy.CanDeleteEvent);
        Assert.False(policy.CanRespond);
    }

    [Fact]
    public void WritableSharedCalendarUnlockedEventIsEditableEvenWhenNotOrganizer()
    {
        var item = CreateItem(isReadOnly: false, isLocked: false, organizerEmail: "owner@example.com");

        var policy = CalendarEventEditPolicy.From(item);

        Assert.False(policy.IsCurrentUserOrganizer);
        Assert.True(policy.CanEditEventDetails);
        Assert.True(policy.CanEditPersonalOptions);
    }

    [Fact]
    public void LocalSelfCreatedEventUsesOrganizerEmailComparison()
    {
        var item = CreateItem(isReadOnly: false, isLocked: false, organizerEmail: "ME@example.com");

        var policy = CalendarEventEditPolicy.From(item);

        Assert.True(policy.IsCurrentUserOrganizer);
        Assert.True(policy.CanEditEventDetails);
    }

    private static CalendarItem CreateItem(bool isReadOnly, bool isLocked, string organizerEmail)
    {
        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = Guid.NewGuid(),
            OrganizerEmail = organizerEmail,
            IsLocked = isLocked,
            AssignedCalendar = new AccountCalendar
            {
                Id = Guid.NewGuid(),
                IsReadOnly = isReadOnly,
                MailAccount = new MailAccount
                {
                    Id = Guid.NewGuid(),
                    Address = "me@example.com"
                }
            }
        };
    }
}
