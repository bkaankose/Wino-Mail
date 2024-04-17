using System;
using Microsoft.Graph.Models;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Extensions
{
    public static class OutlookIntegratorExtensions
    {
        public static MailItemFolder GetLocalFolder(this MailFolder nativeFolder, Guid accountId)
        {
            return new MailItemFolder()
            {
                Id = Guid.NewGuid(),
                FolderName = nativeFolder.DisplayName,
                RemoteFolderId = nativeFolder.Id,
                ParentRemoteFolderId = nativeFolder.ParentFolderId,
                IsSynchronizationEnabled = true,
                MailAccountId = accountId,
                IsHidden = nativeFolder.IsHidden.GetValueOrDefault()
            };
        }

        public static bool GetIsDraft(this Message message)
            => message != null && message.IsDraft.GetValueOrDefault();

        public static bool GetIsRead(this Message message)
            => message != null && message.IsRead.GetValueOrDefault();

        public static bool GetIsFocused(this Message message)
            => message?.InferenceClassification != null && message.InferenceClassification.Value == InferenceClassificationType.Focused;

        public static bool GetIsFlagged(this Message message)
            => message?.Flag?.FlagStatus != null && message.Flag.FlagStatus == FollowupFlagStatus.Flagged;

        public static MailCopy AsMailCopy(this Message outlookMessage)
        {
            bool isDraft = GetIsDraft(outlookMessage);

            var mailCopy = new MailCopy()
            {
                MessageId = outlookMessage.InternetMessageId,
                IsFlagged = GetIsFlagged(outlookMessage),
                IsFocused = GetIsFocused(outlookMessage),
                Importance = !outlookMessage.Importance.HasValue ? MailImportance.Normal : (MailImportance)outlookMessage.Importance.Value,
                IsRead = GetIsRead(outlookMessage),
                IsDraft = isDraft,
                CreationDate = outlookMessage.ReceivedDateTime.GetValueOrDefault().DateTime,
                HasAttachments = outlookMessage.HasAttachments.GetValueOrDefault(),
                PreviewText = outlookMessage.BodyPreview,
                Id = outlookMessage.Id,
                ThreadId = outlookMessage.ConversationId,
                FromName = outlookMessage.From?.EmailAddress?.Name,
                FromAddress = outlookMessage.From?.EmailAddress?.Address,
                Subject = outlookMessage.Subject,
                FileId = Guid.NewGuid()
            };

            if (mailCopy.IsDraft)
                mailCopy.DraftId = mailCopy.ThreadId;

            return mailCopy;
        }
    }
}
