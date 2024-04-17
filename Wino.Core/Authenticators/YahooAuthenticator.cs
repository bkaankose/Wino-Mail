using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class YahooAuthenticator : BaseAuthenticator, IAuthenticator
    {
        public YahooAuthenticator(ITokenService tokenService) : base(tokenService) { }

        public override MailProviderType ProviderType => MailProviderType.Yahoo;

        public string ClientId => throw new NotImplementedException();

        public event EventHandler<string> InteractiveAuthenticationRequired;

        public void CancelAuthorization()
        {
            throw new NotImplementedException();
        }

        public void ContinueAuthorization(Uri authorizationResponseUri)
        {
            throw new NotImplementedException();
        }

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
