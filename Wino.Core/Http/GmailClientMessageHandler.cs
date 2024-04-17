using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Http;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Http
{
    internal class GmailClientMessageHandler : ConfigurableMessageHandler
    {
        public Func<Task<TokenInformation>> TokenRetrieveDelegate { get; }

        public GmailClientMessageHandler(Func<Task<TokenInformation>> tokenRetrieveDelegate) : base(new HttpClientHandler())
        {
            TokenRetrieveDelegate = tokenRetrieveDelegate;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var tokenizationTask = TokenRetrieveDelegate.Invoke();
            var tokenInformation = await tokenizationTask;

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenInformation.AccessToken);

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
