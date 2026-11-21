using System.Collections.Generic;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.MacOS.Views.Dialogs;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Event reminders editor (Windows CalendarReminderEditorDialog). Owned by the reminders work.</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>Same contract as Windows: the edited reminders, or null when the user cancels.</summary>
    public Task<List<CalendarReminderDraft>?> ShowCalendarReminderEditorAsync(List<CalendarReminderDraft> reminders, bool supportsEmailReminder)
        => PresentAsync(window => new CalendarReminderEditorSheet(reminders, supportsEmailReminder, error).PresentAsync(window));
}
