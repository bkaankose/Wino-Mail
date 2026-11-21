using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Raises task reminders while the app runs: each open task's reminder, or its snooze, when it falls due.</summary>
public interface ITaskReminderServer
{
    Task StartAsync();
    Task StopAsync();
}
