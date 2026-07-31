using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// Result of synchronizing a single folder.
/// Used for partial failure tracking when one folder fails but others succeed.
/// </summary>
public class FolderSyncResult
{
    /// <summary>
    /// Gets or sets the folder ID.
    /// </summary>
    public Guid FolderId { get; set; }

    /// <summary>
    /// Gets or sets the folder name for display purposes.
    /// </summary>
    public string FolderName { get; set; }

    /// <summary>
    /// Gets or sets whether the folder sync was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the number of messages downloaded/synchronized.
    /// </summary>
    public int DownloadedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of messages deleted locally (removed from server).
    /// </summary>
    public int DeletedCount { get; set; }

    /// <summary>
    /// Gets or sets the number of messages whose flags were updated.
    /// </summary>
    public int UpdatedCount { get; set; }

    /// <summary>
    /// Gets or sets the error message if sync failed.
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets the error severity if sync failed.
    /// </summary>
    public SynchronizerErrorSeverity? ErrorSeverity { get; set; }

    /// <summary>
    /// Gets or sets the error category if sync failed.
    /// </summary>
    public SynchronizerErrorCategory? ErrorCategory { get; set; }

    /// <summary>
    /// Gets or sets whether this folder was skipped (e.g., due to configuration).
    /// </summary>
    public bool WasSkipped { get; set; }

    /// <summary>
    /// Gets or sets the reason the folder was skipped.
    /// </summary>
    public string SkipReason { get; set; }

    /// <summary>
    /// Creates a successful folder sync result.
    /// </summary>
    public static FolderSyncResult Successful(Guid folderId, string folderName, int downloaded = 0, int deleted = 0, int updated = 0)
        => new()
        {
            FolderId = folderId,
            FolderName = folderName,
            Success = true,
            DownloadedCount = downloaded,
            DeletedCount = deleted,
            UpdatedCount = updated
        };

    /// <summary>
    /// Creates a failed folder sync result.
    /// </summary>
    public static FolderSyncResult Failed(Guid folderId, string folderName, string errorMessage,
        SynchronizerErrorSeverity severity = SynchronizerErrorSeverity.Fatal,
        SynchronizerErrorCategory category = SynchronizerErrorCategory.Unknown)
        => new()
        {
            FolderId = folderId,
            FolderName = folderName,
            Success = false,
            ErrorMessage = errorMessage,
            ErrorSeverity = severity,
            ErrorCategory = category
        };

    /// <summary>
    /// Creates a failed folder sync result from an error context.
    /// </summary>
    public static FolderSyncResult Failed(Guid folderId, string folderName, SynchronizerErrorContext errorContext)
        => new()
        {
            FolderId = folderId,
            FolderName = folderName,
            Success = false,
            ErrorMessage = errorContext?.ErrorMessage ?? "Unknown error",
            ErrorSeverity = errorContext?.Severity ?? SynchronizerErrorSeverity.Fatal,
            ErrorCategory = errorContext?.Category ?? SynchronizerErrorCategory.Unknown
        };

    /// <summary>
    /// Creates a skipped folder sync result.
    /// </summary>
    public static FolderSyncResult Skipped(Guid folderId, string folderName, string reason)
        => new()
        {
            FolderId = folderId,
            FolderName = folderName,
            Success = true, // Skipping is not a failure
            WasSkipped = true,
            SkipReason = reason
        };
}
