using System;
using System.Collections.Generic;
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

    public static MailSynchronizationResult Canceled => new() { CompletedState = SynchronizationCompletedState.Canceled };
    public static MailSynchronizationResult Failed(Exception exception) => new()
    {
        CompletedState = SynchronizationCompletedState.Failed,
        Exception = exception
    };
}
