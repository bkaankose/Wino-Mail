using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Server;
using Wino.Messaging.Server;
using Wino.Server.Core;

namespace Wino.Server.MessageHandlers
{
    public class AuthenticationHandler : ServerMessageHandler<AuthorizationRequested, TokenInformation>
    {
        private readonly IAuthenticationProvider _authenticationProvider;

        public override WinoServerResponse<TokenInformation> FailureDefaultResponse(Exception ex)
            => WinoServerResponse<TokenInformation>.CreateErrorResponse(ex.Message);

        public AuthenticationHandler(IAuthenticationProvider authenticationProvider)
        {
            _authenticationProvider = authenticationProvider;
        }

        protected override async Task<WinoServerResponse<TokenInformation>> HandleAsync(AuthorizationRequested message,
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

            // Do not save the token here. Call is coming from account creation and things are atomic there.
            var generatedToken = await authenticator.GenerateTokenAsync(message.CreatedAccount, saveToken: false);

            return WinoServerResponse<TokenInformation>.CreateSuccessResponse(generatedToken);
        }
    }
}
