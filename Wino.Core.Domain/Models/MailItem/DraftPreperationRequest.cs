using System;
using System.Collections.Generic;
using System.Text;
using MimeKit;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.MailItem
{
    public class DraftPreperationRequest : DraftCreationOptions
    {
        public DraftPreperationRequest(MailAccount account, MailCopy createdLocalDraftCopy, MimeMessage createdLocalDraftMimeMessage)
        {
            Account = account ?? throw new ArgumentNullException(nameof(account));

            CreatedLocalDraftCopy = createdLocalDraftCopy ?? throw new ArgumentNullException(nameof(createdLocalDraftCopy));
            CreatedLocalDraftMimeMessage = createdLocalDraftMimeMessage ?? throw new ArgumentNullException(nameof(createdLocalDraftMimeMessage));
        }

        public MailCopy CreatedLocalDraftCopy { get; set; }
        public MimeMessage CreatedLocalDraftMimeMessage { get; set; }
        public MailAccount Account { get; }
    }
}
