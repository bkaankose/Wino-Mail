using System;
using Wino.Core.Domain.Entities.Calendar;

namespace Wino.Core.Domain.Models.Calendar;

public sealed record CalendarEventEditPolicy(
    bool CanEditEventDetails,
    bool CanEditPersonalOptions,
    bool CanRespond,
    bool CanDeleteEvent,
    bool IsCurrentUserOrganizer)
{
    public static CalendarEventEditPolicy From(CalendarItem item)
    {
        var assignedCalendar = item?.AssignedCalendar;
        var isWritable = assignedCalendar?.IsReadOnly == false;
        var accountAddress = assignedCalendar?.MailAccount?.Address;
        var isOrganizer = !string.IsNullOrWhiteSpace(item?.OrganizerEmail) &&
                          !string.IsNullOrWhiteSpace(accountAddress) &&
                          string.Equals(item.OrganizerEmail, accountAddress, StringComparison.OrdinalIgnoreCase);
        var canEditDetails = isWritable && item?.IsLocked == false;
        var canRespond = isWritable && item?.IsLocked == true && !isOrganizer;

        return new CalendarEventEditPolicy(
            canEditDetails,
            isWritable,
            canRespond,
            canEditDetails,
            isOrganizer);
    }
}
