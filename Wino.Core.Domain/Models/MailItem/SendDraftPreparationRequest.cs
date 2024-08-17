using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem
{
    public record SendDraftPreparationRequest(MailCopy MailItem,
                                              MailAccountAlias SendingAlias,
                                              MailItemFolder SentFolder,
                                              MailItemFolder DraftFolder,
                                              MailAccountPreferences AccountPreferences,
                                              string Base64MimeMessage)
    {
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
