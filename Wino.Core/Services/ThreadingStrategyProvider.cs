using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Threading;

namespace Wino.Core.Services
{
    public class ThreadingStrategyProvider : IThreadingStrategyProvider
    {
        private readonly OutlookThreadingStrategy _outlookThreadingStrategy;
        private readonly GmailThreadingStrategy _gmailThreadingStrategy;
        private readonly ImapThreadingStrategy _imapThreadStrategy;

        public ThreadingStrategyProvider(OutlookThreadingStrategy outlookThreadingStrategy,
                                         GmailThreadingStrategy gmailThreadingStrategy,
                                         ImapThreadingStrategy imapThreadStrategy)
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
}
