using System;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Imap;

/// <summary>
/// Handles generic IMAP protocol errors (ImapProtocolException, ImapCommandException).
/// This is the catch-all handler for IMAP errors not handled by more specific handlers.
/// </summary>
public class ImapProtocolErrorHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ImapProtocolErrorHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        // This is a catch-all for IMAP-related exceptions
        return error.Exception is ImapProtocolException ||
               error.Exception is ImapCommandException;
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        var severity = ClassifyProtocolError(error);
        var category = SynchronizerErrorCategory.ProtocolError;

        _logger.Warning(error.Exception,
            "IMAP protocol error for account {AccountName} ({AccountId}). Folder: {FolderName}. Operation: {Operation}. Severity: {Severity}",
            error.Account?.Name, error.Account?.Id, error.FolderName ?? "N/A", error.OperationType ?? "N/A", severity);

        error.Severity = severity;
        error.Category = category;

        // For transient protocol errors, suggest a retry delay
        if (severity == SynchronizerErrorSeverity.Transient)
        {
            error.RetryDelay = TimeSpan.FromSeconds(5);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Classifies the protocol error to determine if it's transient, recoverable, or fatal.
    /// </summary>
    private static SynchronizerErrorSeverity ClassifyProtocolError(SynchronizerErrorContext error)
    {
        var message = error.ErrorMessage?.ToLowerInvariant() ?? string.Empty;
        var exMessage = error.Exception?.Message?.ToLowerInvariant() ?? string.Empty;

        // Check for rate limiting / throttling
        if (message.Contains("too many") || message.Contains("rate limit") ||
            message.Contains("throttl") || exMessage.Contains("too many"))
        {
            return SynchronizerErrorSeverity.Transient;
        }

        // Check for temporary server issues
        if (message.Contains("try again") || message.Contains("temporary") ||
            message.Contains("busy") || exMessage.Contains("try again"))
        {
            return SynchronizerErrorSeverity.Transient;
        }

        // Check for command-specific errors that are usually transient
        if (error.Exception is ImapCommandException cmdEx)
        {
            // NO response usually means the operation failed but can be retried
            if (cmdEx.Response == ImapCommandResponse.No)
            {
                // Unless it's a permanent failure indication
                if (message.Contains("permanent") || message.Contains("invalid"))
                {
                    return SynchronizerErrorSeverity.Recoverable;
                }
                return SynchronizerErrorSeverity.Transient;
            }

            // BAD response usually indicates a protocol violation - don't retry
            if (cmdEx.Response == ImapCommandResponse.Bad)
            {
                return SynchronizerErrorSeverity.Recoverable;
            }
        }

        // Protocol exceptions that indicate connection issues
        if (error.Exception is ImapProtocolException)
        {
            // Most protocol exceptions are connection-related and transient
            return SynchronizerErrorSeverity.Transient;
        }

        // Default to recoverable for unknown protocol errors
        return SynchronizerErrorSeverity.Recoverable;
    }
}
