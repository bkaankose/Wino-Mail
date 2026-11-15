using System;
using Wino.Authentication;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using IAuthenticationProvider = Wino.Core.Domain.Interfaces.IAuthenticationProvider;

namespace Wino.Core.Services;

public class AuthenticationProvider : IAuthenticationProvider
{
    private readonly Func<IOutlookAuthenticator> _outlookFactory;
    private readonly Func<IGmailAuthenticator> _gmailFactory;
    private readonly Func<IExchangeAuthenticator> _exchangeFactory;

    public AuthenticationProvider(
        Func<IOutlookAuthenticator> outlookFactory,
        Func<IGmailAuthenticator> gmailFactory,
        Func<IExchangeAuthenticator> exchangeFactory)
    {
        _outlookFactory = outlookFactory;
        _gmailFactory = gmailFactory;
        _exchangeFactory = exchangeFactory;
    }

    public IAuthenticator GetAuthenticator(MailProviderType providerType)
    {
        return providerType switch
        {
            MailProviderType.Outlook => _outlookFactory(),
            MailProviderType.Gmail => _gmailFactory(),
            MailProviderType.Exchange => _exchangeFactory(),
            _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
        };
    }
}
