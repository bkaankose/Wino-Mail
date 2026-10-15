using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The POST_NOTIFICATIONS runtime permission (Android 13+; granted at install below that). The shell asks once when it
/// first appears (<see cref="RequestOnceAsync"/>); onboarding does not ask. The system shows the dialog at most twice;
/// after a refusal the user changes it in system settings. The answer is not needed by the caller: the notification builder checks before each post.
/// </summary>
public sealed class AndroidNotificationPermission
{
    public const int RequestCode = 0x4E4F;
    private const string FileName = "wino_permissions";
    private const string AskedKey = "post_notifications_asked";

    private TaskCompletionSource<bool>? _pending;

    /// <summary>True when notifications may be posted (always below Android 13 unless the user blocked the app).</summary>
    public static bool IsGranted(Context context) => NotificationManagerCompat.From(context).AreNotificationsEnabled();

    /// <summary>Whether the prompt was already shown once by <see cref="RequestOnceAsync"/> or <see cref="RequestAsync"/>.</summary>
    public static bool WasAsked(Context context)
        => context.GetSharedPreferences(FileName, FileCreationMode.Private)!.GetBoolean(AskedKey, false);

    /// <summary>Asks once per installation; later calls return the current state without a prompt.</summary>
    public Task<bool> RequestOnceAsync(Activity activity)
        => WasAsked(activity) ? Task.FromResult(IsGranted(activity)) : RequestAsync(activity);

    /// <summary>Shows the system prompt when the permission is missing (Android 13+); completes with the answer.</summary>
    public Task<bool> RequestAsync(Activity activity)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) ||
            ContextCompat.CheckSelfPermission(activity, Manifest.Permission.PostNotifications) == Permission.Granted)
        {
            return Task.FromResult(IsGranted(activity));
        }

        using (var editor = activity.GetSharedPreferences(FileName, FileCreationMode.Private)!.Edit()!)
        {
            editor.PutBoolean(AskedKey, true);
            editor.Apply();
        }

        _pending?.TrySetResult(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        ActivityCompat.RequestPermissions(activity, [Manifest.Permission.PostNotifications], RequestCode);
        return pending.Task;
    }

    /// <summary>MainActivity forwards <c>onRequestPermissionsResult</c>.</summary>
    public void OnRequestPermissionsResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode != RequestCode) return;
        var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
        Serilog.Log.Information("Notification permission {State}.", granted ? "granted" : "denied");
        _pending?.TrySetResult(granted);
        _pending = null;
    }
}
