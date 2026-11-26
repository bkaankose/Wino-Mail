using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Extensions;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Services.Models.MailItem;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

public sealed partial class MapiExchangeSynchronizer
{
    private const uint SenderAddressTypeTag = 0x0C1E001F;              // PidTagSenderAddressType
    private const uint SentRepresentingAddressTypeTag = 0x0064001F;    // PidTagSentRepresentingAddressType
    private const uint SentRepresentingEmailAddressTag = 0x0065001F;   // PidTagSentRepresentingEmailAddress

    public override bool SupportsMailImport => true;

    /// <summary>
    /// Creates the message as received mail in the target folder. MAPI carries no MIME, so the message is written as
    /// properties: the original headers as PidTagTransportMessageHeaders, the sender, the received and sent times, and
    /// the read and flag state. PidTagMessageFlags is set before the first save because it is read-only afterwards and
    /// a new message starts as mfRead | mfUnsent, which would make it a draft (MS-OXCMSG 2.2.1.6, 3.2.5.2).
    /// </summary>
    public override async Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default)
    {
        var folderId = RequireFolderId(package.TargetFolder);
        var mime = package.Mime;
        var receivedUtc = package.ReceivedDateUtc.AsStoredUtc();
        var sentUtc = mime.Date == DateTimeOffset.MinValue ? receivedUtc : mime.Date.UtcDateTime;

        var message = MapiOutgoingMessageMapper.FromMime(mime);

        // Stored mail must not ask this mailbox for a read receipt again.
        message.ReadReceiptRequested = false;
        message.TransportHeaders = ReadHeaderBlock(mime);

        foreach (var embedded in mime.BodyParts.OfType<MessagePart>())
        {
            if (embedded.Message is null)
                continue;

            using var stream = new MemoryStream();
            await embedded.Message.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
            var fileName = embedded.ContentDisposition?.FileName ?? (string.IsNullOrWhiteSpace(embedded.Message.Subject) ? "message" : embedded.Message.Subject) + ".eml";
            message.Attachments.Add(new MapiOutgoingAttachment(fileName, "message/rfc822", stream.ToArray(), null, false));
        }

        var flags = package.IsRead ? PropertyTags.MessageFlagRead : 0u;
        message.ExtraProperties.Add(TaggedPropertyValue.Long(PropertyTags.MessageFlags, flags));
        message.ExtraProperties.Add(TaggedPropertyValue.SysTime(PropertyTags.MessageDeliveryTime, receivedUtc));
        message.ExtraProperties.Add(TaggedPropertyValue.SysTime(PropertyTags.ClientSubmitTime, sentUtc));

        if (package.IsFlagged)
            message.ExtraProperties.Add(TaggedPropertyValue.Long(PropertyTags.FlagStatus, 2));

        if (mime.From.Mailboxes.FirstOrDefault() is { Address: { Length: > 0 } fromAddress } from)
        {
            var fromName = string.IsNullOrWhiteSpace(from.Name) ? fromAddress : from.Name;
            var sender = mime.Sender is { Address: { Length: > 0 } senderAddress } actualSender
                ? (Name: string.IsNullOrWhiteSpace(actualSender.Name) ? senderAddress : actualSender.Name, Address: senderAddress)
                : (Name: fromName, Address: fromAddress);

            message.ExtraProperties.AddRange(
            [
                TaggedPropertyValue.Unicode(PropertyTags.SentRepresentingName, fromName),
                TaggedPropertyValue.Unicode(SentRepresentingAddressTypeTag, "SMTP"),
                TaggedPropertyValue.Unicode(SentRepresentingEmailAddressTag, fromAddress),
                TaggedPropertyValue.Unicode(PropertyTags.SentRepresentingSmtpAddress, fromAddress),
                TaggedPropertyValue.Unicode(PropertyTags.SenderName, sender.Name),
                TaggedPropertyValue.Unicode(SenderAddressTypeTag, "SMTP"),
                TaggedPropertyValue.Unicode(PropertyTags.SenderEmailAddress, sender.Address),
                TaggedPropertyValue.Unicode(PropertyTags.SenderSmtpAddress, sender.Address),
            ]);
        }

        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var messageId = await MapiMessageComposer.CreateAsync(lease.Session, folderId, message, cancellationToken, Diagnostics, interactive: true).ConfigureAwait(false);

        return new MailImportResult(ToMapiId(messageId));
    }

    /// <summary>The message's header block as it arrived: everything up to the first blank line (MS-OXOMSG 2.2.1.61).</summary>
    private static string ReadHeaderBlock(MimeMessage mime)
    {
        using var stream = new MemoryStream();
        mime.Headers.WriteTo(FormatOptions.Default, stream);
        return Encoding.UTF8.GetString(stream.ToArray()).TrimEnd('\r', '\n');
    }
}
