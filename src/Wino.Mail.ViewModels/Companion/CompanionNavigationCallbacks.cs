#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels.Companion;

/// <summary>
/// The host's navigation into the main app. Each platform fills these with its own window and
/// activation routes (Windows App.xaml.cs, the macOS AppDelegate).
/// </summary>
public sealed record CompanionNavigationCallbacks(
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
