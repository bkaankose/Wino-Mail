using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Services
{
    /// <summary>
    /// Service that is returning available provider details.
    /// </summary>
    public class ProviderService : IProviderService
    {
        public IProviderDetail GetProviderDetail(MailProviderType type)
        {
            var details = GetProviderDetails();

            return details.FirstOrDefault(a => a.Type == type);
        }

        public List<IProviderDetail> GetProviderDetails()
        {
            var providerList = new List<IProviderDetail>();

            var providers = new MailProviderType[]
            {
                MailProviderType.Outlook,
                MailProviderType.Gmail,
                MailProviderType.IMAP4
            };

            foreach (var type in providers)
            {
                providerList.Add(new ProviderDetail(type));
            }

            return providerList;
        }
    }
}
