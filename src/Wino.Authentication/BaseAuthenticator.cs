using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

public abstract class BaseAuthenticator
{
    public abstract MailProviderType ProviderType { get; }
    protected IAuthenticatorConfig AuthenticatorConfig { get; }

    protected BaseAuthenticator(IAuthenticatorConfig authenticatorConfig)
    {

        AuthenticatorConfig = authenticatorConfig;
    }
}
