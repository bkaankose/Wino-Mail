using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.Services
{
    /// <summary>
    /// Service that is returning available provider details.
    /// </summary>
    public class ProviderService : IProviderService
    {
        public IProviderDetail GetProviderDetail(MailProviderType type)
        {
            var details = GetAvailableProviders();

            return details.FirstOrDefault(a => a.Type == type);
        }

        public List<IProviderDetail> GetAvailableProviders()
        {
            var providerList = new List<IProviderDetail>
            {
                new ProviderDetail(MailProviderType.Outlook, SpecialImapProvider.None),
                new ProviderDetail(MailProviderType.Gmail, SpecialImapProvider.None),
                new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.iCloud),
                new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.Yahoo),
                new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.None)
            };

            return providerList;
        }
    }
}
