using System;
using Wino.Core.Authenticators;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using IAuthenticationProvider = Wino.Core.Domain.Interfaces.IAuthenticationProvider;

namespace Wino.Core.Services
{
    public class AuthenticationProvider : IAuthenticationProvider
    {
        private readonly INativeAppService _nativeAppService;
        private readonly ITokenService _tokenService;

        public AuthenticationProvider(INativeAppService nativeAppService, ITokenService tokenService)
        {
            _nativeAppService = nativeAppService;
            _tokenService = tokenService;
        }

        public IAuthenticator GetAuthenticator(MailProviderType providerType)
        {
            return providerType switch
            {
                MailProviderType.Outlook => new OutlookAuthenticator(_tokenService, _nativeAppService),
                MailProviderType.Office365 => new Office365Authenticator(_tokenService, _nativeAppService),
                MailProviderType.Gmail => new GmailAuthenticator(_tokenService, _nativeAppService),
                MailProviderType.Yahoo => new YahooAuthenticator(_tokenService),
                MailProviderType.IMAP4 => new CustomAuthenticator(_tokenService),
                _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
            };
        }
    }
}
