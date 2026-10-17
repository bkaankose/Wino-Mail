#nullable enable
using System;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Synchronizers.CalDav;

namespace Wino.Core.Integration.Processors;

/// <summary>CalDAV ATTACH properties become <c>CalendarAttachment</c> rows on every import of an event.</summary>
public partial class ImapChangeProcessor
{
    private readonly IApplicationConfiguration? _applicationConfiguration;

    /// <summary>
    /// Replaces the attachment rows of the item with the event's ATTACH properties. Inline data of
    /// a single event or series master is written to the attachment folder at once; occurrences
    /// read it from their stored ICS copy when opened. A failure here never fails the import.
    /// </summary>
    private async Task SaveCalDavAttachmentsAsync(Guid calendarItemId, CalDavCalendarEvent calendarEvent)
    {
        try
        {
            await CalDavAttachmentRows.ReplaceAsync(
                CalendarService,
                calendarItemId,
                calendarEvent.Attachments,
                _applicationConfiguration?.CalendarAttachmentsFolderPath,
                storeInlineData: !calendarEvent.IsRecurringInstance).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ForContext<ImapChangeProcessor>().Warning(ex, "Could not store the attachments of CalDAV event {RemoteEventId}", calendarEvent.RemoteEventId);
        }
    }
}
