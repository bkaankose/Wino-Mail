#nullable enable
using System.Runtime.Versioning;
using Foundation;

namespace Wino.Platform.Apple.PushRelay;

/// <summary>Reads a relay push (data-only, every value a string at the top level) from an APNs user-info dictionary.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios17.0")]
public static class ApplePushPayload
{
    private const string ApsKey = "aps";

    /// <summary>Top-level string values by key. The <c>aps</c> dictionary and non-string values are skipped.</summary>
    public static Dictionary<string, string> ToPayload(NSDictionary? userInfo)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo is null) return payload;
        foreach (var (key, value) in userInfo)
        {
            if (key is not NSString name || value is not NSString text) continue;
            var keyText = name.ToString();
            if (keyText == ApsKey) continue;
            payload[keyText] = text.ToString();
        }
        return payload;
    }
}
