using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IAuthenticationProvider
{
    IAuthenticator GetAuthenticator(MailProviderType providerType);
}
