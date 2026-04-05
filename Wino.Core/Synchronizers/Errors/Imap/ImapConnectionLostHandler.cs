using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using MailKit;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Synchronizers.Errors.Imap;

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
            "IMAP connection lost for account {AccountName} ({AccountId}). Folder: {FolderName}. Operation: {Operation}. Will retry.",
            error.Account?.Name, error.Account?.Id, error.FolderName ?? "N/A", error.OperationType ?? "N/A");

        // Mark as transient - the RetryExecutor will handle the retry logic
        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.Network;

        // Suggest a reasonable retry delay for connection issues
        error.RetryDelay = TimeSpan.FromSeconds(2);

        return Task.FromResult(true);
    }
}
