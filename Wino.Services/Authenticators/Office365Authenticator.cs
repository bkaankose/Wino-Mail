using Wino.Domain.Enums;
using Wino.Domain.Interfaces;

namespace Wino.Services.Authenticators
{
    public class Office365Authenticator : OutlookAuthenticator
    {
        public Office365Authenticator(ITokenService tokenService, INativeAppService nativeAppService) : base(tokenService, nativeAppService) { }

        public override MailProviderType ProviderType => MailProviderType.Office365;
    }
}
