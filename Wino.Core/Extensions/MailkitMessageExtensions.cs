using System;
using System.Linq;
using MailKit;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Services.Extensions;

namespace Wino.Core.Extensions;

/// <summary>
/// MailKit-typed message helpers used by the IMAP synchronizers. Lives in Wino.Core
/// (companion-only) so the shared Wino.Services assembly stays MailKit-free; the
/// MailKit-free uid string helpers remain in Wino.Services.Extensions.MailkitClientExtensions.
/// </summary>
public static class MailkitMessageExtensions
{
    public static UniqueId ResolveUidStruct(string mailCopyId)
        => new UniqueId(MailkitClientExtensions.ResolveUid(mailCopyId));

    public static UniqueId ResolveUidStruct(MailCopy mailCopy)
        => new UniqueId(MailkitClientExtensions.ResolveUid(mailCopy));

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
        => MailHeaderExtensions.NormalizeMessageId(mimeMessage.Headers[HeaderId.MessageId]);

    public static string GetReferences(this MessageIdList messageIdList)
        => MailHeaderExtensions.JoinStoredReferences(messageIdList);

    public static string GetInReplyTo(this MimeMessage mimeMessage)
    {
        if (mimeMessage.Headers.Contains(HeaderId.InReplyTo))
        {
            return MailHeaderExtensions.NormalizeMessageId(mimeMessage.Headers[HeaderId.InReplyTo]);
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

    public static MailCopy GetMailDetails(this IMessageSummary messageSummary, MailItemFolder folder, MimeMessage mime = null)
    {
        // IMAP UIDs are unique only within a folder.
        // MailCopy.Id maps to {FolderId}_{UID} for deterministic folder-local identity.

        var envelope = messageSummary.Envelope;

        var messageUid = MailkitClientExtensions.CreateUid(folder.Id, messageSummary.UniqueId.Id);
        var subject = mime?.Subject ?? envelope?.Subject ?? string.Empty;
        var previewText = mime != null ? mime.GetPreviewText() : GetPreviewText(messageSummary, subject);

        // Prefer InternalDate (server received time). Fall back to envelope date and finally UTC now.
        var creationDate = messageSummary.InternalDate?.UtcDateTime
                           ?? envelope?.Date?.UtcDateTime
                           ?? DateTime.UtcNow;

        var messageId = MailHeaderExtensions.NormalizeMessageId(mime?.GetMessageId() ?? envelope?.MessageId);
        var fromName = mime != null ? GetActualSenderName(mime) : GetEnvelopeSenderName(envelope);
        var fromAddress = mime != null ? GetActualSenderAddress(mime) : GetEnvelopeSenderAddress(envelope);
        var references = mime?.References?.GetReferences() ?? messageSummary.References?.GetReferences();
        var inReplyTo = MailHeaderExtensions.NormalizeMessageId(mime != null ? mime.GetInReplyTo() : envelope?.InReplyTo);
        var threadId = ResolveThreadId(messageSummary, messageId, references, inReplyTo);
        var hasAttachments = mime != null ? mime.Attachments.Any() : false;
        var itemType = mime != null ? GetMailItemTypeFromMime(mime) : MailItemType.Mail;

        var copy = new MailCopy()
        {
            Id = messageUid,
            ImapUid = messageSummary.UniqueId.Id,
            ImapUidValidity = folder.UidValidity,
            CreationDate = creationDate,
            ThreadId = threadId,
            MessageId = messageId,
            Subject = subject,
            IsRead = messageSummary.Flags.GetIsRead(),
            IsReadReceiptRequested = mime?.HasReadReceiptRequest()
                                     ?? (messageSummary.Headers?.Contains(Constants.DispositionNotificationToHeader) == true
                                         && !string.IsNullOrWhiteSpace(messageSummary.Headers[Constants.DispositionNotificationToHeader])),
            IsFlagged = messageSummary.Flags.GetIsFlagged(),
            PreviewText = previewText,
            FromAddress = fromAddress,
            FromName = fromName,
            IsFocused = false,
            Importance = mime != null ? mime.GetImportance() : MailImportance.Normal,
            References = references,
            InReplyTo = inReplyTo,
            HasAttachments = hasAttachments,
            FileId = Guid.NewGuid(),
            ItemType = itemType
        };

        return copy;
    }

    private static string ResolveThreadId(IMessageSummary messageSummary, string messageId, string references, string inReplyTo)
    {
        var serverThreadId = messageSummary.GetThreadId();
        if (!string.IsNullOrEmpty(serverThreadId))
            return serverThreadId;

        // Fallback threading for IMAP providers that do not expose a native ThreadId:
        // - Prefer root of References chain
        // - Then In-Reply-To
        // - Finally own Message-Id (single-message thread root)
        var rootReference = references?
            .Split(new[] { ';', ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeThreadToken)
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(rootReference))
            return rootReference;

        var normalizedInReplyTo = NormalizeThreadToken(inReplyTo);
        if (!string.IsNullOrEmpty(normalizedInReplyTo))
            return normalizedInReplyTo;

        var normalizedMessageId = NormalizeThreadToken(messageId);
        if (!string.IsNullOrEmpty(normalizedMessageId))
            return normalizedMessageId;

        return string.Empty;
    }

    private static string NormalizeThreadToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();

        if (value.StartsWith("<") && value.EndsWith(">") && value.Length > 2)
            value = value.Substring(1, value.Length - 2);

        return value;
    }

    private static string GetPreviewText(IMessageSummary messageSummary, string subjectFallback)
    {
        if (!string.IsNullOrWhiteSpace(messageSummary.PreviewText))
            return messageSummary.PreviewText;

        return subjectFallback ?? string.Empty;
    }

    private static string GetEnvelopeSenderName(Envelope envelope)
    {
        var mailbox = envelope?.From?.Mailboxes?.FirstOrDefault() ?? envelope?.Sender?.Mailboxes?.FirstOrDefault();
        if (mailbox == null)
            return Translator.UnknownSender;

        return string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name;
    }

    private static string GetEnvelopeSenderAddress(Envelope envelope)
    {
        var mailbox = envelope?.From?.Mailboxes?.FirstOrDefault() ?? envelope?.Sender?.Mailboxes?.FirstOrDefault();
        return mailbox?.Address ?? Translator.UnknownSender;
    }

    /// <summary>
    /// Determines MailItemType based on MIME message content type.
    /// Calendar invitations have text/calendar content type with METHOD parameter.
    /// </summary>
    private static MailItemType GetMailItemTypeFromMime(MimeMessage mime)
    {
        if (mime == null) return MailItemType.Mail;

        // Check if the message contains text/calendar content
        var calendarPart = mime.BodyParts.OfType<MimePart>()
            .FirstOrDefault(p => p.ContentType?.MimeType?.Equals("text/calendar", StringComparison.OrdinalIgnoreCase) == true);

        if (calendarPart != null)
        {
            // Check the METHOD parameter to determine invitation type
            var method = calendarPart.ContentType.Parameters
                .FirstOrDefault(p => p.Name.Equals("method", StringComparison.OrdinalIgnoreCase))?.Value?.ToUpperInvariant();

            if (!string.IsNullOrEmpty(method))
            {
                return method switch
                {
                    "REQUEST" => MailItemType.CalendarInvitation,
                    "CANCEL" => MailItemType.CalendarCancellation,
                    "REPLY" => MailItemType.CalendarResponse,
                    _ => MailItemType.Mail
                };
            }

            // If no method specified, assume it's an invitation
            return MailItemType.CalendarInvitation;
        }

        return MailItemType.Mail;
    }

    // TODO: Name and Address parsing should be handled better.
    // At some point Wino needs better contact management.

    public static string GetActualSenderName(MimeMessage message)
    {
        if (message == null)
            return string.Empty;

        return message.From.Mailboxes.FirstOrDefault()?.Name ?? message.Sender?.Name ?? Translator.UnknownSender;
    }

    // TODO: This is wrong.
    public static string GetActualSenderAddress(MimeMessage message)
    {
        return message.From.Mailboxes.FirstOrDefault()?.Address ?? message.Sender?.Address ?? Translator.UnknownSender;
    }
}
