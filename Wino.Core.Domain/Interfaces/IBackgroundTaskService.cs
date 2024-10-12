using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IBackgroundTaskService
    {
        /// <summary>
        /// Unregisters all background tasks once.
        /// This is used to clean up the background tasks when the app is updated.
        /// Safe to execute multiple times.
        /// </summary>
        void UnregisterAllBackgroundTask();

        /// <summary>
        /// Registers required background tasks.
        /// </summary>
        Task RegisterBackgroundTasksAsync();
    }
}
