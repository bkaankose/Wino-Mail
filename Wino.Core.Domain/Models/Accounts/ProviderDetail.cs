using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Accounts
{
    public class ProviderDetail : IProviderDetail
    {
        public MailProviderType Type { get; }

        public string Name { get; }

        public string Description { get; }

        public string ProviderImage => $"ms-appx:///Assets/Providers/{Type}.png";

        public bool IsSupported => Type == MailProviderType.Outlook || Type == MailProviderType.Gmail || Type == MailProviderType.IMAP4;

        public ProviderDetail(MailProviderType type)
        {
            Type = type;

            switch (Type)
            {
                case MailProviderType.Outlook:
                    Name = "Outlook";
                    Description = "Outlook.com, Live.com, Hotmail, MSN";
                    break;
                case MailProviderType.Office365:
                    Name = "Office 365";
                    Description = "Office 365, Exchange";
                    break;
                case MailProviderType.Gmail:
                    Name = "Gmail";
                    Description = Translator.ProviderDetail_Gmail_Description;
                    break;
                case MailProviderType.Yahoo:
                    Name = "Yahoo";
                    Description = "Yahoo Mail";
                    break;
                case MailProviderType.IMAP4:
                    Name = Translator.ProviderDetail_IMAP_Title;
                    Description = Translator.ProviderDetail_IMAP_Description;
                    break;
            }
        }
    }
}
