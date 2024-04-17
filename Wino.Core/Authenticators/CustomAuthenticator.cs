using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class CustomAuthenticator : BaseAuthenticator, IAuthenticator
    {
        public CustomAuthenticator(ITokenService tokenService) : base(tokenService) { }

        public override MailProviderType ProviderType => MailProviderType.IMAP4;

        public string ClientId => throw new NotImplementedException(); // Not needed.

        public event EventHandler<string> InteractiveAuthenticationRequired;

        public void CancelAuthorization() { }

        public void ContinueAuthorization(Uri authorizationResponseUri) { }

        public Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken)
        {
            throw new NotImplementedException();
        }

        public Task<TokenInformation> GetTokenAsync(MailAccount account)
        {
            throw new NotImplementedException();
        }
    }
}
