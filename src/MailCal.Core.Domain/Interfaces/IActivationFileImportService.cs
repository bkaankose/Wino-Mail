using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Contacts;

namespace MailCal.Core.Domain.Interfaces;

public interface IActivationFileImportService
{
    Task<CalendarEventComposeNavigationArgs?> ImportCalendarEventAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    Task<ContactImportDraft?> ImportContactAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);
}
