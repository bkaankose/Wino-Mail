using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Models.Synchronization;

public class MailSynchronizationResult
{
    public MailSynchronizationResult() { }

    /// <summary>
    /// Gets the new downloaded messages from synchronization.
    /// Server will create notifications for these messages.
    /// It's ignored in serialization. Client should not react to this.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<MailCopy> DownloadedMessages { get; set; } = [];

    public ProfileInformation ProfileInformation { get; set; }

    public SynchronizationCompletedState CompletedState { get; set; }

    public Exception Exception { get; set; }

    public List<SynchronizationIssue> Issues { get; set; } = [];

    /// <summary>
    /// Gets or sets the results for each folder that was synchronized.
    /// Enables partial failure tracking - some folders may succeed while others fail.
    /// </summary>
    public List<FolderSyncResult> FolderResults { get; set; } = [];

    /// <summary>
    /// Gets whether the synchronization had any partial failures.
    /// True if at least one folder failed but others succeeded.
    /// </summary>
    [JsonIgnore]
    public bool HasPartialFailures => FolderResults.Any(f => !f.Success) && FolderResults.Any(f => f.Success);

    /// <summary>
    /// Gets the number of folders that were successfully synchronized.
    /// </summary>
    [JsonIgnore]
    public int SuccessfulFolderCount => FolderResults.Count(f => f.Success);

    /// <summary>
    /// Gets the number of folders that failed to synchronize.
    /// </summary>
    [JsonIgnore]
    public int FailedFolderCount => FolderResults.Count(f => !f.Success);

    /// <summary>
    /// Gets the total number of messages downloaded across all folders.
    /// </summary>
    [JsonIgnore]
    public int TotalDownloadedCount => FolderResults.Sum(f => f.DownloadedCount);

    /// <summary>
    /// Gets the total number of messages deleted across all folders.
    /// </summary>
    [JsonIgnore]
    public int TotalDeletedCount => FolderResults.Sum(f => f.DeletedCount);

    /// <summary>
    /// Gets the total number of messages updated across all folders.
    /// </summary>
    [JsonIgnore]
    public int TotalUpdatedCount => FolderResults.Sum(f => f.UpdatedCount);

    /// <summary>
    /// Gets the folders that failed to sync for error reporting.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<FolderSyncResult> FailedFolders => FolderResults.Where(f => !f.Success);

    [JsonIgnore]
    public IEnumerable<SynchronizationIssue> AllIssues
        => Issues.Concat(FailedFolders.Select(SynchronizationIssue.FromFolderResult).Where(issue => issue != null));

    public static MailSynchronizationResult Empty => new() { CompletedState = SynchronizationCompletedState.Success };

    // Mail synchronization
    public static MailSynchronizationResult Completed(IEnumerable<MailCopy> downloadedMessages)
        => new()
        {
            DownloadedMessages = downloadedMessages,
            CompletedState = SynchronizationCompletedState.Success
        };

    // Profile synchronization
    public static MailSynchronizationResult Completed(ProfileInformation profileInformation)
        => new()
        {
            ProfileInformation = profileInformation,
            CompletedState = SynchronizationCompletedState.Success
        };

    /// <summary>
    /// Creates a completed result with folder-level results.
    /// </summary>
    public static MailSynchronizationResult CompletedWithFolderResults(
        IEnumerable<MailCopy> downloadedMessages,
        List<FolderSyncResult> folderResults)
    {
        var hasAnyFailure = folderResults.Any(f => !f.Success);
        var hasAnySuccess = folderResults.Any(f => f.Success);

        return new()
        {
            DownloadedMessages = downloadedMessages,
            FolderResults = folderResults,
            CompletedState = hasAnyFailure && !hasAnySuccess
                ? SynchronizationCompletedState.Failed
                : hasAnyFailure
                    ? SynchronizationCompletedState.PartiallyCompleted
                    : SynchronizationCompletedState.Success
        };
    }

    public static MailSynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
    public static MailSynchronizationResult Failed(Exception exception) => new()
    {
        CompletedState = SynchronizationCompletedState.Failed,
        Exception = exception
    };

    public MailSynchronizationResult MergeIssues(IEnumerable<SynchronizationIssue> issues)
    {
        if (issues == null)
            return this;

        foreach (var issue in issues.Where(issue => issue != null))
        {
            if (!Issues.Any(existing => AreEquivalent(existing, issue)))
            {
                Issues.Add(issue);
            }
        }

        if (CompletedState == SynchronizationCompletedState.Success && AllIssues.Any())
        {
            CompletedState = SynchronizationCompletedState.PartiallyCompleted;
        }

        if (Exception == null)
        {
            Exception = Issues.FirstOrDefault(issue => !string.IsNullOrWhiteSpace(issue?.Message)) is { } issue
                ? new Exception(issue.Message)
                : null;
        }

        return this;
    }

    private static bool AreEquivalent(SynchronizationIssue left, SynchronizationIssue right)
        => string.Equals(left?.Message, right?.Message, StringComparison.Ordinal)
           && left?.ErrorCode == right?.ErrorCode
           && left?.Severity == right?.Severity
           && left?.Category == right?.Category
           && string.Equals(left?.OperationType, right?.OperationType, StringComparison.Ordinal)
           && string.Equals(left?.RequestType, right?.RequestType, StringComparison.Ordinal)
           && left?.FolderId == right?.FolderId
           && left?.CalendarId == right?.CalendarId
           && string.Equals(left?.ScopeName, right?.ScopeName, StringComparison.Ordinal);
}
