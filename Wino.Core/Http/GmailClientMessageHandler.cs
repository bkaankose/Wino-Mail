using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Http;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Http
{
    internal class GmailClientMessageHandler : ConfigurableMessageHandler
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
            // This call here will automatically trigger Google Auth's interactive login if the token is not found.
            // or refresh the token based on the FileDataStore.

            var tokenInformation = await _gmailAuthenticator.GetTokenInformationAsync(_mailAccount);

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenInformation.AccessToken);

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
