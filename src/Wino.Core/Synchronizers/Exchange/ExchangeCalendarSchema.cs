using Microsoft.Exchange.WebServices.Data;

namespace Wino.Core.Synchronizers.Exchange;

internal static class ExchangeCalendarSchema
{
    /// <summary>
    /// Named property stamped on appointments this client creates, carrying the local preview id so the
    /// synced event reconciles with the optimistic UI item. The MAPI path writes the same PublicStrings
    /// property, so an event created on one transport is recognized by the other.
    /// </summary>
    public static readonly ExtendedPropertyDefinition WinoClientTrackingId =
        new(DefaultExtendedPropertySet.PublicStrings, "WinoClientTrackingId", MapiPropertyType.String);
}
