using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Core.Http;

internal sealed class GmailClientMessageHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private readonly IGmailAuthenticator _gmailAuthenticator;
    private readonly MailAccount _mailAccount;
    private readonly IReadOnlyCollection<ProviderFeature> _requiredFeatures;

    public GmailClientMessageHandler(
        IGmailAuthenticator gmailAuthenticator,
        MailAccount mailAccount,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null) : base(new HttpClientHandler())
    {
        _gmailAuthenticator = gmailAuthenticator;
        _mailAccount = mailAccount;
        _requiredFeatures = requiredFeatures;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tokenInformation = await _gmailAuthenticator.GetTokenInformationAsync(_mailAccount, _requiredFeatures);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenInformation.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        var refreshLock = RefreshLocks.GetOrAdd(_mailAccount.Id, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        TokenInformationEx refreshedToken;
        try
        {
            // Another parallel Google request may already have refreshed the shared account token.
            // Reuse it when it differs from the token rejected by this request.
            refreshedToken = await _gmailAuthenticator
                .GetTokenInformationAsync(_mailAccount, _requiredFeatures)
                .ConfigureAwait(false);

            if (string.Equals(refreshedToken.AccessToken, tokenInformation.AccessToken, StringComparison.Ordinal))
            {
                refreshedToken = await _gmailAuthenticator
                    .RefreshTokenInformationAsync(_mailAccount, _requiredFeatures)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            refreshLock.Release();
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
