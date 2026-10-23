using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Http;

/// <summary>
/// Retries one rejected Microsoft Graph request after forcing a silent token refresh.
/// </summary>
public sealed class GraphAuthenticationRetryHandler(
    MailAccount account,
    IAuthenticator authenticator,
    IReadOnlyCollection<ProviderFeature> requiredFeatures = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        var refreshedToken = await authenticator
            .RefreshTokenInformationAsync(account, requiredFeatures, cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
