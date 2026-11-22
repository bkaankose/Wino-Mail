using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.Linux.Notifications;

/// <summary>
/// The one application action every notification button and body targets: <c>app.notification-response</c> with a
/// string array of <c>key=value</c> pairs (the response kind, the action identifier, the category and the values the
/// shared response rules read). GLib routes a click to it through org.freedesktop.Notifications, org.gtk.Notifications
/// or the portal alike, so the head handles every response in one place.
/// </summary>
public static class LinuxNotificationActions
{
    public const string ActionName = "notification-response";
    public const string DetailedName = "app." + ActionName;

    private const string KindKey = "wino.kind";
    private const string ActionKey = "wino.action";
    private const string CategoryKey = "wino.category";

    public static GLib.Variant Encode(NotificationResponseKind kind, string actionIdentifier, string category, IReadOnlyDictionary<string, string> values)
    {
        var pairs = new List<string>(values.Count + 3)
        {
            KindKey + "=" + kind,
            ActionKey + "=" + actionIdentifier,
            CategoryKey + "=" + category,
        };
        foreach (var pair in values) pairs.Add(pair.Key + "=" + pair.Value);
        return GLib.Variant.NewStrv([.. pairs]);
    }

    /// <summary>Null when the parameter is not a response this builder wrote.</summary>
    public static NotificationResponse? Decode(GLib.Variant? parameter)
    {
        if (parameter is null) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameter.GetStrv(out _))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0) values[pair[..separator]] = pair[(separator + 1)..];
        }
        if (!values.Remove(KindKey, out var kindText) || !Enum.TryParse<NotificationResponseKind>(kindText, out var kind)) return null;
        values.Remove(ActionKey, out var action);
        values.Remove(CategoryKey, out var category);
        return new NotificationResponse(kind, action ?? string.Empty, category, null, values);
    }
}
