#nullable enable
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// Decides what a delete command removes for an event that belongs to a recurring series.
/// </summary>
internal static class RecurringEventDeletion
{
    /// <summary>
    /// Asks the user for the scope when the item is an occurrence, and confirms deleting a series master.
    /// Call it on the UI thread; it presents dialogs.
    /// </summary>
    /// <returns>The item to delete (the occurrence or its series master), or null when the user cancels.</returns>
    public static async Task<CalendarItem?> ResolveDeleteTargetAsync(CalendarItem? item, IMailDialogService dialogService, ICalendarService calendarService)
    {
        if (item == null)
            return null;

        if (item.IsRecurringChild)
        {
            var scope = await dialogService.ShowRecurringEventDeleteDialogAsync();

            if (scope == null)
                return null;

            if (scope == CalendarEventTargetType.Single)
                return item;

            var seriesMaster = await calendarService.GetCalendarItemTargetAsync(new CalendarItemTarget(item, CalendarEventTargetType.Series));

            if (seriesMaster == null)
            {
                Log.Warning("The series master {SeriesMasterId} of calendar item {CalendarItemId} is not stored locally, so the series cannot be deleted.", item.RecurringCalendarItemId, item.Id);
                return null;
            }

            seriesMaster.AssignedCalendar ??= item.AssignedCalendar;
            return seriesMaster;
        }

        if (item.IsRecurringParent)
        {
            var confirmed = await dialogService.ShowConfirmationDialogAsync(
                Translator.DialogMessage_DeleteRecurringSeriesMessage,
                Translator.DialogMessage_DeleteRecurringSeriesTitle,
                Translator.Buttons_Delete);

            return confirmed ? item : null;
        }

        return item;
    }
}
