using System;
using System.Threading;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Extensions;
using Wino.Services.Models.MailItem;
using EwsMimeContent = Microsoft.Exchange.WebServices.Data.MimeContent;
using Task = System.Threading.Tasks.Task;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

public partial class ExchangeSynchronizer
{
    // PidTagMessageFlags. An item saved from MIME is a draft (mfUnsent) unless the flags say otherwise; 1 is mfRead.
    private static readonly ExtendedPropertyDefinition MessageFlagsProperty = new(0x0E07, MapiPropertyType.Integer);

    // PidTagMessageDeliveryTime and PidTagClientSubmitTime: the received and sent times the list shows.
    private static readonly ExtendedPropertyDefinition MessageDeliveryTimeProperty = new(0x0E06, MapiPropertyType.SystemTime);
    private static readonly ExtendedPropertyDefinition ClientSubmitTimeProperty = new(0x0039, MapiPropertyType.SystemTime);

    public override bool SupportsMailImport => true;

    /// <summary>
    /// Creates the item from the exact MIME as received mail. Unread and the flag are set after the save, because the
    /// read bit is part of the flags that make the item non-draft. A failure of that follow-up leaves the copy stored
    /// and read, which the next synchronization shows.
    /// </summary>
    public override async System.Threading.Tasks.Task<MailImportResult> ImportMailAsync(MailImportPackage package, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var receivedUtc = package.ReceivedDateUtc.AsStoredUtc();
        var sentUtc = package.Mime.Date == DateTimeOffset.MinValue ? receivedUtc : package.Mime.Date.UtcDateTime;

        var message = new EmailMessage(service)
        {
            MimeContent = new EwsMimeContent("UTF-8", await ToMimeBytesAsync(package.Mime, cancellationToken).ConfigureAwait(false))
        };

        message.SetExtendedProperty(MessageFlagsProperty, 1);
        message.SetExtendedProperty(MessageDeliveryTimeProperty, receivedUtc);
        message.SetExtendedProperty(ClientSubmitTimeProperty, sentUtc);

        await message.Save(new FolderId(package.TargetFolder.RemoteFolderId)).ConfigureAwait(false);

        if (!package.IsRead || package.IsFlagged)
        {
            try
            {
                if (!package.IsRead)
                    message.IsRead = false;
                if (package.IsFlagged)
                    message.Flag = new Flag { FlagStatus = ItemFlagStatus.Flagged };

                await message.Update(ConflictResolutionMode.AlwaysOverwrite).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "The moved mail was stored in Exchange, but its unread or flagged state was not.");
            }
        }

        return new MailImportResult(message.Id?.UniqueId);
    }
}
