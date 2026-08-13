using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Http;

public class MicrosoftTokenProvider : IAccessTokenProvider
{
    private readonly MailAccount _account;
    private readonly IAuthenticator _authenticator;
    private readonly IReadOnlyCollection<ProviderFeature> _requiredFeatures;

    public MicrosoftTokenProvider(
        MailAccount account,
        IAuthenticator authenticator,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        _account = account;
        _authenticator = authenticator;
        _requiredFeatures = requiredFeatures;
    }

    public AllowedHostsValidator AllowedHostsValidator { get; }

    public async Task<string> GetAuthorizationTokenAsync(Uri uri,
                                                   Dictionary<string, object> additionalAuthenticationContext = null,
                                                   CancellationToken cancellationToken = default)
    {
        var tokenInfo = await _authenticator.GetTokenInformationAsync(_account, _requiredFeatures);

        return tokenInfo.AccessToken;
    }
}
