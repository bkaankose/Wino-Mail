using Microsoft.Exchange.WebServices.Data;

namespace Wino.Core.Synchronizers.Exchange;

internal static class ExchangeCalendarSchema
{
    /// <summary>
    /// Named string property used to round-trip Wino's local optimistic-preview id through EWS. On
    /// create we stamp the appointment with the local CalendarItem.Id; on read we fold it into the
    /// stored RemoteEventId (via WithClientTrackingId) so the shared calendar UI can reconcile the
    /// synced event with the preview instead of showing a duplicate. This is the EWS analog of
    /// Outlook's Graph TransactionId / CalDav's client-generated id.
    /// </summary>
    public static readonly ExtendedPropertyDefinition WinoClientTrackingId =
        new(DefaultExtendedPropertySet.PublicStrings, "WinoClientTrackingId", MapiPropertyType.String);
}
