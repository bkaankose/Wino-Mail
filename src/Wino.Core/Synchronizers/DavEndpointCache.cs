#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Wino.Core.Synchronizers;

/// <summary>
/// What the CalDAV and CardDAV engines learn about servers and keep for the life of the container: discovered CalDAV
/// endpoints per account, and the collections that rejected sync-collection or multiget. Nothing here is stored on disk.
/// </summary>
public sealed class DavEndpointCache
{
    internal ConcurrentDictionary<Guid, SemaphoreSlim> CalDavDiscoveryLocks { get; } = new();
    internal ConcurrentDictionary<Guid, DiscoveredDavEndpoint> CalDavEndpoints { get; } = new();
    internal ConcurrentDictionary<string, byte> CardDavSyncCollectionRejected { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, byte> CardDavMultigetRejected { get; } = new(StringComparer.Ordinal);
}

internal sealed record DiscoveredDavEndpoint(Uri? ServiceUri, DateTime AttemptedAtUtc);
