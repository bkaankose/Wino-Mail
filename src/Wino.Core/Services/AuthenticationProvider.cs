using System;
using Wino.Authentication;
using Wino.Authentication.Exchange;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using IAuthenticationProvider = Wino.Core.Domain.Interfaces.IAuthenticationProvider;

namespace Wino.Core.Services;

public class AuthenticationProvider : IAuthenticationProvider
{
    private readonly INativeAppService _nativeAppService;
    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IAuthenticatorConfig _authenticatorConfig;
    private readonly IExchangeAuthenticator _exchangeAuthenticator;

    public AuthenticationProvider(INativeAppService nativeAppService,
                                  IApplicationConfiguration applicationConfiguration,
                                  IAuthenticatorConfig authenticatorConfig,
                                  IExchangeAuthenticator exchangeAuthenticator)
    {
        _nativeAppService = nativeAppService;
        _applicationConfiguration = applicationConfiguration;
        _authenticatorConfig = authenticatorConfig;
        _exchangeAuthenticator = exchangeAuthenticator;
    }

    public IAuthenticator GetAuthenticator(MailProviderType providerType)
    {
        // TODO: Move DI
        return providerType switch
        {
            MailProviderType.Outlook => new OutlookAuthenticator(_nativeAppService, _applicationConfiguration, _authenticatorConfig),
            MailProviderType.Gmail => new GmailAuthenticator(_authenticatorConfig, _nativeAppService),
            MailProviderType.Exchange => _exchangeAuthenticator,
            _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
        };
    }
}
