using System;
using Wino.Domain;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using IAuthenticationProvider = Wino.Domain.Interfaces.IAuthenticationProvider;

namespace Wino.Services.Services
{
    public class AuthenticationProvider : IAuthenticationProvider
    {
        private readonly INativeAppService _nativeAppService;
        private readonly ITokenService _tokenService;
        private readonly IOutlookAuthenticator _outlookAuthenticator;
        private readonly IGmailAuthenticator _gmailAuthenticator;

        public AuthenticationProvider(INativeAppService nativeAppService,
                                      ITokenService tokenService,
                                      IOutlookAuthenticator outlookAuthenticator,
                                      IGmailAuthenticator gmailAuthenticator)
        {
            _nativeAppService = nativeAppService;
            _tokenService = tokenService;
            _outlookAuthenticator = outlookAuthenticator;
            _gmailAuthenticator = gmailAuthenticator;
        }

        public IAuthenticator GetAuthenticator(MailProviderType providerType)
        {
            return providerType switch
            {
                MailProviderType.Outlook => _outlookAuthenticator,
                MailProviderType.Office365 => _outlookAuthenticator,
                MailProviderType.Gmail => _gmailAuthenticator,
                _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
            };
        }
    }
}
