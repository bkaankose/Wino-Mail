using System.Collections.Generic;
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
    public static CalendarSynchronizationResult Failed => new() { CompletedState = SynchronizationCompletedState.Failed };
}
