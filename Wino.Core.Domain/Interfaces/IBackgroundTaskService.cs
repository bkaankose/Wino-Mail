using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces
{
    public interface IBackgroundTaskService
    {
        /// <summary>
        /// Manages background task registrations, requests access if needed, checks the statusses of them etc.
        /// </summary>
        /// <exception cref="BackgroundTaskExecutionRequestDeniedException">If the access request is denied for some reason.</exception>
        /// <exception cref="BackgroundTaskRegistrationFailedException">If one of the requires background tasks are failed during registration.</exception>
        Task HandleBackgroundTaskRegistrations();

        /// <summary>
        /// Unregisters all existing background tasks. Useful for migrations.
        /// </summary>
        void UnregisterAllBackgroundTask();
    }
}
