using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Synchronization
{
    public class SynchronizationOptions
    {
        /// <summary>
        /// Unique id of synchronization.
        /// </summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>
        /// Account to execute synchronization for.
        /// </summary>
        public Guid AccountId { get; set; }

        /// <summary>
        /// Type of the synchronization to be performed.
        /// </summary>
        public SynchronizationType Type { get; set; }

        /// <summary>
        /// Collection of FolderId to perform SynchronizationType.Custom type sync.
        /// </summary>
        public List<Guid> SynchronizationFolderIds { get; set; }

        /// <summary>
        /// A listener to be notified about the progress of the synchronization.
        /// </summary>
        public ISynchronizationProgress ProgressListener { get; set; }

        /// <summary>
        /// When doing a linked inbox synchronization, we must ignore reporting completion to the caller for each folder.
        /// This Id will help tracking that. Id is unique, but this one can be the same for all sync requests
        /// inside the same linked inbox sync.
        /// </summary>
        public Guid? GroupedSynchronizationTrackingId { get; set; }

        public override string ToString() => $"Type: {Type}, Folders: {(SynchronizationFolderIds == null ? "None" : string.Join(",", SynchronizationFolderIds))}";
    }
}
