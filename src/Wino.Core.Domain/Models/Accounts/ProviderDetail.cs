using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Models.Accounts;

public class ProviderDetail : IProviderDetail
{
    public MailProviderType Type { get; }
    public SpecialImapProvider SpecialImapProvider { get; }
    public string Name { get; }

    public string Description { get; }

    public bool IsFeatured { get; }

    public IReadOnlyList<string> EmailDomains { get; } = [];

    public string ProviderImage
    {
        get
        {
            if (SpecialImapProvider == SpecialImapProvider.None)
            {
                return Type == MailProviderType.POP3
                    ? "/Assets/Providers/IMAP4.png"
                    : $"/Assets/Providers/{Type}.png";
            }
            else
            {
                return $"/Assets/Providers/{SpecialImapProvider}.png";
            }
        }
    }

    public bool IsSupported => Type is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.POP3;

    /// <summary>
    /// A catalog provider. Its name and domains come from the catalog entry.
    /// </summary>
    public ProviderDetail(KnownImapProviderDefinition definition)
    {
        Type = MailProviderType.IMAP4;
        SpecialImapProvider = definition.SpecialImapProvider;
        Name = string.IsNullOrWhiteSpace(definition.DisplayName)
            ? definition.SpecialImapProvider.ToString()
            : definition.DisplayName;
        EmailDomains = definition.EmailDomains ?? [];
        IsFeatured = definition.SetupFeatured;

        // Domains are the most recognizable summary of a mail brand; providers that host any
        // domain (Migadu, STRATO) describe themselves instead.
        Description = EmailDomains.Count > 0
            ? string.Join(" · ", EmailDomains.Take(4))
            : Translator.ProviderDetail_HostedDomain_Description;
    }

    public ProviderDetail(MailProviderType type, SpecialImapProvider specialImapProvider)
    {
        Type = type;
        SpecialImapProvider = specialImapProvider;

        switch (Type)
        {
            case MailProviderType.Outlook:
                Name = "Outlook";
                Description = "Outlook.com, Live.com, Hotmail, MSN";
                EmailDomains = ["outlook.com", "hotmail.com", "live.com", "msn.com"];
                IsFeatured = true;
                break;
            case MailProviderType.Gmail:
                Name = "Gmail";
                Description = Translator.ProviderDetail_Gmail_Description;
                EmailDomains = ["gmail.com", "googlemail.com"];
                IsFeatured = true;
                break;
            case MailProviderType.IMAP4:
                switch (specialImapProvider)
                {
                    case SpecialImapProvider.None:
                        Name = Translator.ProviderDetail_IMAP_Title;
                        Description = Translator.ProviderDetail_IMAP_Description;
                        break;
                    case SpecialImapProvider.iCloud:
                        Name = Translator.ProviderDetail_iCloud_Title;
                        Description = Translator.ProviderDetail_iCloud_Description;
                        IsFeatured = true;
                        break;
                    case SpecialImapProvider.Yahoo:
                        Name = Translator.ProviderDetail_Yahoo_Title;
                        Description = Translator.ProviderDetail_Yahoo_Description;
                        IsFeatured = true;
                        break;
                    default:
                        // Catalog providers are normally built from their definition; this keeps
                        // an enum-only construction (tests, old callers) from producing a blank tile.
                        Name = specialImapProvider.ToString();
                        Description = string.Empty;
                        break;
                }

                break;
            case MailProviderType.POP3:
                Name = Translator.ProviderDetail_POP3_Title;
                Description = Translator.ProviderDetail_POP3_Description;
                break;
        }
    }

    public bool MatchesSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var trimmed = query.Trim();
        return Name.Contains(trimmed, System.StringComparison.OrdinalIgnoreCase)
            || EmailDomains.Any(domain => domain.Contains(trimmed, System.StringComparison.OrdinalIgnoreCase));
    }
}
