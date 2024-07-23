using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Models.Synchronization
{
    public class SynchronizationResult
    {
        public SynchronizationResult() { }
        public SynchronizationResult(Exception ex)
        {
            Exception = ex;
        }

        public IEnumerable<IMailItem> DownloadedMessages { get; set; } = new List<IMailItem>();
        public SynchronizationCompletedState CompletedState { get; set; }
        public Exception Exception { get; set; }

        public static SynchronizationResult Empty => new() { CompletedState = SynchronizationCompletedState.Success };

        public static SynchronizationResult Completed(IEnumerable<IMailItem> downloadedMessages)
            => new() { DownloadedMessages = downloadedMessages, CompletedState = SynchronizationCompletedState.Success };

        public static SynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
        public static SynchronizationResult Failed(Exception ex) => new(ex) { CompletedState = SynchronizationCompletedState.Failed };
    }
}
