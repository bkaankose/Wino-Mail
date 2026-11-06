#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services.Dav;

public sealed class DavOAuthAuthenticationProvider : IDavOAuthAuthenticationProvider
{
    private static readonly ILogger Logger = Log.ForContext<DavOAuthAuthenticationProvider>();

    private readonly Func<IOAuthTokenService> _tokenService;
    private readonly IAccountOAuthProfileService _profileService;
    private readonly IKnownImapProviderCatalog _catalog;

    public DavOAuthAuthenticationProvider(
        Func<IOAuthTokenService> tokenService,
        IAccountOAuthProfileService profileService,
        IKnownImapProviderCatalog catalog)
    {
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<DavAuthenticationProfile?> TryCreateForAccountAsync(
        Guid accountId,
        CustomServerInformation? serverInformation,
        DavServiceKind kind,
        Uri? davUri,
        CancellationToken cancellationToken = default)
    {
        // Password accounts return here without touching the token service or the profile table.
        if (serverInformation?.IncomingAuthenticationMethod != ImapAuthenticationMethod.OAuth2 || accountId == Guid.Empty)
            return null;

        var profile = await _profileService.GetAsync(accountId).ConfigureAwait(false);
        return await TryCreateAsync(accountId, serverInformation, profile, kind, davUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DavAuthenticationProfile?> TryCreateAsync(
        Guid tokenKey,
        CustomServerInformation? serverInformation,
        AccountOAuthProfile? profile,
        DavServiceKind kind,
        Uri? davUri,
        CancellationToken cancellationToken = default)
    {
        if (serverInformation?.IncomingAuthenticationMethod != ImapAuthenticationMethod.OAuth2 || tokenKey == Guid.Empty)
            return null;

        var issuer = profile?.Issuer;
        var dav = _catalog.GetOAuthDavSettings(profile);

        if (!DavOAuthRule.IsTrustedDavUri(davUri, serverInformation.IncomingServer, issuer, dav))
            return null;

        var tokenService = _tokenService();
        if (tokenService is null)
            return null;

        IReadOnlyCollection<string> grantedScopes;
        try
        {
            grantedScopes = await tokenService.GetGrantedScopesAsync(tokenKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning("The granted OAuth scopes of {Key} could not be read ({ErrorType}).", tokenKey, ex.GetType().Name);
            return null;
        }

        if (!DavOAuthRule.UsesToken(serverInformation.IncomingAuthenticationMethod, grantedScopes, kind, davUri, serverInformation.IncomingServer, issuer, dav))
            return null;

        var accessToken = await GetTokenAsync(() => tokenService.GetAccessTokenAsync(tokenKey, cancellationToken)).ConfigureAwait(false);

        return new DavAuthenticationProfile
        {
            Kind = DavAuthenticationKind.Bearer,
            BearerToken = accessToken,
            BearerScheme = dav.AuthorizationScheme,
            RefreshBearerTokenAsync = (rejectedToken, refreshCancellation) => RefreshAsync(tokenService, tokenKey, rejectedToken, refreshCancellation)
        };
    }

    /// <summary>
    /// Another request may already have refreshed the token; only a token equal to the rejected one is refreshed again,
    /// because issuers that rotate refresh tokens revoke the grant when a superseded one is presented.
    /// </summary>
    private static async Task<string> RefreshAsync(IOAuthTokenService tokenService, Guid tokenKey, string rejectedToken, CancellationToken cancellationToken)
    {
        var current = await GetTokenAsync(() => tokenService.GetAccessTokenAsync(tokenKey, cancellationToken)).ConfigureAwait(false);
        if (!string.Equals(current, rejectedToken, StringComparison.Ordinal))
            return current;

        return await GetTokenAsync(() => tokenService.RefreshNowAsync(tokenKey, cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<string> GetTokenAsync(Func<Task<string>> getToken)
    {
        try
        {
            return await getToken().ConfigureAwait(false);
        }
        catch (OAuthGrantInvalidException ex)
        {
            throw new DavRequestException(401, Translator.DavError_AuthenticationRequired, innerException: ex);
        }
        catch (OAuthTransientException ex)
        {
            throw new DavRequestException(0, Translator.DavError_Network, innerException: ex);
        }
    }
}
