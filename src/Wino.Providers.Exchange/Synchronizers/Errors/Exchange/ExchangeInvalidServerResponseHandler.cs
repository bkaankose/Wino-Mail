#nullable enable
using System;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Synchronizers.Errors.Exchange;

/// <summary>Classifies non-SOAP EWS responses, usually an HTTP 5xx from the server or a proxy, as transient.</summary>
public class ExchangeInvalidServerResponseHandler : ISynchronizerErrorHandler
{
    private readonly ILogger _logger = Log.ForContext<ExchangeInvalidServerResponseHandler>();

    public bool CanHandle(SynchronizerErrorContext error)
    {
        if (error.Exception is not ServiceRequestException requestException)
            return false;

        return requestException.InnerException is XmlException
            || (requestException.Message?.Contains("valid XML", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public Task<bool> HandleAsync(SynchronizerErrorContext error)
    {
        _logger.Error(error.Exception,
            "Exchange returned a non-XML response for account {AccountName} ({AccountId}).",
            error.Account?.Name, error.Account?.Id);

        error.ErrorMessage = Translator.Synchronizer_ExchangeInvalidServerResponse;
        error.Severity = SynchronizerErrorSeverity.Transient;
        error.Category = SynchronizerErrorCategory.ServerError;
        error.RetryDelay = TimeSpan.FromSeconds(15);

        return Task.FromResult(true);
    }
}
