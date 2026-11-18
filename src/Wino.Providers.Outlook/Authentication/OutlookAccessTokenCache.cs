using System;
using System.Collections.Concurrent;
using System.Threading;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication;

/// <summary>
/// The Outlook access tokens issued in this container, per account, client and scope set, with one refresh gate per key.
/// <see cref="OutlookAuthenticator"/> is transient, so every instance shares this singleton. Nothing here is stored on disk.
/// </summary>
public sealed class OutlookAccessTokenCache
{
    internal ConcurrentDictionary<string, CachedOutlookAccessToken> Tokens { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, SemaphoreSlim> Gates { get; } = new(StringComparer.Ordinal);
}

internal sealed record CachedOutlookAccessToken(TokenInformationEx TokenInformation, DateTimeOffset ExpiresOn, DateTimeOffset AcquiredOn);
