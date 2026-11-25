#nullable enable
using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Extensions;

public static class KnownImapProviderCatalogExtensions
{
    /// <summary>
    /// Returns the catalog's CardDAV endpoint for a known provider, or an empty string when it has none.
    /// A regional provider uses the region whose incoming host the account connects to, else the default region.
    /// </summary>
    public static string ResolveCardDavServiceUrl(this IKnownImapProviderCatalog? catalog, SpecialImapProvider provider, string? incomingHost)
        => ResolveDavServiceUrl(catalog, provider, incomingHost, isCardDav: true);

    /// <summary>The catalog's CalDAV endpoint for a known provider, chosen the same way as <see cref="ResolveCardDavServiceUrl"/>.</summary>
    public static string ResolveCalDavServiceUrl(this IKnownImapProviderCatalog? catalog, SpecialImapProvider provider, string? incomingHost)
        => ResolveDavServiceUrl(catalog, provider, incomingHost, isCardDav: false);

    private static string ResolveDavServiceUrl(IKnownImapProviderCatalog? catalog, SpecialImapProvider provider, string? incomingHost, bool isCardDav)
    {
        if (catalog == null || provider == SpecialImapProvider.None)
            return string.Empty;

        var definition = catalog.GetBySpecialProvider(provider);
        if (definition == null)
            return string.Empty;

        if (definition.Regions == null || definition.Regions.Count == 0)
            return (isCardDav ? definition.CardDavServiceUrl : definition.CalDavServiceUrl) ?? string.Empty;

        var region = definition.Regions.FirstOrDefault(item =>
                         string.Equals(item.IncomingHost, incomingHost?.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? definition.Regions[0];

        return (isCardDav ? region.CardDavServiceUrl : region.CalDavServiceUrl) ?? string.Empty;
    }

    /// <summary>
    /// The catalog registration a built-in profile signs in with, or null for a discovered or typed issuer, whose DAV
    /// settings must never come from the catalog.
    /// </summary>
    public static KnownImapProviderDefinition? FindOAuthProvider(this IKnownImapProviderCatalog? catalog, AccountOAuthProfile? profile)
    {
        if (catalog == null || profile == null || profile.TrustSource != OAuthTrustSource.BuiltIn)
            return null;

        return catalog.Providers.FirstOrDefault(provider =>
            provider.OAuth is { } registration &&
            string.Equals(registration.Issuer, profile.Issuer, StringComparison.Ordinal) &&
            string.Equals(registration.ClientId, profile.ClientId, StringComparison.Ordinal));
    }

    public static OAuthDavSettings GetOAuthDavSettings(this IKnownImapProviderCatalog? catalog, AccountOAuthProfile? profile)
    {
        var provider = catalog.FindOAuthProvider(profile);
        if (provider?.OAuth is not { } registration)
            return OAuthDavSettings.Standard;

        var davUrls = new[] { provider.CalDavServiceUrl, provider.CardDavServiceUrl }
            .Concat((provider.Regions ?? []).SelectMany(region => new[] { region.CalDavServiceUrl, region.CardDavServiceUrl }));

        string[] domains = davUrls
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.Host : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new OAuthDavSettings(
            string.IsNullOrWhiteSpace(registration.CalendarScope) ? OAuthScopes.Calendars : registration.CalendarScope.Trim(),
            string.IsNullOrWhiteSpace(registration.ContactsScope) ? OAuthScopes.Contacts : registration.ContactsScope.Trim(),
            domains,
            string.IsNullOrWhiteSpace(registration.DavAuthorizationScheme) ? OAuthDavSettings.BearerScheme : registration.DavAuthorizationScheme.Trim());
    }

    /// <summary>The DAV scopes a catalog registration offers in addition to its mail scopes.</summary>
    public static string[] GetOAuthDavScopes(this KnownImapOAuthRegistration? registration)
        => registration == null
            ? []
            : new[] { registration.CalendarScope, registration.ContactsScope }
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Select(scope => scope.Trim())
                .ToArray();
}
