#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Issues relay tokens through the Wino Mail API (<c>POST /api/v1/relay/token</c>). It is the only relay code that
/// talks to the Wino API. Tokens are cached until two minutes before they expire, and concurrent callers share one call.
/// </summary>
public interface IPushRelayTokenProvider
{
    /// <summary>
    /// Returns a usable relay token. Throws <see cref="Exceptions.PushRelayApiException"/> with
    /// <see cref="PushRelayErrorCodes.SignInRequired"/> when no Wino Account can get one, or
    /// <see cref="PushRelayErrorCodes.ServiceUnavailable"/> when the API cannot issue one now.
    /// </summary>
    Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the cached token so the next call asks the API again.</summary>
    void Invalidate();

    /// <summary>The <c>wr_mbx</c> claim of the last issued token, when known.</summary>
    int? MailboxLimit { get; }
}
