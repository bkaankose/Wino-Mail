using Microsoft.Exchange.WebServices.Data;

namespace Wino.Core.Synchronizers.Exchange;

internal static class ExchangeCalendarSchema
{
    /// <summary>Named property used to reconcile local calendar previews with synced EWS items.</summary>
    public static readonly ExtendedPropertyDefinition WinoClientTrackingId =
        new(DefaultExtendedPropertySet.PublicStrings, "WinoClientTrackingId", MapiPropertyType.String);
}
