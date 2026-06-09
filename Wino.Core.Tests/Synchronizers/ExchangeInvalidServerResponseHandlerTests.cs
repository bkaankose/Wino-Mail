using System;
using System.Xml;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Synchronizers.Errors.Exchange;
using Xunit;
// EWS defines its own Task item type; alias bare `Task` to the TPL Task.
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ExchangeInvalidServerResponseHandlerTests
{
    private static SynchronizerErrorContext ContextFor(Exception ex)
        => new() { Exception = ex, ErrorMessage = ex.Message };

    [Fact]
    public void CanHandle_ServiceRequestExceptionWithInnerXmlException_IsTrue()
    {
        // The exact shape produced when EWS gets a non-SOAP (HTML/proxy 5xx) body.
        var ex = new ServiceRequestException(
            "The response received from the service didn't contain valid XML.",
            new XmlException("Data at the root level is invalid. Line 1, position 1."));

        new ExchangeInvalidServerResponseHandler().CanHandle(ContextFor(ex)).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_ServiceRequestExceptionWithValidXmlMessage_IsTrue()
    {
        // Message-only match (no inner exception) still classifies as a non-XML response.
        var ex = new ServiceRequestException("The response received from the service didn't contain valid XML.");

        new ExchangeInvalidServerResponseHandler().CanHandle(ContextFor(ex)).Should().BeTrue();
    }

    [Fact]
    public void CanHandle_UnrelatedServiceRequestException_IsFalse()
    {
        // A ServiceRequestException without the non-XML signature must not be swallowed here.
        var ex = new ServiceRequestException("The request failed. The remote server returned an error: (503).");

        new ExchangeInvalidServerResponseHandler().CanHandle(ContextFor(ex)).Should().BeFalse();
    }

    [Fact]
    public void CanHandle_OtherExceptionType_IsFalse()
    {
        new ExchangeInvalidServerResponseHandler()
            .CanHandle(ContextFor(new InvalidOperationException("boom"))).Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ClassifiesAsTransientServerError_AndRewritesMessage()
    {
        var ex = new ServiceRequestException(
            "The response received from the service didn't contain valid XML.",
            new XmlException("Data at the root level is invalid."));
        var context = ContextFor(ex);

        var handled = await new ExchangeInvalidServerResponseHandler().HandleAsync(context);

        handled.Should().BeTrue();
        context.Severity.Should().Be(SynchronizerErrorSeverity.Transient);
        context.Category.Should().Be(SynchronizerErrorCategory.ServerError);
        context.RetryDelay.Should().NotBeNull();
        context.ErrorMessage.Should().NotContain("Data at the root level"); // cryptic XML-parse wording is gone
        context.ErrorMessage.Should().Contain("HTTP server error");
    }
}
