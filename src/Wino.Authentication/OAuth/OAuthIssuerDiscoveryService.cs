#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Authentication.OAuth;

/// <summary>
/// Finds the OAuth issuer for a mail server: the known-provider catalog first, then the HTTPS metadata published on the
/// IMAP host. Runs during every IMAP setup, so failures and timeouts only ever mean "no OAuth offered".
/// </summary>
public sealed class OAuthIssuerDiscoveryService : IOAuthIssuerDiscoveryService
{
    public const string MailScope = OAuthScopes.Mail;
    public const string OfflineAccessScope = OAuthScopes.OfflineAccess;

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);
    private static readonly ILogger Logger = Log.ForContext<OAuthIssuerDiscoveryService>();

    private readonly IKnownImapProviderCatalog _catalog;
    private readonly IOAuthProtocolClient _protocol;

    public OAuthIssuerDiscoveryService(IKnownImapProviderCatalog catalog, IOAuthProtocolClient protocol)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(protocol);
        _catalog = catalog;
        _protocol = protocol;
    }

    public async Task<OAuthDiscoveryResult> DiscoverAsync(
        string emailAddress,
        string imapHost,
        ImapAuthCapabilities capabilities,
        CancellationToken cancellationToken = default,
        bool deferClientRegistration = false)
    {
        capabilities ??= ImapAuthCapabilities.Unknown;
        var host = imapHost?.Trim().TrimEnd('.') ?? string.Empty;

        var builtIn = TryCreateCatalogProfile(emailAddress, host, capabilities);
        if (builtIn is not null)
        {
            return new OAuthDiscoveryResult
            {
                Profile = builtIn,
                IssuerHost = TryGetHost(builtIn.Issuer),
                SupportedScopes = GetBuiltInSupportedScopes(builtIn)
            };
        }

        if (!capabilities.SupportsOAuth || !capabilities.IsTlsVerified || host.Length == 0)
            return OAuthDiscoveryResult.NotOffered;

        try
        {
            return await DiscoverFromHostAsync(host, capabilities, deferClientRegistration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("OAuth discovery for {ImapHost} ended without a usable issuer ({ErrorType}).", host, ex.GetType().Name);
            return new OAuthDiscoveryResult { OfferedWithoutClient = true };
        }
    }

    private async Task<OAuthDiscoveryResult> DiscoverFromHostAsync(string host, ImapAuthCapabilities capabilities, bool deferClientRegistration, CancellationToken cancellationToken)
    {
        OAuthServerMetadata metadata;
        string issuer;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(MetadataTimeout);

            var hint = await _protocol.FindHostMetadataAsync(host, timeout.Token).ConfigureAwait(false);
            if (hint?.Issuer is not { } hintedIssuer)
                return new OAuthDiscoveryResult { OfferedWithoutClient = true };

            issuer = hintedIssuer.Trim();
            OAuthProtocolClient.RequireHttps(issuer, "issuer");

            // The host's document only names the issuer; a different issuer must vouch for itself with its own metadata.
            metadata = OAuthProtocolClient.IssuerEquals(issuer, $"https://{host}")
                ? hint
                : await _protocol.GetMetadataAsync(issuer, timeout.Token).ConfigureAwait(false);
        }

        var endpoints = _protocol.CreateEndpoints(issuer, metadata, OAuthTrustSource.Discovered);
        var issuerHost = new Uri(issuer).Host;
        var requiresConfirmation = !RegistrableDomain.AreSame(issuerHost, host);
        IReadOnlyList<string> supportedScopes = metadata.ScopesSupported?.ToArray() ?? [];

        if (endpoints.RegistrationEndpoint is null)
        {
            return new OAuthDiscoveryResult
            {
                OfferedWithoutClient = true,
                IssuerHost = issuerHost,
                RequiresIssuerConfirmation = requiresConfirmation,
                SupportedScopes = supportedScopes
            };
        }

        var profile = new AccountOAuthProfile
        {
            Issuer = issuer,
            RevocationEndpoint = endpoints.RevocationEndpoint?.AbsoluteUri,
            Scopes = SelectScopes(metadata.ScopesSupported),
            RedirectKind = SupportsS256(metadata) ? OAuthRedirectKind.CustomScheme : OAuthRedirectKind.Loopback,
            TrustSource = OAuthTrustSource.Discovered,
            SaslMechanism = capabilities.PreferredOAuthMechanism
        };

        // Registration only runs for an issuer that passed the trust checks. One the user still has to confirm, or a
        // caller that defers registration, is registered by the sign-in itself, after the user chose it.
        if (requiresConfirmation || deferClientRegistration)
        {
            return new OAuthDiscoveryResult
            {
                Profile = profile,
                IssuerHost = issuerHost,
                RequiresIssuerConfirmation = requiresConfirmation,
                SupportedScopes = supportedScopes
            };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RegistrationTimeout);
            var registration = await _protocol.RegisterClientAsync(endpoints.RegistrationEndpoint, profile.Scopes,
                profile.RedirectKind == OAuthRedirectKind.CustomScheme, timeout.Token).ConfigureAwait(false);
            profile.ClientId = registration.ClientId;
            profile.RedirectKind = registration.RedirectKind;
        }
        catch (Exception ex) when (ex is OAuthProtocolException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Logger.Information("Dynamic client registration with {IssuerHost} failed ({OAuthError}).", issuerHost,
                (ex as OAuthProtocolException)?.OAuthError ?? ex.GetType().Name);
            return new OAuthDiscoveryResult { OfferedWithoutClient = true, IssuerHost = issuerHost, SupportedScopes = supportedScopes };
        }

        return new OAuthDiscoveryResult { Profile = profile, IssuerHost = issuerHost, SupportedScopes = supportedScopes };
    }

    private AccountOAuthProfile? TryCreateCatalogProfile(string emailAddress, string host, ImapAuthCapabilities capabilities)
    {
        var provider = _catalog.Match(emailAddress ?? string.Empty, host);
        if (provider?.OAuth is not { } registration
            || string.IsNullOrWhiteSpace(registration.ClientId)
            || string.IsNullOrWhiteSpace(registration.Issuer))
        {
            return null;
        }

        // A catalog match by email domain alone must not send that provider's token to a different IMAP server.
        if (host.Length > 0 && !IsProviderHost(provider, host))
            return null;

        var mechanism = registration.SaslMechanism;
        if (capabilities.Reached && capabilities.SupportsOAuth && !Supports(capabilities, mechanism))
            mechanism = capabilities.PreferredOAuthMechanism;

        return new AccountOAuthProfile
        {
            Issuer = registration.Issuer,
            AuthorizationEndpoint = NullIfEmpty(registration.AuthorizationEndpoint),
            TokenEndpoint = NullIfEmpty(registration.TokenEndpoint),
            RevocationEndpoint = NullIfEmpty(registration.RevocationEndpoint),
            ClientId = registration.ClientId,
            Scopes = registration.Scopes,
            RedirectKind = registration.RedirectKind,
            LoopbackPort = registration.LoopbackPort,
            TrustSource = OAuthTrustSource.BuiltIn,
            SaslMechanism = mechanism
        };
    }

    private static bool IsProviderHost(KnownImapProviderDefinition provider, string host)
    {
        static bool Matches(string? candidate, string value)
            => !string.IsNullOrWhiteSpace(candidate) && string.Equals(candidate.Trim(), value, StringComparison.OrdinalIgnoreCase);

        return Matches(provider.Incoming.Host, host)
               || provider.IncomingHosts.Any(candidate => Matches(candidate, host))
               || provider.Regions.Any(region => Matches(region.IncomingHost, host));
    }

    private static bool Supports(ImapAuthCapabilities capabilities, OAuthSaslMechanism mechanism)
        => mechanism == OAuthSaslMechanism.OAuthBearer ? capabilities.SupportsOAuthBearer : capabilities.SupportsXOAuth2;

    /// <summary>
    /// Only scopes the server lists: the mail scope (Stalwart requires it), the calendars and contacts scopes, and
    /// offline_access for a refresh token. Requesting an unlisted scope risks invalid_scope, so a server without
    /// scopes_supported gets none. Registration carries all of them because servers such as Stalwart only grant scopes
    /// the client registered; each sign-in then requests the subset its modes need.
    /// </summary>
    private static string SelectScopes(List<string>? scopesSupported)
    {
        if (scopesSupported is null || scopesSupported.Count == 0)
            return string.Empty;

        var selected = new List<string>();
        foreach (var scope in new[] { MailScope, OAuthScopes.Calendars, OAuthScopes.Contacts, OfflineAccessScope })
        {
            if (scopesSupported.Contains(scope, StringComparer.Ordinal))
                selected.Add(scope);
        }

        return string.Join(' ', selected);
    }

    public async Task<IReadOnlyList<string>> GetSupportedScopesAsync(AccountOAuthProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.TrustSource == OAuthTrustSource.BuiltIn)
            return GetBuiltInSupportedScopes(profile);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(MetadataTimeout);
            var metadata = await _protocol.GetMetadataAsync(profile.Issuer, timeout.Token).ConfigureAwait(false);
            return metadata.ScopesSupported?.ToArray() ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("The scopes supported by {IssuerHost} could not be read ({ErrorType}).", TryGetHost(profile.Issuer), ex.GetType().Name);
            return [];
        }
    }

    /// <summary>
    /// A catalog registration supports its mail scopes plus the calendar and contacts scopes it names, which a sign-in
    /// requests only for the modes the user chose.
    /// </summary>
    private IReadOnlyList<string> GetBuiltInSupportedScopes(AccountOAuthProfile profile)
        => OAuthScopes.Split(profile.Scopes)
            .Concat(_catalog.FindOAuthProvider(profile)?.OAuth.GetOAuthDavScopes() ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>A custom-scheme redirect is safe only with PKCE; a server that does not advertise S256 gets loopback.</summary>
    private static bool SupportsS256(OAuthServerMetadata metadata)
        => metadata.CodeChallengeMethodsSupported?.Contains("S256", StringComparer.Ordinal) == true;

    private static string? TryGetHost(string issuer)
        => Uri.TryCreate(issuer, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
