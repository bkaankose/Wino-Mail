using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// Contains context information about a synchronizer error
/// </summary>
public class SynchronizerErrorContext
{
    /// <summary>
    /// Account associated with the error
    /// </summary>
    public MailAccount Account { get; set; }

    /// <summary>
    /// Gets or sets the error code
    /// </summary>
    public int? ErrorCode { get; set; }

    /// <summary>
    /// Gets or sets the error message
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the request bundle associated with the error
    /// </summary>
    public IRequestBundle RequestBundle { get; set; }

    /// <summary>
    /// Gets or sets the original request associated with the error when available.
    /// </summary>
    public IRequestBase Request { get; set; }

    /// <summary>
    /// Gets or sets additional data associated with the error
    /// </summary>
    public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets or sets the exception associated with the error
    /// </summary>
    public Exception Exception { get; set; }

    /// <summary>
    /// Gets or sets the severity of the error for retry decision making.
    /// </summary>
    public SynchronizerErrorSeverity Severity { get; set; } = SynchronizerErrorSeverity.Fatal;

    /// <summary>
    /// Gets or sets the category of the error for targeted handling.
    /// </summary>
    public SynchronizerErrorCategory Category { get; set; } = SynchronizerErrorCategory.Unknown;

    /// <summary>
    /// Gets or sets the current retry attempt count.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retries allowed.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the suggested delay before retrying.
    /// </summary>
    public TimeSpan? RetryDelay { get; set; }

    /// <summary>
    /// Gets or sets the folder ID associated with the error for partial failure tracking.
    /// </summary>
    public Guid? FolderId { get; set; }

    /// <summary>
    /// Gets or sets the folder name for display purposes.
    /// </summary>
    public string FolderName { get; set; }

    /// <summary>
    /// Gets or sets the calendar ID associated with the error for calendar sync issue tracking.
    /// </summary>
    public Guid? CalendarId { get; set; }

    /// <summary>
    /// Gets or sets the calendar name for display purposes.
    /// </summary>
    public string CalendarName { get; set; }

    /// <summary>
    /// Gets or sets the type of operation that failed.
    /// Examples: "FolderSync", "MailSync", "RequestExecution", "Idle"
    /// </summary>
    public string OperationType { get; set; }

    /// <summary>
    /// Gets or sets whether the error was explicitly classified as a missing remote entity.
    /// This is used to distinguish true "mail/folder/event no longer exists" cases from
    /// unrelated HTTP 404 responses that should still surface to the user.
    /// </summary>
    public bool IsEntityNotFound { get; set; }

    /// <summary>
    /// Gets or sets whether a synchronizer error handler processed this error.
    /// </summary>
    public bool WasHandled { get; set; }

    /// <summary>
    /// Gets or sets the handler type that processed this error.
    /// </summary>
    public string HandledBy { get; set; }

    /// <summary>
    /// Gets whether this error should be retried based on severity and retry count.
    /// </summary>
    public bool ShouldRetry => Severity == SynchronizerErrorSeverity.Transient && RetryCount < MaxRetries;

    /// <summary>
    /// Gets whether synchronization can continue despite this error.
    /// </summary>
    public bool CanContinueSync => Severity == SynchronizerErrorSeverity.Recoverable ||
                                    (Severity == SynchronizerErrorSeverity.Transient && RetryCount >= MaxRetries);
}
