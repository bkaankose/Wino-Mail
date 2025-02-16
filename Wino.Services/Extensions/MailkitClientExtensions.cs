using System;
using System.Linq;
using MailKit;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Services.Extensions
{
    public static class MailkitClientExtensions
    {
        public static char MailCopyUidSeparator = '_';

        public static uint ResolveUid(string mailCopyId)
        {
            var splitted = mailCopyId.Split(MailCopyUidSeparator);

            if (splitted.Length > 1 && uint.TryParse(splitted[1], out uint parsedUint)) return parsedUint;

            throw new ArgumentOutOfRangeException(nameof(mailCopyId), mailCopyId, "Invalid mailCopyId format.");
        }

        public static UniqueId ResolveUidStruct(string mailCopyId)
            => new UniqueId(ResolveUid(mailCopyId));

        public static string CreateUid(Guid folderId, uint messageUid)
            => $"{folderId}{MailCopyUidSeparator}{messageUid}";

        public static MailImportance GetImportance(this MimeMessage messageSummary)
        {
            if (messageSummary.Headers != null && messageSummary.Headers.Contains(HeaderId.Importance))
            {
                var rawImportance = messageSummary.Headers[HeaderId.Importance];

                return rawImportance switch
                {
                    "Low" => MailImportance.Low,
                    "High" => MailImportance.High,
                    _ => MailImportance.Normal,
                };
            }

            return MailImportance.Normal;
        }

        public static bool GetIsRead(this MessageFlags? flags)
            => flags.GetValueOrDefault().HasFlag(MessageFlags.Seen);

        public static bool GetIsFlagged(this MessageFlags? flags)
            => flags.GetValueOrDefault().HasFlag(MessageFlags.Flagged);

        public static string GetThreadId(this IMessageSummary messageSummary)
        {
            // First check whether we have the default values.

            if (!string.IsNullOrEmpty(messageSummary.ThreadId))
                return messageSummary.ThreadId;

            if (messageSummary.GMailThreadId != null)
                return messageSummary.GMailThreadId.ToString();

            return default;
        }

        public static string GetMessageId(this MimeMessage mimeMessage)
            => mimeMessage.MessageId;

        public static string GetReferences(this MessageIdList messageIdList)
            => string.Join(";", messageIdList);

        public static string GetInReplyTo(this MimeMessage mimeMessage)
        {
            if (mimeMessage.Headers.Contains(HeaderId.InReplyTo))
            {
                // Normalize if <> brackets are there.
                var inReplyTo = mimeMessage.Headers[HeaderId.InReplyTo];

                if (inReplyTo.StartsWith("<") && inReplyTo.EndsWith(">"))
                    return inReplyTo.Substring(1, inReplyTo.Length - 2);

                return inReplyTo;
            }

            return string.Empty;
        }

        private static string GetPreviewText(this MimeMessage message)
        {
            if (string.IsNullOrEmpty(message.HtmlBody))
                return message.TextBody;
            else
                return HtmlAgilityPackExtensions.GetPreviewText(message.HtmlBody);
        }

        public static MailCopy GetMailDetails(this IMessageSummary messageSummary, MailItemFolder folder, MimeMessage mime)
        {
            // MessageSummary will only have UniqueId, Flags, ThreadId.
            // Other properties are extracted directly from the MimeMessage.

            // IMAP doesn't have unique id for mails.
            // All mails are mapped to specific folders with incremental Id.
            // Uid 1 may belong to different messages in different folders, but can never be
            // same for different messages in same folders.
            // Here we create arbitrary Id that maps the Id of the message with Folder UniqueId.
            // When folder becomes invalid, we'll clear out these MailCopies as well.

            var messageUid = CreateUid(folder.Id, messageSummary.UniqueId.Id);
            var previewText = mime.GetPreviewText();

            var copy = new MailCopy()
            {
                Id = messageUid,
                CreationDate = mime.Date.UtcDateTime,
                ThreadId = messageSummary.GetThreadId(),
                MessageId = mime.GetMessageId(),
                Subject = mime.Subject,
                IsRead = messageSummary.Flags.GetIsRead(),
                IsFlagged = messageSummary.Flags.GetIsFlagged(),
                PreviewText = previewText,
                FromAddress = GetActualSenderAddress(mime),
                FromName = GetActualSenderName(mime),
                IsFocused = false,
                Importance = mime.GetImportance(),
                References = mime.References?.GetReferences(),
                InReplyTo = mime.GetInReplyTo(),
                HasAttachments = mime.Attachments.Any(),
                FileId = Guid.NewGuid()
            };

            return copy;
        }

        // TODO: Name and Address parsing should be handled better.
        // At some point Wino needs better contact management.

        public static string GetActualSenderName(MimeMessage message)
        {
            if (message == null)
                return string.Empty;

            return message.From.Mailboxes.FirstOrDefault()?.Name ?? message.Sender?.Name ?? Translator.UnknownSender;

            // From MimeKit

            // The "From" header specifies the author(s) of the message.
            // If more than one MimeKit.MailboxAddress is added to the list of "From" addresses,
            // the MimeKit.MimeMessage.Sender should be set to the single MimeKit.MailboxAddress
            // of the personal actually sending the message.

            // Also handle: https://stackoverflow.com/questions/46474030/mailkit-from-address

            //if (message.Sender != null)
            //    return string.IsNullOrEmpty(message.Sender.Name) ? message.Sender.Address : message.Sender.Name;
            //else if (message.From?.Mailboxes != null)
            //{
            //    var firstAvailableName = message.From.Mailboxes.FirstOrDefault(a => !string.IsNullOrEmpty(a.Name))?.Name;

            //    if (string.IsNullOrEmpty(firstAvailableName))
            //    {
            //        var firstAvailableAddress = message.From.Mailboxes.FirstOrDefault(a => !string.IsNullOrEmpty(a.Address))?.Address;

            //        if (!string.IsNullOrEmpty(firstAvailableAddress))
            //        {
            //            return firstAvailableAddress;
            //        }
            //    }

            //    return firstAvailableName;
            //}

            //// No sender, no from, I don't know what to do.
            //return Translator.UnknownSender;
        }

        // TODO: This is wrong.
        public static string GetActualSenderAddress(MimeMessage message)
        {
            return message.From.Mailboxes.FirstOrDefault()?.Address ?? message.Sender?.Address ?? Translator.UnknownSender;
            //if (mime == null)
            //    return string.Empty;

            //bool hasSingleFromMailbox = mime.From.Mailboxes.Count() == 1;

            //if (hasSingleFromMailbox)
            //    return mime.From.Mailboxes.First().GetAddress(idnEncode: true);
            //else if (mime.Sender != null)
            //    return mime.Sender.GetAddress(idnEncode: true);
            //else
            //    return Translator.UnknownSender;
        }
    }
}
