#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MailCal.Mail.ViewModels.Companion;

/// <summary>
/// The host's navigation into the main app, filled with the platform's window and activation routes.
/// </summary>
public sealed record CompanionNavigationCallbacks(
    Func<CancellationToken, Task> OpenMailCal,
    Func<CancellationToken, Task> OpenCalendar,
    Func<Guid, Guid, CancellationToken, Task> OpenCalendarEvent,
    Func<CompanionEventDraft, CancellationToken, Task> NewEvent,
    Func<CancellationToken, Task> OpenSettings);
