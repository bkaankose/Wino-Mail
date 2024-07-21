using Wino.Domain.Enums;
using Wino.Domain.Interfaces;

namespace Wino.Services.Services
{
    public class ThreadingStrategyProvider : IThreadingStrategyProvider
    {
        private readonly IOutlookThreadingStrategy _outlookThreadingStrategy;
        private readonly IGmailThreadingStrategy _gmailThreadingStrategy;
        private readonly IImapThreadStrategy _imapThreadStrategy;

        public ThreadingStrategyProvider(IOutlookThreadingStrategy outlookThreadingStrategy,
                                         IGmailThreadingStrategy gmailThreadingStrategy,
                                         IImapThreadStrategy imapThreadStrategy)
        {
            _outlookThreadingStrategy = outlookThreadingStrategy;
            _gmailThreadingStrategy = gmailThreadingStrategy;
            _imapThreadStrategy = imapThreadStrategy;
        }

        public IThreadingStrategy GetStrategy(MailProviderType mailProviderType)
        {
            return mailProviderType switch
            {
                MailProviderType.Outlook or MailProviderType.Office365 => _outlookThreadingStrategy,
                MailProviderType.Gmail => _gmailThreadingStrategy,
                _ => _imapThreadStrategy,
            };
        }
    }
}
