#nullable enable

using System;

namespace MailCal.Core.Domain.Misc;

public static class MessageIdGenerator
{
    // Used only when the sender has no usable domain. It must not identify the client.
    private const string FallbackDomain = "localhost";

    /// <summary>
    /// Generates a Message-ID on the sender's domain, as other mail clients do.
    /// Exchange derives MIME boundaries from it, so it must not carry the client name.
    /// </summary>
    public static string Generate(string? senderAddress)
    {
        return $"<{Guid.NewGuid()}@{GetDomain(senderAddress)}>";
    }

    private static string GetDomain(string? senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress))
            return FallbackDomain;

        var separatorIndex = senderAddress.LastIndexOf('@');
        if (separatorIndex < 0 || separatorIndex == senderAddress.Length - 1)
            return FallbackDomain;

        var domain = senderAddress[(separatorIndex + 1)..].Trim();

        return domain.Length == 0 || domain.IndexOfAny(['<', '>', '@', ' ']) >= 0
            ? FallbackDomain
            : domain;
    }
}
