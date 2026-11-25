using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class GtkDialogService
{
    /// <summary>Same contract as Windows and macOS: the edited reminders, or null when the user cancels.</summary>
    public Task<List<CalendarReminderDraft>?> ShowCalendarReminderEditorAsync(List<CalendarReminderDraft> reminders, bool supportsEmailReminder)
        => PresentAsync(owner => CalendarReminderEditorDialog.ShowAsync(owner, reminders, supportsEmailReminder), null);
}
