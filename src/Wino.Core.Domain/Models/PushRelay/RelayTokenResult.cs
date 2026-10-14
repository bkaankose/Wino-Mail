#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// The result of <c>POST /api/v1/relay/token</c>: a short-lived ES256 relay token and the relay URL to use it with.
/// Mirrors the API's own record; it is not part of <c>Wino.Mail.Contracts</c>.
/// </summary>
public sealed record RelayTokenDto(string Token, DateTimeOffset ExpiresUtc, string? RelayUrl)
{
    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"RelayTokenDto {{ ExpiresUtc = {ExpiresUtc:O}, RelayUrl = {RelayUrl} }}";
}

/// <summary>Error codes of the API's relay endpoints.</summary>
public static class RelayApiErrorCodes
{
    /// <summary>503: the API has no ECDSA signing key, so no relay token can be issued.</summary>
    public const string RelayUnavailable = "RELAY_UNAVAILABLE";
}

/// <summary>A cached relay token as the relay client uses it. <see cref="ToString"/> never prints the token.</summary>
public sealed class PushRelayAccessToken(string token, DateTimeOffset expiresUtc, Uri relayBaseUri, int? mailboxLimit)
{
    public string Token { get; } = token;
    public DateTimeOffset ExpiresUtc { get; } = expiresUtc;

    /// <summary>The relay base address, always ending in a slash.</summary>
    public Uri RelayBaseUri { get; } = relayBaseUri;

    /// <summary>The token's <c>wr_mbx</c> claim, when present.</summary>
    public int? MailboxLimit { get; } = mailboxLimit;

    public override string ToString() => $"PushRelayAccessToken {{ ExpiresUtc = {ExpiresUtc:O}, RelayBaseUri = {RelayBaseUri} }}";
}
