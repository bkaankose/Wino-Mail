#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Authorization;

public static class OAuthScopes
{
    public const string Mail = "urn:ietf:params:oauth:scope:mail";
    public const string Calendars = "urn:ietf:params:oauth:scope:calendars";
    public const string Contacts = "urn:ietf:params:oauth:scope:contacts";
    public const string OfflineAccess = "offline_access";

    public static IReadOnlyList<string> Split(string? scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? Array.Empty<string>()
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// How an issuer's token reaches CalDAV and CardDAV. Standard servers use the IETF scopes and Bearer on the mail
/// domain; a catalog provider can name its own scopes, header scheme and the domains its DAV servers live on.
/// </summary>
public sealed record OAuthDavSettings(
    string CalendarScope,
    string ContactsScope,
    IReadOnlyList<string> TrustedDomains,
    string AuthorizationScheme)
{
    public const string BearerScheme = "Bearer";

    public static OAuthDavSettings Standard { get; } = new(OAuthScopes.Calendars, OAuthScopes.Contacts, Array.Empty<string>(), BearerScheme);
}

/// <summary>
/// Decides when CalDAV or CardDAV sends the account's OAuth access token instead of a DAV password, and which scopes a
/// sign-in requests for the modes the user chose.
/// </summary>
public static class DavOAuthRule
{
    public static string GetRequiredScope(DavServiceKind kind, OAuthDavSettings? dav = null)
    {
        dav ??= OAuthDavSettings.Standard;
        return kind == DavServiceKind.CalDav ? dav.CalendarScope : dav.ContactsScope;
    }

    /// <summary>
    /// True when incoming mail signs in with OAuth, the token was granted the DAV scope, and the DAV URL is HTTPS on the
    /// registrable domain of the IMAP host, the issuer, or a domain the catalog names for the provider. A token must
    /// never reach a server outside that organization.
    /// </summary>
    public static bool UsesToken(
        ImapAuthenticationMethod incomingMethod,
        IEnumerable<string>? grantedScopes,
        DavServiceKind kind,
        Uri? davUri,
        string? imapHost,
        string? issuer,
        OAuthDavSettings? dav = null)
    {
        if (incomingMethod != ImapAuthenticationMethod.OAuth2 || grantedScopes is null)
            return false;

        if (!grantedScopes.Contains(GetRequiredScope(kind, dav), StringComparer.Ordinal))
            return false;

        return IsTrustedDavUri(davUri, imapHost, issuer, dav);
    }

    public static bool UsesToken(
        ImapAuthenticationMethod incomingMethod,
        IEnumerable<string>? grantedScopes,
        DavServiceKind kind,
        string? davUrl,
        string? imapHost,
        string? issuer,
        OAuthDavSettings? dav = null)
        => Uri.TryCreate(davUrl?.Trim(), UriKind.Absolute, out var davUri)
           && UsesToken(incomingMethod, grantedScopes, kind, davUri, imapHost, issuer, dav);

    public static bool IsTrustedDavUri(Uri? davUri, string? imapHost, string? issuer, OAuthDavSettings? dav = null)
    {
        if (davUri is null || davUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(davUri.Host))
            return false;

        if (!string.IsNullOrWhiteSpace(imapHost) && RegistrableDomain.AreSame(davUri.Host, imapHost))
            return true;

        if (dav?.TrustedDomains.Any(domain => RegistrableDomain.AreSame(davUri.Host, domain)) == true)
            return true;

        return Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
               && RegistrableDomain.AreSame(davUri.Host, issuerUri.Host);
    }

    /// <summary>
    /// The scopes a sign-in requests: the profile's own scopes without the DAV scopes, plus the calendars and contacts
    /// scopes for the chosen modes when the server supports them. Without a known list of supported scopes nothing is added.
    /// </summary>
    public static string ComposeScopes(string? profileScopes, IReadOnlyCollection<string>? supportedScopes, bool calDav, bool cardDav, OAuthDavSettings? dav = null)
    {
        dav ??= OAuthDavSettings.Standard;
        var scopes = OAuthScopes.Split(profileScopes)
            .Where(scope => !IsDavScope(scope, dav))
            .ToList();

        void AddIfSupported(bool wanted, string scope)
        {
            if (wanted && supportedScopes?.Contains(scope, StringComparer.Ordinal) == true)
                scopes.Add(scope);
        }

        AddIfSupported(calDav, dav.CalendarScope);
        AddIfSupported(cardDav, dav.ContactsScope);

        return string.Join(' ', scopes.Distinct(StringComparer.Ordinal));
    }

    /// <summary>The DAV scopes the chosen modes need that the server supports but the grant lacks.</summary>
    public static IReadOnlyList<string> GetMissingScopes(
        IEnumerable<string>? grantedScopes,
        IReadOnlyCollection<string>? supportedScopes,
        bool calDav,
        bool cardDav,
        OAuthDavSettings? dav = null)
    {
        dav ??= OAuthDavSettings.Standard;
        var granted = grantedScopes?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();

        if (calDav && supportedScopes?.Contains(dav.CalendarScope, StringComparer.Ordinal) == true && !granted.Contains(dav.CalendarScope))
            missing.Add(dav.CalendarScope);

        if (cardDav && supportedScopes?.Contains(dav.ContactsScope, StringComparer.Ordinal) == true && !granted.Contains(dav.ContactsScope))
            missing.Add(dav.ContactsScope);

        return missing;
    }

    private static bool IsDavScope(string scope, OAuthDavSettings dav)
        => scope is OAuthScopes.Calendars or OAuthScopes.Contacts
           || string.Equals(scope, dav.CalendarScope, StringComparison.Ordinal)
           || string.Equals(scope, dav.ContactsScope, StringComparison.Ordinal);
}
