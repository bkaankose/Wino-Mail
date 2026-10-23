using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Authentication;

public abstract class BaseAuthenticator
{
    public abstract MailProviderType ProviderType { get; }
    protected IAuthenticatorConfig AuthenticatorConfig { get; }

    protected BaseAuthenticator(IAuthenticatorConfig authenticatorConfig)
    {

        AuthenticatorConfig = authenticatorConfig;
    }
}
