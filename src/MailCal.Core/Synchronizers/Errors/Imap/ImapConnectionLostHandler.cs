using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using MailKit;
using Serilog;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Core.Synchronizers.Errors.Imap;

/// <summary>
/// Handles IMAP connection loss errors (IOException, SocketException, ServiceNotConnectedException).
/// Marks the error as transient for retry with backoff.
/// </summary>
public class ImapConnectionLostHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ImapConnectionLostHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        return error.Exception is IOException ||
               error.Exception is SocketException ||
               error.Exception is ServiceNotConnectedException ||
               error.Exception?.InnerException is IOException ||
               error.Exception?.InnerException is SocketException;
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Warning(error.Exception,
            "IMAP connection lost for account {AccountName} ({AccountId}). Folder: {FolderName}. Operation: {Operation}.",
            error.Account?.Name, error.Account?.Id, error.FolderName ?? "N/A", error.OperationType ?? "N/A");

        // The caller decides whether its operation is safe to retry.
        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.Network;

        // Suggest a reasonable retry delay for connection issues
        error.RetryDelay = TimeSpan.FromSeconds(2);

        return Task.FromResult(true);
    }
}
