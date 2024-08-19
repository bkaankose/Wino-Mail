using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// An interface that should force synchronizer to do synchronization for only given folder ids
    /// after the execution is completed.
    /// </summary>
    public interface ICustomFolderSynchronizationRequest
    {
        /// <summary>
        /// Which folders to sync after this operation?
        /// </summary>
        List<Guid> SynchronizationFolderIds { get; }
    }
}
