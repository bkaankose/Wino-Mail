using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Models.Synchronization;

public class CalendarSynchronizationResult
{
    public CalendarSynchronizationResult() { }

    /// <summary>
    /// Gets the new downloaded events from synchronization.
    /// Server will create notifications for these event.
    /// It's ignored in serialization. Client should not react to this.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<ICalendarItem> DownloadedEvents { get; set; } = [];

    public ProfileInformation ProfileInformation { get; set; }

    public SynchronizationCompletedState CompletedState { get; set; }

    public Exception Exception { get; set; }

    public List<SynchronizationIssue> Issues { get; set; } = [];

    [JsonIgnore]
    public IEnumerable<SynchronizationIssue> AllIssues => Issues;

    public static CalendarSynchronizationResult Empty => new() { CompletedState = SynchronizationCompletedState.Success };

    // Mail synchronization
    public static CalendarSynchronizationResult Completed(IEnumerable<ICalendarItem> downloadedEvent)
        => new()
        {
            DownloadedEvents = downloadedEvent,
            CompletedState = SynchronizationCompletedState.Success
        };

    // Profile synchronization
    public static CalendarSynchronizationResult Completed(ProfileInformation profileInformation)
        => new()
        {
            ProfileInformation = profileInformation,
            CompletedState = SynchronizationCompletedState.Success
        };

    public static CalendarSynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
    public static CalendarSynchronizationResult Failed(Exception exception = null) => new()
    {
        CompletedState = SynchronizationCompletedState.Failed,
        Exception = exception
    };

    public CalendarSynchronizationResult MergeIssues(IEnumerable<SynchronizationIssue> issues)
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

        if (CompletedState == SynchronizationCompletedState.Success && Issues.Any())
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
