using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using RemoteInput = AndroidX.Core.App.RemoteInput;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.Android.Notifications;

/// <summary>
/// The components that receive notification taps and actions. They live in the app head, which owns the service
/// container: the activity that opens on a tap, and the broadcast receiver that runs actions without opening the app.
/// </summary>
/// <param name="ActivityType">The launcher activity (single task); it reads taps with <see cref="AndroidNotificationIntents.TryRead"/>.</param>
/// <param name="ActionReceiverType">The broadcast receiver for actions that need no UI (mark read, delete, archive, inline reply, snooze).</param>
public sealed record AndroidNotificationComponents(Type ActivityType, Type ActionReceiverType);

/// <summary>
/// Notification payloads on intents. A notification carries the same values as its Apple and Windows twins (keys from
/// <see cref="Wino.Core.Domain.Constants"/>) in a bundle; a tap or an action turns them back into a
/// <see cref="NotificationResponse"/> for the shared <c>INotificationResponseService</c>.
/// </summary>
public static class AndroidNotificationIntents
{
    public const string ActionNotificationResponse = "com.winomail.android.action.NOTIFICATION_RESPONSE";
    public const string ReplyInputKey = "wino.notification.reply";

    private const string ValuesExtra = "wino.notification.values";
    private const string ActionExtra = "wino.notification.action";
    private const string CategoryExtra = "wino.notification.category";
    private const string TagExtra = "wino.notification.tag";
    private const string IdExtra = "wino.notification.id";
    private const string KindExtra = "wino.notification.kind";

    /// <summary>The notification tag and id an intent belongs to, so the handler can remove it.</summary>
    public readonly record struct NotificationIdentity(string Tag, int Id);

    /// <summary>The tap: opens the activity with the payload and the default response.</summary>
    internal static PendingIntent CreateContentIntent(Context context, AndroidNotificationComponents components, NotificationIdentity identity,
        string category, IReadOnlyDictionary<string, string> values)
        => CreateActivityIntent(context, components, identity, category, values, string.Empty, NotificationResponseKind.Default);

    /// <summary>An action that needs the app in front (reply all, forward, join, sign in).</summary>
    internal static PendingIntent CreateActivityIntent(Context context, AndroidNotificationComponents components, NotificationIdentity identity,
        string category, IReadOnlyDictionary<string, string> values, string actionIdentifier, NotificationResponseKind kind = NotificationResponseKind.Action)
    {
        var intent = new Intent(context, Java.Lang.Class.FromType(components.ActivityType));
        intent.SetAction(ActionNotificationResponse);
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        Fill(intent, identity, category, values, actionIdentifier, kind);
        return PendingIntent.GetActivity(context, RequestCode(identity, actionIdentifier), intent, ImmutableFlags)!;
    }

    /// <summary>An action that runs in the background through the action receiver.</summary>
    internal static PendingIntent CreateBroadcastIntent(Context context, AndroidNotificationComponents components, NotificationIdentity identity,
        string category, IReadOnlyDictionary<string, string> values, string actionIdentifier, bool acceptsText = false,
        NotificationResponseKind kind = NotificationResponseKind.Action)
    {
        var intent = new Intent(context, Java.Lang.Class.FromType(components.ActionReceiverType));
        intent.SetAction(ActionNotificationResponse);
        Fill(intent, identity, category, values, actionIdentifier, kind);
        // An inline reply writes its text into the intent, so that intent must stay mutable (API 31+).
        var flags = acceptsText && OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable
            : ImmutableFlags;
        return PendingIntent.GetBroadcast(context, RequestCode(identity, actionIdentifier), intent, flags)!;
    }

    /// <summary>Reads a notification response from an activity or receiver intent; false for any other intent.</summary>
    public static bool TryRead(Intent? intent, out NotificationResponse response, out NotificationIdentity identity)
    {
        response = null!;
        identity = default;
        if (intent is null || intent.Action != ActionNotificationResponse) return false;
        if (intent.GetBundleExtra(ValuesExtra) is not { } bundle) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in bundle.KeySet() ?? [])
        {
            if (bundle.GetString(key) is { } value) values[key] = value;
        }

        var kind = (NotificationResponseKind)intent.GetIntExtra(KindExtra, (int)NotificationResponseKind.Default);
        var text = RemoteInput.GetResultsFromIntent(intent)?.GetCharSequence(ReplyInputKey)?.ToString();
        response = new NotificationResponse(kind, intent.GetStringExtra(ActionExtra) ?? string.Empty,
            intent.GetStringExtra(CategoryExtra), text, values);
        identity = new NotificationIdentity(intent.GetStringExtra(TagExtra) ?? string.Empty, intent.GetIntExtra(IdExtra, 0));
        return true;
    }

    /// <summary>Marks an intent as handled so a recreated activity does not answer the same tap twice.</summary>
    public static void Consume(Intent intent) => intent.SetAction(Intent.ActionMain);

    private static void Fill(Intent intent, NotificationIdentity identity, string category, IReadOnlyDictionary<string, string> values,
        string actionIdentifier, NotificationResponseKind kind)
    {
        var bundle = new Bundle();
        foreach (var pair in values) bundle.PutString(pair.Key, pair.Value);
        intent.PutExtra(ValuesExtra, bundle);
        intent.PutExtra(ActionExtra, actionIdentifier);
        intent.PutExtra(CategoryExtra, category);
        intent.PutExtra(TagExtra, identity.Tag);
        intent.PutExtra(IdExtra, identity.Id);
        intent.PutExtra(KindExtra, (int)kind);
        // A distinct data URI keeps each notification's PendingIntent (and its extras) separate.
        intent.SetData(global::Android.Net.Uri.Parse($"wino-notification://{Uri.EscapeDataString(identity.Tag)}/{identity.Id}/{Uri.EscapeDataString(actionIdentifier)}"));
    }

    private static PendingIntentFlags ImmutableFlags => PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable;

    private static int RequestCode(NotificationIdentity identity, string actionIdentifier)
        => StableHash(identity.Tag + "|" + identity.Id + "|" + actionIdentifier);

    /// <summary>FNV-1a: the same value in every process (string.GetHashCode is randomized per process).</summary>
    internal static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in text) hash = (hash ^ character) * 16777619;
            return (int)hash;
        }
    }
}
