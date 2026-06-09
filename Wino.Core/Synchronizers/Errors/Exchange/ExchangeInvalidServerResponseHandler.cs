using System;
using System.Xml;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Errors.Exchange;

/// <summary>
/// Handles EWS responses that are not valid XML — the server (or a reverse proxy / load balancer
/// in front of EWS) returned an HTML error page or other non-SOAP body, typically an HTTP 5xx.
/// The EWS Managed API surfaces this as a <see cref="ServiceRequestException"/> wrapping an
/// <see cref="XmlException"/> ("Data at the root level is invalid"), which is opaque to users;
/// this replaces it with a clear, actionable message and classifies it as a transient server error.
/// </summary>
public class ExchangeInvalidServerResponseHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ExchangeInvalidServerResponseHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.Exception is not ServiceRequestException requestException)
            return false;

        // The tell-tale of a non-SOAP body (HTML error page, proxy 5xx): the response couldn't be
        // parsed as XML. Match on the inner XmlException or the library's own message wording.
        return requestException.InnerException is XmlException
            || (requestException.Message?.Contains("valid XML", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Error(error.Exception,
            "Exchange returned a non-XML response for account {AccountName} ({AccountId}) — likely an HTTP 5xx " +
            "from the mailbox server or a reverse proxy / load balancer in front of EWS.",
            error.Account?.Name, error.Account?.Id);

        // Replace the cryptic "Data at the root level is invalid" with something actionable.
        error.ErrorMessage =
            "The Exchange server returned an unexpected response (not valid XML). This usually means the mailbox " +
            "server returned an HTTP server error (5xx), or a reverse proxy / load balancer in front of EWS is " +
            "misconfigured or unavailable. Verify the EWS endpoint is reachable and healthy.";

        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.ServerError;
        error.RetryDelay = TimeSpan.FromSeconds(15);

        return Task.FromResult(true);
    }
}
