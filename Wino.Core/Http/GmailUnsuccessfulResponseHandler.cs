using System;
using System.Net;
using System.Threading.Tasks;

using Google.Apis.Http;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Http;

internal class GmailUnsuccessfulResponseHandler : IHttpUnsuccessfulResponseHandler
{
    readonly Func<Task<TokenInformation>> _refreshToken;

    public GmailUnsuccessfulResponseHandler(Func<Task<TokenInformation>> refreshTokenAsync)
    {
        _refreshToken = refreshTokenAsync;
    }

    public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
    {
        if (!args.SupportsRetry) return false;

        if (args.Response?.StatusCode == HttpStatusCode.Unauthorized
            && args.CurrentFailedTry == 1)
        {
            var token = await _refreshToken();
            args.Request.Headers.Authorization = new("Bearer", token.AccessToken);

            return true;
        }

        return false;
    }
}
