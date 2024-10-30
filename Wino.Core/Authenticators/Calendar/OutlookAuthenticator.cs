using System;
using System.Threading.Tasks;
using Wino.Core.Authenticators.Base;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Services;

namespace Wino.Core.Authenticators.Calendar
{
    public class OutlookAuthenticator : OutlookAuthenticatorBase
    {
        public OutlookAuthenticator(ITokenService tokenService) : base(tokenService)
        {
        }

        public override string ClientId => throw new NotImplementedException();

        public override MailProviderType ProviderType => MailProviderType.Outlook;

        public override Task<TokenInformation> GenerateTokenAsync(MailAccount account, bool saveToken)
        {
            throw new NotImplementedException();
        }

        public override Task<TokenInformation> GetTokenAsync(MailAccount account)
        {
            throw new NotImplementedException();
        }
    }
}
