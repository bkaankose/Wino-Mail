using System;
using System.Collections.Generic;
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
        public const string SPAM_LABEL_ID = "SPAM";
        public const string CHAT_LABEL_ID = "CHAT";
        public const string TRASH_LABEL_ID = "TRASH";

        // Category labels.
        public const string FORUMS_LABEL_ID = "FORUMS";
        public const string UPDATES_LABEL_ID = "UPDATES";
        public const string PROMOTIONS_LABEL_ID = "PROMOTIONS";
        public const string SOCIAL_LABEL_ID = "SOCIAL";
        public const string PERSONAL_LABEL_ID = "PERSONAL";

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
            { FORUMS_LABEL_ID, SpecialFolderType.Forums },
            { UPDATES_LABEL_ID, SpecialFolderType.Updates },
            { PROMOTIONS_LABEL_ID, SpecialFolderType.Promotions },
            { SOCIAL_LABEL_ID, SpecialFolderType.Social},
            { PERSONAL_LABEL_ID, SpecialFolderType.Personal},
        };

        public static string[] SubCategoryFolderLabelIds =
        [
            FORUMS_LABEL_ID,
            UPDATES_LABEL_ID,
            PROMOTIONS_LABEL_ID,
            SOCIAL_LABEL_ID,
            PERSONAL_LABEL_ID
        ];

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


            var normalizedLabelName = GetFolderName(label);

            // Even though we normalize the label name, check is done by capitalizing the label name.
            var capitalNormalizedLabelName = normalizedLabelName.ToUpper();

            bool isSpecialFolder = KnownFolderDictionary.ContainsKey(capitalNormalizedLabelName);

            var specialFolderType = isSpecialFolder ? KnownFolderDictionary[capitalNormalizedLabelName] : SpecialFolderType.Other;

            // We used to support FOLDER_HIDE_IDENTIFIER to hide invisible folders.
            // However, a lot of people complained that they don't see their folders after the initial sync
            // without realizing that they are hidden in Gmail settings. Therefore, it makes more sense to ignore Gmail's configuration
            // since Wino allows folder visibility configuration separately.

            // Overridden hidden labels are shown in the UI.
            // Also Gmail does not support folder sync enable/disable options due to history changes.
            // By default all folders will be enabled for synchronization.

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
                IsSynchronizationEnabled = true,
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

        public static List<RemoteAccountAlias> GetRemoteAliases(this ListSendAsResponse response)
        {
            return response?.SendAs?.Select(a => new RemoteAccountAlias()
            {
                AliasAddress = a.SendAsEmail,
                IsRootAlias = a.IsDefault.GetValueOrDefault(),
                IsPrimary = a.IsPrimary.GetValueOrDefault(),
                ReplyToAddress = a.ReplyToAddress,
                IsVerified = a.VerificationStatus == "accepted" || a.IsDefault.GetValueOrDefault(),
            }).ToList();
        }
    }
}
