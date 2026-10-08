using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

public class ContactSynchronizationResult
{
    public SynchronizationCompletedState CompletedState { get; set; }
    public int DownloadedCount { get; set; }
    public int ChangedCount { get; set; }
    public int DeletedCount { get; set; }
    public Exception Exception { get; set; }
    public List<SynchronizationIssue> Issues { get; set; } = [];

    public static ContactSynchronizationResult Empty => new() { CompletedState = SynchronizationCompletedState.Success };
    public static ContactSynchronizationResult Completed(int downloaded, int changed, int deleted) => new()
    {
        CompletedState = SynchronizationCompletedState.Success,
        DownloadedCount = downloaded,
        ChangedCount = changed,
        DeletedCount = deleted
    };
    public static ContactSynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
    public static ContactSynchronizationResult Failed(Exception exception) => new() { CompletedState = SynchronizationCompletedState.Failed, Exception = exception };

    public ContactSynchronizationResult MergeIssues(IEnumerable<SynchronizationIssue> issues)
    {
        foreach (var issue in issues?.Where(issue => issue is not null) ?? [])
            if (!Issues.Any(existing => existing.Message == issue.Message && existing.ErrorCode == issue.ErrorCode))
                Issues.Add(issue);
        if (CompletedState == SynchronizationCompletedState.Success && Issues.Count > 0)
            CompletedState = SynchronizationCompletedState.PartiallyCompleted;
        return this;
    }
}
