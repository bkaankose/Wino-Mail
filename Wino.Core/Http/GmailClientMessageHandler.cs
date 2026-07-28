using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Http;

internal sealed class GmailClientMessageHandler : DelegatingHandler
{
    private readonly IGmailAuthenticator _gmailAuthenticator;
    private readonly MailAccount _mailAccount;

    public GmailClientMessageHandler(IGmailAuthenticator gmailAuthenticator, MailAccount mailAccount) : base(new HttpClientHandler())
    {
        _gmailAuthenticator = gmailAuthenticator;
        _mailAccount = mailAccount;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tokenInformation = await _gmailAuthenticator.GetTokenInformationAsync(_mailAccount);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenInformation.AccessToken);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();

        var refreshedToken = await _gmailAuthenticator
            .RefreshTokenInformationAsync(_mailAccount)
            .ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
