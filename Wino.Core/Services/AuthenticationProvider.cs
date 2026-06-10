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
            MailProviderType.Gmail => new GmailAuthenticator(_authenticatorConfig, _applicationConfiguration),
            _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
        };
    }

    public void SetInteractiveAuthorizationWindow(long parentWindowHandle)
    {
        // Authenticators read the parent window through INativeAppService at construction
        // time (GetAuthenticator builds a fresh instance per call). In the headless
        // companion the handle comes from the UI process over RPC; WAM's broker runs
        // out-of-process, so a cross-process HWND is a valid parent.
        _nativeAppService.GetCoreWindowHwnd = parentWindowHandle == 0
            ? null
            : () => new IntPtr(parentWindowHandle);
    }
}
