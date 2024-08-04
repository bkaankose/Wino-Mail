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
        private readonly IApplicationConfiguration _applicationConfiguration;

        public AuthenticationProvider(INativeAppService nativeAppService, ITokenService tokenService, IApplicationConfiguration applicationConfiguration)
        {
            _nativeAppService = nativeAppService;
            _tokenService = tokenService;
            _applicationConfiguration = applicationConfiguration;
        }

        public IAuthenticator GetAuthenticator(MailProviderType providerType)
        {
            // TODO: Move DI
            return providerType switch
            {
                MailProviderType.Outlook => new OutlookAuthenticator(_tokenService, _nativeAppService, _applicationConfiguration),
                MailProviderType.Office365 => new Office365Authenticator(_tokenService, _nativeAppService, _applicationConfiguration),
                MailProviderType.Gmail => new GmailAuthenticator(_tokenService, _nativeAppService),
                _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
            };
        }
    }
}
