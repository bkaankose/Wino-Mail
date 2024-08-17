using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem
{
    public class SendDraftPreparationRequest
    {
        public MailCopy MailItem { get; }
        public string Base64MimeMessage { get; }
        public MailItemFolder SentFolder { get; }
        public MailItemFolder DraftFolder { get; }
        public MailAccountPreferences AccountPreferences { get; }
        public MailAccountAlias SendingAlias { get; set; }

        public SendDraftPreparationRequest(MailCopy mailItem,
                                           MailAccountAlias sendingAlias,
                                           MailItemFolder sentFolder,
                                           MailItemFolder draftFolder,
                                           MailAccountPreferences accountPreferences,
                                           string base64MimeMessage)
        {
            MailItem = mailItem;
            SendingAlias = sendingAlias;
            SentFolder = sentFolder;
            DraftFolder = draftFolder;
            AccountPreferences = accountPreferences;
            Base64MimeMessage = base64MimeMessage;
        }

        [JsonConstructor]
        private SendDraftPreparationRequest() { }

        [JsonIgnore]
        private MimeMessage mime;

        [JsonIgnore]
        public MimeMessage Mime
        {
            get
            {
                if (mime == null)
                {
                    mime = Base64MimeMessage.GetMimeMessageFromBase64();
                }

                return mime;
            }
        }
    }
}
