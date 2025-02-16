using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ThreadingStrategyProvider : IThreadingStrategyProvider
{
    private readonly IOutlookThreadingStrategy _outlookThreadingStrategy;
    private readonly IGmailThreadingStrategy _gmailThreadingStrategy;
    private readonly IImapThreadingStrategy _imapThreadStrategy;

    public ThreadingStrategyProvider(IOutlookThreadingStrategy outlookThreadingStrategy,
                                     IGmailThreadingStrategy gmailThreadingStrategy,
                                     IImapThreadingStrategy imapThreadStrategy)
    {
        _outlookThreadingStrategy = outlookThreadingStrategy;
        _gmailThreadingStrategy = gmailThreadingStrategy;
        _imapThreadStrategy = imapThreadStrategy;
    }

    public IThreadingStrategy GetStrategy(MailProviderType mailProviderType)
    {
        return mailProviderType switch
        {
            MailProviderType.Outlook => _outlookThreadingStrategy,
            MailProviderType.Gmail => _gmailThreadingStrategy,
            _ => _imapThreadStrategy,
        };
    }
}
