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

    public AuthenticationProvider(Func<IOutlookAuthenticator> outlookFactory, Func<IGmailAuthenticator> gmailFactory)
    {
        _outlookFactory = outlookFactory;
        _gmailFactory = gmailFactory;
    }

    public IAuthenticator GetAuthenticator(MailProviderType providerType)
    {
        return providerType switch
        {
            MailProviderType.Outlook => _outlookFactory(),
            MailProviderType.Gmail => _gmailFactory(),
            _ => throw new ArgumentException(Translator.Exception_UnsupportedProvider),
        };
    }
}
