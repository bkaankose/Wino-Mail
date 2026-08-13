using System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Synchronization;

/// <summary>
/// Represents a user-visible synchronization issue collected during request execution or provider synchronization.
/// </summary>
public class SynchronizationIssue
{
    public string Message { get; set; }
    public int? ErrorCode { get; set; }
    public SynchronizerErrorSeverity Severity { get; set; } = SynchronizerErrorSeverity.Fatal;
    public SynchronizerErrorCategory Category { get; set; } = SynchronizerErrorCategory.Unknown;
    public string OperationType { get; set; }
    public string RequestType { get; set; }
    public Guid? FolderId { get; set; }
    public string FolderName { get; set; }
    public Guid? CalendarId { get; set; }
    public string CalendarName { get; set; }
    public string ScopeName { get; set; }
    public bool WasHandled { get; set; }
    public string HandledBy { get; set; }
    public bool CanContinueSync { get; set; }
    public bool IsEntityNotFound { get; set; }
    public string ExceptionType { get; set; }

    public static SynchronizationIssue FromErrorContext(SynchronizerErrorContext errorContext)
    {
        if (errorContext == null)
            return null;

        return new SynchronizationIssue
        {
            Message = errorContext.ErrorMessage ?? errorContext.Exception?.Message,
            ErrorCode = errorContext.ErrorCode,
            Severity = errorContext.Severity,
            Category = errorContext.Category,
            OperationType = errorContext.OperationType,
            RequestType = errorContext.Request?.GetType().Name,
            FolderId = errorContext.FolderId,
            FolderName = errorContext.FolderName,
            CalendarId = errorContext.CalendarId,
            CalendarName = errorContext.CalendarName,
            ScopeName = GetScopeName(errorContext),
            WasHandled = errorContext.WasHandled,
            HandledBy = errorContext.HandledBy,
            CanContinueSync = errorContext.CanContinueSync,
            IsEntityNotFound = errorContext.IsEntityNotFound,
            ExceptionType = errorContext.Exception?.GetType().Name
        };
    }

    public static SynchronizationIssue FromException(
        Exception exception,
        string operationType = null,
        SynchronizerErrorSeverity severity = SynchronizerErrorSeverity.Fatal,
        SynchronizerErrorCategory category = SynchronizerErrorCategory.Unknown,
        string scopeName = null)
    {
        if (exception == null)
            return null;

        return new SynchronizationIssue
        {
            Message = exception.Message,
            Severity = severity,
            Category = category,
            OperationType = operationType,
            ScopeName = scopeName,
            CanContinueSync = severity == SynchronizerErrorSeverity.Recoverable,
            ExceptionType = exception.GetType().Name
        };
    }

    public static SynchronizationIssue FromFolderResult(FolderSyncResult folderResult)
    {
        if (folderResult == null || folderResult.Success)
            return null;

        return new SynchronizationIssue
        {
            Message = folderResult.ErrorMessage,
            Severity = folderResult.ErrorSeverity ?? SynchronizerErrorSeverity.Fatal,
            Category = folderResult.ErrorCategory ?? SynchronizerErrorCategory.Unknown,
            OperationType = "FolderSync",
            FolderId = folderResult.FolderId,
            FolderName = folderResult.FolderName,
            ScopeName = folderResult.FolderName
        };
    }

    private static string GetScopeName(SynchronizerErrorContext errorContext)
    {
        if (!string.IsNullOrWhiteSpace(errorContext.CalendarName))
            return errorContext.CalendarName;

        if (!string.IsNullOrWhiteSpace(errorContext.FolderName))
            return errorContext.FolderName;

        return errorContext.Request switch
        {
            IFolderActionRequest folderRequest => folderRequest.Folder?.FolderName,
            IMailActionRequest mailRequest => mailRequest.Item?.Subject,
            ICalendarActionRequest calendarRequest => calendarRequest.Item?.Title,
            _ => null
        };
    }
}
