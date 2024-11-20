using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication
{
    public class Office365Authenticator : OutlookAuthenticator
    {
        public Office365Authenticator(INativeAppService nativeAppService,
                                      IApplicationConfiguration applicationConfiguration,
                                      IAuthenticatorConfig authenticatorConfig) : base(nativeAppService, applicationConfiguration, authenticatorConfig)
        {
        }

        public override MailProviderType ProviderType => MailProviderType.Office365;
    }
}
