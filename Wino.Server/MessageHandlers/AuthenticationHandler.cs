using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class AuthenticationHandler : ServerMessageHandler<AuthorizationRequested, TokenInformationEx>
    {
        private readonly IAuthenticationProvider _authenticationProvider;

        public override WinoServerResponse<TokenInformationEx> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<TokenInformationEx>.CreateErrorResponse(ex.Message);

        public AuthenticationHandler(IAuthenticationProvider authenticationProvider)
        {
            _authenticationProvider = authenticationProvider;
        }

        protected override async Task<WinoServerResponse<TokenInformationEx>> HandleAsync(AuthorizationRequested message,
                                                                                        CancellationToken cancellationToken = default)
        {
            var authenticator = _authenticationProvider.GetAuthenticator(message.MailProviderType);

            // Some users are having issues with Gmail authentication.
            // Their browsers may never launch to complete authentication.
            // Offer to copy auth url for them to complete it manually.
            // Redirection will occur to the app and the token will be saved.

            if (message.ProposeCopyAuthorizationURL && authenticator is IGmailAuthenticator gmailAuthenticator)
            {
                gmailAuthenticator.ProposeCopyAuthURL = true;
            }

            TokenInformationEx generatedToken = null;

            if (message.CreatedAccount != null)
            {
                generatedToken = await authenticator.GetTokenInformationAsync(message.CreatedAccount);
            }
            else
            {
                // Initial authentication request.
                // There is no account to get token for.

                generatedToken = await authenticator.GenerateTokenInformationAsync(message.CreatedAccount);
            }

            return WinoServerResponse<TokenInformationEx>.CreateSuccessResponse(generatedToken);
        }
    }
}
