using System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

public class AuthenticationProvider : IAuthenticationProvider
{
    private readonly IMailProviderRegistry _providers;

    public AuthenticationProvider(IMailProviderRegistry providers)
    {
        _providers = providers;
    }

    public IAuthenticator GetAuthenticator(MailProviderType providerType)
        => _providers.Find(providerType)?.CreateAuthenticator()
           ?? throw new ArgumentException(Translator.Exception_UnsupportedProvider);
}
