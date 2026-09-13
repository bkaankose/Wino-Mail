using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.WinUI.Services.Companion;

internal sealed record CompanionNavigationCallbacks(
    Func<CancellationToken, Task> OpenWino,
    Func<CancellationToken, Task> OpenCalendar,
    Func<CancellationToken, Task> OpenTasks,
    Func<Guid?, CancellationToken, Task> OpenInbox,
    Func<Guid, Guid, CancellationToken, Task> OpenMail,
    Func<Guid, Guid, CancellationToken, Task> OpenCalendarEvent,
    Func<Guid, Guid, CancellationToken, Task> JoinCalendarEvent,
    Func<string?, CancellationToken, Task> FindContact,
    Func<Guid?, CancellationToken, Task> NewMail,
    Func<Guid?, DateTimeOffset?, CancellationToken, Task> NewEvent,
    Func<CancellationToken, Task> OpenSettings);
