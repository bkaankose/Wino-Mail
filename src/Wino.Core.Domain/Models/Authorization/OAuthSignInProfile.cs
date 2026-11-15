#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Authorization;

/// <summary>The profile an IMAP browser sign-in runs with, shared by the address-first setup page and Advanced setup.</summary>
public static class OAuthSignInProfile
{
    /// <summary>
    /// Requests the calendars and contacts scopes the chosen modes need. A discovered client registered without one of
    /// them is registered again, because servers such as Stalwart grant only registered scopes.
    /// </summary>
    public static AccountOAuthProfile PrepareForModes(
        AccountOAuthProfile profile,
        IReadOnlyCollection<string> supportedScopes,
        IEnumerable<string>? registeredScopes,
        bool calDav,
        bool cardDav,
        OAuthDavSettings dav)
    {
        var prepared = profile.Clone();
        if (supportedScopes.Count == 0)
            return prepared;

        var scopes = DavOAuthRule.ComposeScopes(profile.Scopes, supportedScopes, calDav, cardDav, dav);
        var registered = new HashSet<string>(OAuthScopes.Split(profile.Scopes), StringComparer.Ordinal);
        if (registeredScopes != null)
            registered.UnionWith(registeredScopes);

        if (prepared.TrustSource == OAuthTrustSource.Discovered &&
            !string.IsNullOrWhiteSpace(prepared.ClientId) &&
            OAuthScopes.Split(scopes).Any(scope => !registered.Contains(scope)))
        {
            prepared.ClientId = string.Empty;
        }

        prepared.Scopes = scopes;
        return prepared;
    }
}
