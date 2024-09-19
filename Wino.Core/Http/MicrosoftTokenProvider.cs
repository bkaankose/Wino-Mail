using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kiota.Abstractions.Authentication;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Http
{
    public class MicrosoftTokenProvider : IAccessTokenProvider
    {
        private readonly MailAccount _account;
        private readonly IAuthenticator _authenticator;

        public MicrosoftTokenProvider(MailAccount account, IAuthenticator authenticator)
        {
            _account = account;
            _authenticator = authenticator;
        }

        public AllowedHostsValidator AllowedHostsValidator { get; }

        public async Task<string> GetAuthorizationTokenAsync(Uri uri,
                                                             Dictionary<string, object> additionalAuthenticationContext = null,
                                                             CancellationToken cancellationToken = default)
        {
            var token = await _authenticator.GetTokenAsync(_account).ConfigureAwait(false);

            return token?.AccessToken;
        }
    }
}
