#nullable enable
using System.Threading;
using System.Threading.Tasks;
using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Mail.ViewModels.Companion;

public interface ICompanionActionHandler
{
    Task OpenMailCalAsync(CancellationToken cancellationToken);
    Task OpenCalendarAsync(CancellationToken cancellationToken);
    Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken);
    Task OpenEventEditorAsync(CompanionEventDraft draft, CancellationToken cancellationToken);
    Task OpenSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Creates the event in its calendar. Throws a validation exception with a user-facing message.</summary>
    Task CreateQuickEventAsync(CompanionEventDraft draft, CancellationToken cancellationToken);
}
