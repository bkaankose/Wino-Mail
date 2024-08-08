namespace Wino.Core.Domain.Interfaces
{
    public interface IBackgroundTaskService
    {
        /// <summary>
        /// Unregisters all existing background tasks. Useful for migrations.
        /// </summary>
        void UnregisterAllBackgroundTask();
    }
}
