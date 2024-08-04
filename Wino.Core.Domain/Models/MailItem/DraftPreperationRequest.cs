using System;
using System.Text.Json.Serialization;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.MailItem
{
    public class DraftPreperationRequest : DraftCreationOptions
    {
        public DraftPreperationRequest(MailAccount account, MailCopy createdLocalDraftCopy, string base64EncodedMimeMessage)
        {
            Account = account ?? throw new ArgumentNullException(nameof(account));

            CreatedLocalDraftCopy = createdLocalDraftCopy ?? throw new ArgumentNullException(nameof(createdLocalDraftCopy));

            // MimeMessage is not serializable with System.Text.Json. Convert to base64 string.
            // This is additional work when deserialization needed, but not much to do atm.

            Base64LocalDraftMimeMessage = base64EncodedMimeMessage;
        }

        [JsonConstructor]
        private DraftPreperationRequest() { }

        public MailCopy CreatedLocalDraftCopy { get; set; }

        public string Base64LocalDraftMimeMessage { get; set; }

        [JsonIgnore]
        private MimeMessage createdLocalDraftMimeMessage;

        [JsonIgnore]
        public MimeMessage CreatedLocalDraftMimeMessage
        {
            get
            {
                if (createdLocalDraftMimeMessage == null)
                {
                    createdLocalDraftMimeMessage = Base64LocalDraftMimeMessage.GetMimeMessageFromBase64();
                }

                return createdLocalDraftMimeMessage;
            }
        }

        public MailAccount Account { get; }
    }
}
