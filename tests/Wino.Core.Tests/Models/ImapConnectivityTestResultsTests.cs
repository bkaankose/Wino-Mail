using System;
using FluentAssertions;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class ImapConnectivityTestResultsTests
{
    [Fact]
    public void Failure_PreservesValidationMessageAndProtocolLog()
    {
        var exception = new ImapValidationException(
            "IMAP/SMTP server validation failed: authentication rejected",
            "IMAP S: NO authentication failed",
            new InvalidOperationException("authentication rejected"));

        var result = ImapConnectivityTestResults.Failure(exception);

        result.IsSuccess.Should().BeFalse();
        result.FailedReason.Should().Be(exception.Message);
        result.ProtocolLog.Should().Be(exception.ProtocolLog);
    }
}
