using Wino.Domain.Enums;

namespace Wino.Domain.Interfaces
{
    public interface IAuthenticationProvider
    {
        IAuthenticator GetAuthenticator(MailProviderType providerType);
    }
}
