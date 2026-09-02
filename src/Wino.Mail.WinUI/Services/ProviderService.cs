using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.Services;

/// <summary>
/// Service that is returning available provider details.
/// </summary>
public class ProviderService(IKnownImapProviderCatalog catalog) : IProviderService
{
    public IProviderDetail GetProviderDetail(MailProviderType type)
    {
        var details = GetAvailableProviders();

        return details.FirstOrDefault(a => a.Type == type) ?? throw new InvalidOperationException($"Provider detail not found for type: {type}");
    }

    public List<IProviderDetail> GetAvailableProviders()
    {
        var providerList = new List<IProviderDetail>
        {
            new ProviderDetail(MailProviderType.Outlook, SpecialImapProvider.None),
            new ProviderDetail(MailProviderType.Gmail, SpecialImapProvider.None)
        };

        providerList.AddRange(catalog.SetupProviders.Select(provider =>
            new ProviderDetail(MailProviderType.IMAP4, provider.SpecialImapProvider)));
        providerList.Add(new ProviderDetail(MailProviderType.IMAP4, SpecialImapProvider.None));
        providerList.Add(new ProviderDetail(MailProviderType.POP3, SpecialImapProvider.None));

        return providerList;
    }
}
