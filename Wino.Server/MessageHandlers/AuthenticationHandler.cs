using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class AuthenticationHandler : ServerMessageHandler<AuthorizationRequested, TokenInformation>
    {
        private readonly IAuthenticationProvider _authenticationProvider;

        public override TokenInformation FailureDefaultResponse(Exception ex) => null;

        public AuthenticationHandler(IAuthenticationProvider authenticationProvider)
        {
            _authenticationProvider = authenticationProvider;
        }

        protected override async Task<TokenInformation> HandleAsync(AuthorizationRequested message, CancellationToken cancellationToken = default)
        {
            var authenticator = _authenticationProvider.GetAuthenticator(message.MailProviderType);

            return await authenticator.GenerateTokenAsync(message.CreatedAccount, true);
        }
    }
}
