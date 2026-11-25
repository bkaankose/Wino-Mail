using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Writes task lists to iCalendar (VTODO) and CSV files and reads them back. Reading returns new task rows
/// with their steps; the caller creates them through a list's request pipeline.
/// </summary>
public interface ITaskTransferService
{
    /// <summary>One calendar file with a VTODO per task and a child VTODO per step.</summary>
    string ExportIcs(IReadOnlyList<AccountTask> tasks, string calendarName);

    /// <summary>A CSV file with a header row; <paramref name="listNames"/> maps a task list id to its name.</summary>
    string ExportCsv(IReadOnlyList<AccountTask> tasks, IReadOnlyDictionary<System.Guid, string> listNames);

    IReadOnlyList<AccountTask> ImportIcs(string content);
    IReadOnlyList<AccountTask> ImportCsv(string content);
}
