using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Threading;

namespace Wino.Core.Services
{
    public class ThreadingStrategyProvider : IThreadingStrategyProvider
    {
        private readonly OutlookThreadingStrategy _outlookThreadingStrategy;
        private readonly GmailThreadingStrategy _gmailThreadingStrategy;
        private readonly ImapThreadStrategy _imapThreadStrategy;

        public ThreadingStrategyProvider(OutlookThreadingStrategy outlookThreadingStrategy,
                                         GmailThreadingStrategy gmailThreadingStrategy,
                                         ImapThreadStrategy imapThreadStrategy)
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
