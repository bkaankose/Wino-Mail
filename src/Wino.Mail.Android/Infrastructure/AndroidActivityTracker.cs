using Android.App;
using Android.OS;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Follows the app's activities for services that outlive them: the resumed or started activity for launching other
/// apps and presenting dialogs, and whether any activity is started (the app is in the foreground). Registered once on
/// the <see cref="global::Android.App.Application"/>; every callback arrives on the main thread.
/// </summary>
public sealed class AndroidActivityTracker : Java.Lang.Object, global::Android.App.Application.IActivityLifecycleCallbacks
{
    private readonly List<WeakReference<Activity>> _started = [];
    private WeakReference<Activity>? _resumed;

    /// <summary>The resumed activity, else the most recently started one; null while the app is in the background.</summary>
    public Activity? Current
    {
        get
        {
            if (_resumed is not null && _resumed.TryGetTarget(out var resumed) && !resumed.IsFinishing) return resumed;
            for (int index = _started.Count - 1; index >= 0; index--)
                if (_started[index].TryGetTarget(out var activity) && !activity.IsFinishing) return activity;
            return null;
        }
    }

    /// <summary>True while at least one activity is started (visible).</summary>
    public bool IsForeground => _started.Count > 0;

    /// <summary>Raised when the first activity starts (true) and when the last one stops (false).</summary>
    public event EventHandler<bool>? ForegroundChanged;

    public void OnActivityStarted(Activity activity)
    {
        bool wasForeground = IsForeground;
        Prune();
        _started.Add(new WeakReference<Activity>(activity));
        if (!wasForeground) ForegroundChanged?.Invoke(this, true);
    }

    public void OnActivityStopped(Activity activity)
    {
        bool wasForeground = IsForeground;
        _started.RemoveAll(reference => !reference.TryGetTarget(out var target) || ReferenceEquals(target, activity));
        if (wasForeground && !IsForeground) ForegroundChanged?.Invoke(this, false);
    }

    public void OnActivityResumed(Activity activity) => _resumed = new WeakReference<Activity>(activity);

    public void OnActivityPaused(Activity activity)
    {
        if (_resumed is not null && _resumed.TryGetTarget(out var resumed) && ReferenceEquals(resumed, activity)) _resumed = null;
    }

    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) { }
    public void OnActivityDestroyed(Activity activity) { }
    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }

    private void Prune() => _started.RemoveAll(reference => !reference.TryGetTarget(out _));
}
