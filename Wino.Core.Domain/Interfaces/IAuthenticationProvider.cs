using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAuthenticationProvider
    {
        IAuthenticator GetAuthenticator(MailProviderType providerType);
    }
}
