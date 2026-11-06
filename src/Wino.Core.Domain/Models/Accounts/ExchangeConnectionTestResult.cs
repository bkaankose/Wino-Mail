#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public enum ExchangeConnectionTestStatus
{
    Succeeded,

    /// <summary>The server refused the password or the token.</summary>
    CredentialRejected,

    /// <summary>The server could not be reached or answered with an error.</summary>
    Failed
}

/// <summary>
/// The outcome of <see cref="Interfaces.IExchangeConnectionTester"/>. <see cref="DetectedTransport"/> is MAPI over HTTP
/// when Autodiscover advertised it, EWS when Autodiscover answered without it, and Automatic when it was not learned.
/// </summary>
public sealed record ExchangeConnectionTestResult(
    ExchangeConnectionTestStatus Status,
    ExchangeTransport DetectedTransport = ExchangeTransport.Automatic,
    string? FailureReason = null)
{
    public bool IsSuccess => Status == ExchangeConnectionTestStatus.Succeeded;
}
