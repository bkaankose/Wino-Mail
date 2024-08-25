using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Authenticators
{
    public class Office365Authenticator : OutlookAuthenticator
    {
        public Office365Authenticator(ITokenService tokenService, INativeAppService nativeAppService, IApplicationConfiguration applicationConfiguration) : base(tokenService, nativeAppService, applicationConfiguration) { }

        public override MailProviderType ProviderType => MailProviderType.Office365;
    }
}
