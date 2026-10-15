#if DEBUG
using Android.App;
using Android.Content;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>Debug-only overrides for emulator checks, kept in a private SharedPreferences file so they survive restarts.</summary>
internal static class DebugSettings
{
    private const string FileName = "wino_debug";
    private const string GraceSecondsKey = "grace_period_seconds";

    private static ISharedPreferences Preferences => Application.Context.GetSharedPreferences(FileName, FileCreationMode.Private)!;

    /// <summary>A shorter background grace period (<c>graceperiod</c> command); null uses the release value.</summary>
    public static TimeSpan? GracePeriodOverride
    {
        get
        {
            var seconds = Preferences.GetInt(GraceSecondsKey, 0);
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        set
        {
            using var editor = Preferences.Edit()!;
            if (value is { } period && period > TimeSpan.Zero) editor.PutInt(GraceSecondsKey, (int)period.TotalSeconds);
            else editor.Remove(GraceSecondsKey);
            editor.Apply();
        }
    }
}
#endif
