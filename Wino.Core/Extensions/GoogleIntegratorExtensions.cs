using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;
using Google.Apis.Gmail.v1.Data;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Extensions
{
    public static class GoogleIntegratorExtensions
    {
        public const string INBOX_LABEL_ID = "INBOX";
        public const string UNREAD_LABEL_ID = "UNREAD";
        public const string IMPORTANT_LABEL_ID = "IMPORTANT";
        public const string STARRED_LABEL_ID = "STARRED";
        public const string DRAFT_LABEL_ID = "DRAFT";
        public const string SENT_LABEL_ID = "SENT";

        private const string SYSTEM_FOLDER_IDENTIFIER = "system";
        private const string FOLDER_HIDE_IDENTIFIER = "labelHide";

        private static Dictionary<string, SpecialFolderType> KnownFolderDictioanry = new Dictionary<string, SpecialFolderType>()
        {
            { INBOX_LABEL_ID, SpecialFolderType.Inbox },
            { "CHAT", SpecialFolderType.Chat },
            { IMPORTANT_LABEL_ID, SpecialFolderType.Important },
            { "TRASH", SpecialFolderType.Deleted },
            { DRAFT_LABEL_ID, SpecialFolderType.Draft },
            { SENT_LABEL_ID, SpecialFolderType.Sent },
            { "SPAM", SpecialFolderType.Junk },
            { STARRED_LABEL_ID, SpecialFolderType.Starred },
            { UNREAD_LABEL_ID, SpecialFolderType.Unread },
            { "FORUMS", SpecialFolderType.Forums },
            { "UPDATES", SpecialFolderType.Updates },
            { "PROMOTIONS", SpecialFolderType.Promotions },
            { "SOCIAL", SpecialFolderType.Social},
            { "PERSONAL", SpecialFolderType.Personal},
        };

        public static MailItemFolder GetLocalFolder(this Label label, Guid accountId)
        {
            var unchangedFolderName = label.Name;

            if (label.Name.StartsWith("CATEGORY_"))
                label.Name = label.Name.Replace("CATEGORY_", "");

            bool isSpecialFolder = KnownFolderDictioanry.ContainsKey(label.Name);
            bool isAllCapital = label.Name.All(a => char.IsUpper(a));

            var specialFolderType = isSpecialFolder ? KnownFolderDictioanry[label.Name] : SpecialFolderType.Other;

            return new MailItemFolder()
            {
                TextColorHex = label.Color?.TextColor,
                BackgroundColorHex = label.Color?.BackgroundColor,
                FolderName = isAllCapital ? char.ToUpper(label.Name[0]) + label.Name.Substring(1).ToLower() : label.Name, // Capitilize only first letter.
                RemoteFolderId = label.Id,
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                IsSynchronizationEnabled = true,
                SpecialFolderType = specialFolderType,
                IsSystemFolder = label.Type == SYSTEM_FOLDER_IDENTIFIER,
                IsSticky = isSpecialFolder && specialFolderType != SpecialFolderType.Category && !unchangedFolderName.StartsWith("CATEGORY"),
                IsHidden = label.LabelListVisibility == FOLDER_HIDE_IDENTIFIER,

                // By default, all special folders update unread count in the UI except Trash.
                ShowUnreadCount = specialFolderType != SpecialFolderType.Deleted || specialFolderType != SpecialFolderType.Other
            };
        }

        public static bool GetIsDraft(this Message message)
            => message?.LabelIds?.Any(a => a == DRAFT_LABEL_ID) ?? false;

        public static bool GetIsUnread(this Message message)
            => message?.LabelIds?.Any(a => a == UNREAD_LABEL_ID) ?? false;

        public static bool GetIsFocused(this Message message)
            => message?.LabelIds?.Any(a => a == IMPORTANT_LABEL_ID) ?? false;

        public static bool GetIsFlagged(this Message message)
            => message?.LabelIds?.Any(a => a == STARRED_LABEL_ID) ?? false;

        /// <summary>
        /// Returns MailCopy out of native Gmail message and converted MimeMessage of that native messaage.
        /// </summary>
        /// <param name="gmailMessage">Gmail Message</param>
        /// <param name="mimeMessage">MimeMessage representation of that native message.</param>
        /// <returns>MailCopy object that is ready to be inserted to database.</returns>
        public static MailCopy AsMailCopy(this Message gmailMessage, MimeMessage mimeMessage)
        {
            bool isUnread = gmailMessage.GetIsUnread();
            bool isFocused = gmailMessage.GetIsFocused();
            bool isFlagged = gmailMessage.GetIsFlagged();
            bool isDraft = gmailMessage.GetIsDraft();

            return new MailCopy()
            {
                CreationDate = mimeMessage.Date.UtcDateTime,
                Subject = HttpUtility.HtmlDecode(mimeMessage.Subject),
                FromName = MailkitClientExtensions.GetActualSenderName(mimeMessage),
                FromAddress = MailkitClientExtensions.GetActualSenderAddress(mimeMessage),
                PreviewText = HttpUtility.HtmlDecode(gmailMessage.Snippet),
                ThreadId = gmailMessage.ThreadId,
                Importance = (MailImportance)mimeMessage.Importance,
                Id = gmailMessage.Id,
                IsDraft = isDraft,
                HasAttachments = mimeMessage.Attachments.Any(),
                IsRead = !isUnread,
                IsFlagged = isFlagged,
                IsFocused = isFocused,
                InReplyTo = mimeMessage.InReplyTo,
                MessageId = mimeMessage.MessageId,
                References = mimeMessage.References.GetReferences(),
                FileId = Guid.NewGuid()
            };
        }

        public static Tuple<MailCopy, MimeMessage, IEnumerable<string>> GetMailDetails(this Message message)
        {
            MimeMessage mimeMessage = message.GetGmailMimeMessage();

            if (mimeMessage == null)
            {
                // This should never happen.
                Debugger.Break();

                return default;
            }

            bool isUnread = message.GetIsUnread();
            bool isFocused = message.GetIsFocused();
            bool isFlagged = message.GetIsFlagged();
            bool isDraft = message.GetIsDraft();

            var mailCopy = new MailCopy()
            {
                CreationDate = mimeMessage.Date.UtcDateTime,
                Subject = HttpUtility.HtmlDecode(mimeMessage.Subject),
                FromName = MailkitClientExtensions.GetActualSenderName(mimeMessage),
                FromAddress = MailkitClientExtensions.GetActualSenderAddress(mimeMessage),
                PreviewText = HttpUtility.HtmlDecode(message.Snippet),
                ThreadId = message.ThreadId,
                Importance = (MailImportance)mimeMessage.Importance,
                Id = message.Id,
                IsDraft = isDraft,
                HasAttachments = mimeMessage.Attachments.Any(),
                IsRead = !isUnread,
                IsFlagged = isFlagged,
                IsFocused = isFocused,
                InReplyTo = mimeMessage.InReplyTo,
                MessageId = mimeMessage.MessageId,
                References = mimeMessage.References.GetReferences()
            };

            return new Tuple<MailCopy, MimeMessage, IEnumerable<string>>(mailCopy, mimeMessage, message.LabelIds);
        }

    }
}
