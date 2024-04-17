using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Synchronization
{
    public class SynchronizationResult
    {
        protected SynchronizationResult() { }

        public IEnumerable<IMailItem> DownloadedMessages { get; set; } = new List<IMailItem>();
        public SynchronizationCompletedState CompletedState { get; set; }

        public static SynchronizationResult Empty => new() { CompletedState = SynchronizationCompletedState.Success };

        public static SynchronizationResult Completed(IEnumerable<IMailItem> downloadedMessages)
            => new() { DownloadedMessages = downloadedMessages, CompletedState = SynchronizationCompletedState.Success };

        public static SynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
    }
}
