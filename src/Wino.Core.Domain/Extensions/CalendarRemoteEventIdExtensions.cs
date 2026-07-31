using System;

namespace Wino.Core.Domain.Extensions;

public static class CalendarRemoteEventIdExtensions
{
    private const string ClientTrackingSeparator = "::";
    private const string CalDavClientTrackingPrefix = "caldav-";
    private const string LocalClientTrackingPrefix = "local-";

    public static string GetProviderRemoteEventId(this string remoteEventId)
    {
        if (string.IsNullOrWhiteSpace(remoteEventId))
            return string.Empty;

        var separatorIndex = remoteEventId.IndexOf(ClientTrackingSeparator, StringComparison.Ordinal);
        return separatorIndex >= 0 ? remoteEventId[..separatorIndex] : remoteEventId;
    }

    public static Guid? GetClientTrackingId(this string remoteEventId)
    {
        if (string.IsNullOrWhiteSpace(remoteEventId))
            return null;

        if (remoteEventId.Contains(ClientTrackingSeparator, StringComparison.Ordinal))
        {
            var trackedPart = remoteEventId[(remoteEventId.LastIndexOf(ClientTrackingSeparator, StringComparison.Ordinal) + ClientTrackingSeparator.Length)..];
            if (TryParseGuid(trackedPart, out var trackedId))
                return trackedId;
        }

        if (TryParseGuid(remoteEventId, out var directId))
            return directId;

        if (remoteEventId.StartsWith(CalDavClientTrackingPrefix, StringComparison.OrdinalIgnoreCase) &&
            TryParseGuid(remoteEventId[CalDavClientTrackingPrefix.Length..], out var calDavId))
        {
            return calDavId;
        }

        if (remoteEventId.StartsWith(LocalClientTrackingPrefix, StringComparison.OrdinalIgnoreCase) &&
            TryParseGuid(remoteEventId[LocalClientTrackingPrefix.Length..], out var localId))
        {
            return localId;
        }

        return null;
    }

    public static string WithClientTrackingId(this string providerRemoteEventId, Guid? clientTrackingId)
    {
        if (string.IsNullOrWhiteSpace(providerRemoteEventId) || !clientTrackingId.HasValue)
            return providerRemoteEventId ?? string.Empty;

        return $"{providerRemoteEventId}{ClientTrackingSeparator}{clientTrackingId.Value:N}";
    }

    private static bool TryParseGuid(string value, out Guid parsedGuid)
    {
        parsedGuid = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Guid.TryParseExact(value, "N", out parsedGuid) || Guid.TryParse(value, out parsedGuid);
    }
}
