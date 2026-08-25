using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Core.Domain.Interfaces;

public interface IActivationFileImportService
{
    Task<CalendarEventComposeNavigationArgs?> ImportCalendarEventAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    Task<ContactImportDraft?> ImportContactAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);
}
