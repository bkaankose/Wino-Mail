using System;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// An interface for reporting progress of the synchronization.
    /// Gmail does not support reporting folder progress.
    /// For others, account progress is calculated based on the number of folders.
    /// </summary>
    public interface ISynchronizationProgress
    {
        /// <summary>
        /// Reports account synchronization progress.
        /// </summary>
        /// <param name="accountId">Account id for the report.</param>
        /// <param name="progress">Value. This is always between 0 - 100</param>
        void AccountProgressUpdated(Guid accountId, int progress);
    }
}
