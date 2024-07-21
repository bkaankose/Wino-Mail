using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using Google.Apis.Gmail.v1.Data;
using MimeKit;
using MimeKit.IO;
using MimeKit.IO.Filters;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Constants = Wino.Domain.Constants;

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
        public const string SPAM_LABEL_ID = "SPAM";
        public const string CHAT_LABEL_ID = "CHAT";
        public const string TRASH_LABEL_ID = "TRASH";



        // Label visibility identifiers.
        private const string SYSTEM_FOLDER_IDENTIFIER = "system";
        private const string FOLDER_HIDE_IDENTIFIER = "labelHide";

        private const string CATEGORY_PREFIX = "CATEGORY_";
        private const string FOLDER_SEPERATOR_STRING = "/";
        private const char FOLDER_SEPERATOR_CHAR = '/';

        private static Dictionary<string, SpecialFolderType> KnownFolderDictionary = new Dictionary<string, SpecialFolderType>()
        {
            { INBOX_LABEL_ID, SpecialFolderType.Inbox },
            { CHAT_LABEL_ID, SpecialFolderType.Chat },
            { IMPORTANT_LABEL_ID, SpecialFolderType.Important },
            { TRASH_LABEL_ID, SpecialFolderType.Deleted },
            { DRAFT_LABEL_ID, SpecialFolderType.Draft },
            { SENT_LABEL_ID, SpecialFolderType.Sent },
            { SPAM_LABEL_ID, SpecialFolderType.Junk },
            { STARRED_LABEL_ID, SpecialFolderType.Starred },
            { UNREAD_LABEL_ID, SpecialFolderType.Unread },
            { Constants.FORUMS_LABEL_ID, SpecialFolderType.Forums },
            { Constants.UPDATES_LABEL_ID, SpecialFolderType.Updates },
            { Constants.PROMOTIONS_LABEL_ID, SpecialFolderType.Promotions },
            { Constants.SOCIAL_LABEL_ID, SpecialFolderType.Social},
            { Constants.PERSONAL_LABEL_ID, SpecialFolderType.Personal},
        };

        private static string GetNormalizedLabelName(string labelName)
        {
            // 1. Remove CATEGORY_ prefix.
            var normalizedLabelName = labelName.Replace(CATEGORY_PREFIX, string.Empty);

            // 2. Normalize label name by capitalizing first letter.
            normalizedLabelName = char.ToUpper(normalizedLabelName[0]) + normalizedLabelName.Substring(1).ToLower();

            return normalizedLabelName;
        }

        public static MailItemFolder GetLocalFolder(this Label label, ListLabelsResponse labelsResponse, Guid accountId)
        {
            bool isAllCapital = label.Name.All(a => char.IsUpper(a));

            var normalizedLabelName = GetFolderName(label);

            // Even though we normalize the label name, check is done by capitalizing the label name.
            var capitalNormalizedLabelName = normalizedLabelName.ToUpper();

            bool isSpecialFolder = KnownFolderDictionary.ContainsKey(capitalNormalizedLabelName);

            var specialFolderType = isSpecialFolder ? KnownFolderDictionary[capitalNormalizedLabelName] : SpecialFolderType.Other;

            // We used to support FOLDER_HIDE_IDENTIFIER to hide invisible folders.
            // However, a lot of people complained that they don't see their folders after the initial sync
            // without realizing that they are hidden in Gmail settings. Therefore, it makes more sense to ignore Gmail's configuration
            // since Wino allows folder visibility configuration separately.

            // Overridden hidden labels are shown in the UI, but they have their synchronization disabled.
            // This is mainly because 'All Mails' label is hidden by default in Gmail, but there is no point to download all mails.

            bool shouldEnableSynchronization = label.LabelListVisibility != FOLDER_HIDE_IDENTIFIER;
            bool isHidden = false;

            bool isChildOfCategoryFolder = label.Name.StartsWith(CATEGORY_PREFIX);
            bool isSticky = isSpecialFolder && specialFolderType != SpecialFolderType.Category && !isChildOfCategoryFolder;

            // By default, all special folders update unread count in the UI except Trash.
            bool shouldShowUnreadCount = specialFolderType != SpecialFolderType.Deleted || specialFolderType != SpecialFolderType.Other;

            bool isSystemFolder = label.Type == SYSTEM_FOLDER_IDENTIFIER;

            var localFolder = new MailItemFolder()
            {
                TextColorHex = label.Color?.TextColor,
                BackgroundColorHex = label.Color?.BackgroundColor,
                FolderName = normalizedLabelName,
                RemoteFolderId = label.Id,
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                IsSynchronizationEnabled = shouldEnableSynchronization,
                SpecialFolderType = specialFolderType,
                IsSystemFolder = isSystemFolder,
                IsSticky = isSticky,
                IsHidden = isHidden,
                ShowUnreadCount = shouldShowUnreadCount,
            };

            localFolder.ParentRemoteFolderId = isChildOfCategoryFolder ? string.Empty : GetParentFolderRemoteId(label.Name, labelsResponse);

            return localFolder;
        }

        public static bool GetIsDraft(this Message message)
            => message?.LabelIds?.Any(a => a == DRAFT_LABEL_ID) ?? false;

        public static bool GetIsUnread(this Message message)
            => message?.LabelIds?.Any(a => a == UNREAD_LABEL_ID) ?? false;

        public static bool GetIsFocused(this Message message)
            => message?.LabelIds?.Any(a => a == IMPORTANT_LABEL_ID) ?? false;

        public static bool GetIsFlagged(this Message message)
            => message?.LabelIds?.Any(a => a == STARRED_LABEL_ID) ?? false;

        private static string GetParentFolderRemoteId(string fullLabelName, ListLabelsResponse labelsResponse)
        {
            if (string.IsNullOrEmpty(fullLabelName)) return string.Empty;

            // Find the last index of '/'
            int lastIndex = fullLabelName.LastIndexOf('/');

            // If '/' not found or it's at the start, return the empty string.
            if (lastIndex <= 0) return string.Empty;

            // Extract the parent label
            var parentLabelName = fullLabelName.Substring(0, lastIndex);

            return labelsResponse.Labels.FirstOrDefault(a => a.Name == parentLabelName)?.Id ?? string.Empty;
        }

        public static string GetFolderName(Label label)
        {
            if (string.IsNullOrEmpty(label.Name)) return string.Empty;

            // Folders with "//" at the end has "/" as the name.
            if (label.Name.EndsWith(FOLDER_SEPERATOR_STRING)) return FOLDER_SEPERATOR_STRING;

            string[] parts = label.Name.Split(FOLDER_SEPERATOR_CHAR);

            var lastPart = parts[parts.Length - 1];

            return GetNormalizedLabelName(lastPart);
        }

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

        /// <summary>
        /// Returns MimeKit.MimeMessage instance for this GMail Message's Raw content.
        /// </summary>
        /// <param name="message">GMail message.</param>
        public static MimeMessage GetGmailMimeMessage(this Message message)
        {
            if (message == null || message.Raw == null)
                return null;

            // Gmail raw is not base64 but base64Safe. We need to remove this HTML things.
            var base64Encoded = message.Raw.Replace(",", "=").Replace("-", "+").Replace("_", "/");

            byte[] bytes = Encoding.ASCII.GetBytes(base64Encoded);

            var stream = new MemoryStream(bytes);

            // This method will dispose outer stream.

            using (stream)
            {
                using var filtered = new FilteredStream(stream);
                filtered.Add(DecoderFilter.Create(ContentEncoding.Base64));

                return MimeMessage.Load(filtered);
            }
        }
    }
}
