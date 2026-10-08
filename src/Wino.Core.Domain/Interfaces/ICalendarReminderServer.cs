using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Polls the calendar for due reminders and raises a reminder notification for each one.
/// Both app heads start it once the shell is running.
/// </summary>
public interface ICalendarReminderServer
{
    /// <summary>Starts polling. Does nothing when already running or when no account has calendar access.</summary>
    Task StartAsync();

    /// <summary>Stops polling and waits for the current tick to finish. The server can be started again.</summary>
    Task StopAsync();
}
