using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Accounts;

public class ProviderDetail : IProviderDetail
{
    public MailProviderType Type { get; }
    public SpecialImapProvider SpecialImapProvider { get; }
    public string Name { get; }

    public string Description { get; }

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

    public ProviderDetail(MailProviderType type, SpecialImapProvider specialImapProvider)
    {
        Type = type;
        SpecialImapProvider = specialImapProvider;

        switch (Type)
        {
            case MailProviderType.Outlook:
                Name = "Outlook";
                Description = "Outlook.com, Live.com, Hotmail, MSN";
                break;
            case MailProviderType.Gmail:
                Name = "Gmail";
                Description = Translator.ProviderDetail_Gmail_Description;
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
                        break;
                    case SpecialImapProvider.Yahoo:
                        Name = Translator.ProviderDetail_Yahoo_Title;
                        Description = Translator.ProviderDetail_Yahoo_Description;
                        break;
                    default:
                        break;
                }

                break;
            case MailProviderType.POP3:
                Name = Translator.ProviderDetail_POP3_Title;
                Description = Translator.ProviderDetail_POP3_Description;
                break;
        }
    }
}
