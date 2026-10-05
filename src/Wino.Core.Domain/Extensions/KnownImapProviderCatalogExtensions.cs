#nullable enable
using System;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Extensions;

public static class KnownImapProviderCatalogExtensions
{
    /// <summary>
    /// Returns the catalog's CardDAV endpoint for a known provider, or an empty string when it has none.
    /// A regional provider uses the region whose incoming host the account connects to, else the default region.
    /// </summary>
    public static string ResolveCardDavServiceUrl(this IKnownImapProviderCatalog? catalog, SpecialImapProvider provider, string? incomingHost)
    {
        if (catalog == null || provider == SpecialImapProvider.None)
            return string.Empty;

        var definition = catalog.GetBySpecialProvider(provider);
        if (definition == null)
            return string.Empty;

        if (definition.Regions == null || definition.Regions.Count == 0)
            return definition.CardDavServiceUrl ?? string.Empty;

        var region = definition.Regions.FirstOrDefault(item =>
                         string.Equals(item.IncomingHost, incomingHost?.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? definition.Regions[0];

        return region.CardDavServiceUrl ?? string.Empty;
    }
}
