using System;
using MailCal.Authentication;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using IAuthenticationProvider = MailCal.Core.Domain.Interfaces.IAuthenticationProvider;

namespace MailCal.Core.Services;

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
