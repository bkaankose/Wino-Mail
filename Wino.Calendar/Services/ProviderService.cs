using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Calendar.Services;

public class ProviderService : IProviderService
{
    public IProviderDetail GetProviderDetail(MailProviderType type)
    {
        var details = GetAvailableProviders();

        return details.FirstOrDefault(a => a.Type == type);
    }

    public List<IProviderDetail> GetAvailableProviders()
    {
        var providerList = new List<IProviderDetail>();

        var providers = new MailProviderType[]
        {
            MailProviderType.Outlook,
            MailProviderType.Gmail
        };

        foreach (var type in providers)
        {
            providerList.Add(new ProviderDetail(type, SpecialImapProvider.None));
        }

        return providerList;
    }
}
