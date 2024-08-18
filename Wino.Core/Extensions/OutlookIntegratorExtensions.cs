using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Graph.Models;
using MimeKit;
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

        public static Message AsOutlookMessage(this MimeMessage mime, string threadId)
        {
            var fromAddress = GetRecipients(mime.From).ElementAt(0);
            var toAddresses = GetRecipients(mime.To).ToList();
            var ccAddresses = GetRecipients(mime.Cc).ToList();
            var bccAddresses = GetRecipients(mime.Bcc).ToList();
            var replyToAddresses = GetRecipients(mime.ReplyTo).ToList();

            var message = new Message()
            {
                Subject = mime.Subject,
                Importance = GetImportance(mime.Importance),
                Body = new ItemBody() { ContentType = BodyType.Html, Content = mime.HtmlBody },
                IsDraft = false,
                IsRead = true, // Sent messages are always read.
                ToRecipients = toAddresses,
                CcRecipients = ccAddresses,
                BccRecipients = bccAddresses,
                From = fromAddress,
                InternetMessageId = GetMessageIdHeader(mime.MessageId),
                ConversationId = threadId,
                InternetMessageHeaders = GetHeaderList(mime),
                ReplyTo = replyToAddresses,
                Attachments = []
            };

            foreach (var part in mime.BodyParts)
            {
                if (part.IsAttachment)
                {
                    // File attachment.

                    using var memory = new MemoryStream();
                    ((MimePart)part).Content.DecodeTo(memory);

                    var bytes = memory.ToArray();

                    var fileAttachment = new FileAttachment()
                    {
                        ContentId = part.ContentId,
                        Name = part.ContentDisposition?.FileName ?? part.ContentType.Name,
                        ContentBytes = bytes,
                    };

                    message.Attachments.Add(fileAttachment);
                }
                else if (part.ContentDisposition != null && part.ContentDisposition.Disposition == "inline")
                {
                    // Inline attachment.

                    using var memory = new MemoryStream();
                    ((MimePart)part).Content.DecodeTo(memory);

                    var bytes = memory.ToArray();
                    var inlineAttachment = new FileAttachment()
                    {
                        IsInline = true,
                        ContentId = part.ContentId,
                        Name = part.ContentDisposition?.FileName ?? part.ContentType.Name,
                        ContentBytes = bytes
                    };

                    message.Attachments.Add(inlineAttachment);
                }
            }

            return message;
        }

        #region Mime to Outlook Message Helpers

        private static IEnumerable<Recipient> GetRecipients(this InternetAddressList internetAddresses)
        {
            foreach (var address in internetAddresses)
            {
                if (address is MailboxAddress mailboxAddress)
                    yield return new Recipient() { EmailAddress = new EmailAddress() { Address = mailboxAddress.Address, Name = mailboxAddress.Name } };
                else if (address is GroupAddress groupAddress)
                {
                    // TODO: Group addresses are not directly supported.
                    // It'll be individually added.

                    foreach (var mailbox in groupAddress.Members)
                        if (mailbox is MailboxAddress groupMemberMailAddress)
                            yield return new Recipient() { EmailAddress = new EmailAddress() { Address = groupMemberMailAddress.Address, Name = groupMemberMailAddress.Name } };
                }
            }
        }

        private static Importance? GetImportance(MessageImportance importance)
        {
            return importance switch
            {
                MessageImportance.Low => Importance.Low,
                MessageImportance.Normal => Importance.Normal,
                MessageImportance.High => Importance.High,
                _ => null
            };
        }

        private static List<InternetMessageHeader> GetHeaderList(this MimeMessage mime)
        {
            // Graph API only allows max of 5 headers.
            // Here we'll try to ignore some headers that are not neccessary.
            // Outlook API will generate them automatically.

            string[] headersToIgnore = ["Date", "To", "MIME-Version", "From", "Subject", "Message-Id"];

            var headers = new List<InternetMessageHeader>();

            int includedHeaderCount = 0;

            foreach (var header in mime.Headers)
            {
                if (!headersToIgnore.Contains(header.Field))
                {
                    headers.Add(new InternetMessageHeader() { Name = header.Field, Value = header.Value });
                    includedHeaderCount++;
                }

                if (includedHeaderCount >= 5) break;
            }

            return headers;
        }

        private static string GetMessageIdHeader(string messageId)
        {
            // Message-Id header must always start with "X-" or "x-".
            if (string.IsNullOrEmpty(messageId)) return string.Empty;

            if (!messageId.StartsWith("x-") || !messageId.StartsWith("X-"))
                return $"X-{messageId}";

            return messageId;
        }
        #endregion
    }
}
