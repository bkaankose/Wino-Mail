using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem
{
    public class SendDraftPreparationRequest
    {
        public MailCopy MailItem { get; set; }
        public string Base64MimeMessage { get; set; }
        public MailItemFolder SentFolder { get; set; }
        public MailItemFolder DraftFolder { get; set; }
        public MailAccountPreferences AccountPreferences { get; set; }

        public SendDraftPreparationRequest(MailCopy mailItem,
                                           MailItemFolder sentFolder,
                                           MailItemFolder draftFolder,
                                           MailAccountPreferences accountPreferences,
                                           string base64MimeMessage)
        {
            MailItem = mailItem;
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
