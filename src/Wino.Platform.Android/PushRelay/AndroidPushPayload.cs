using Firebase.Messaging;

namespace Wino.Platform.Android.PushRelay;

/// <summary>Reads a relay push from an FCM data message (ApplePushPayload twin): every data value is a string at the top level.</summary>
public static class AndroidPushPayload
{
    public static Dictionary<string, string> ToPayload(RemoteMessage? message)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (message?.Data is not { } data) return payload;
        foreach (var pair in data)
        {
            if (!string.IsNullOrEmpty(pair.Key) && pair.Value is { } value) payload[pair.Key] = value;
        }
        return payload;
    }
}
