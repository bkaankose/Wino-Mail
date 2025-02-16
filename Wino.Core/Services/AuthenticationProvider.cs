using System;
using Wino.Authentication;
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

    public AuthenticationProvider(INativeAppService nativeAppService,
                                  IApplicationConfiguration applicationConfiguration,
                                  IAuthenticatorConfig authenticatorConfig)
    {
        _nativeAppService = nativeAppService;
        _applicationConfiguration = applicationConfiguration;
        _authenticatorConfig = authenticatorConfig;
    }

    public IAuthenticator GetAuthenticator(MailProviderType providerType)
    {
        // TODO: Move DI
        return providerType switch
        {
            MailProviderType.Outlook => new OutlookAuthenticator(_nativeAppService, _applicationConfiguration, _authenticatorConfig),
            MailProviderType.Gmail => new GmailAuthenticator(_authenticatorConfig),
            _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
        };
    }
}
