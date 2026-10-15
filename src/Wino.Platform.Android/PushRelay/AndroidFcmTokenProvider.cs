using Android.Content;
using Android.Gms.Tasks;
using Android.Runtime;
using Firebase;
using Firebase.Messaging;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.PushRelay;

/// <summary>
/// The FCM registration token for WinoRelay (compile-only for v1: written and registered, not tested or relied on).
/// Firebase starts only when the app was built with <c>google-services.json</c> (the FirebaseInitProvider reads the
/// generated resources); without it <see cref="IsPushChannelAvailable"/> is false and nothing touches Firebase. The
/// token is an opaque string, passed to the relay unchanged, and never logged. The head's messaging service forwards
/// <c>onNewToken</c> to <see cref="SetToken"/>.
/// </summary>
public sealed class AndroidFcmTokenProvider : IPushTokenProvider
{
    private readonly Lock _gate = new();
    private readonly bool _configured;
    private string? _token;
    private string? _failure;

    public AndroidFcmTokenProvider(Context context)
    {
        _configured = IsFirebaseConfigured(context);
        if (!_configured) _failure = "Firebase is not configured (google-services.json was not part of the build).";
    }

    /// <summary>True when Firebase initialized from the build's google-services.json.</summary>
    public static bool IsFirebaseConfigured(Context context)
    {
        try { return FirebaseApp.GetApps(context).Count > 0; }
        catch (Exception) { return false; }
    }

    public bool IsPushChannelAvailable => _configured;

    public string? CurrentToken
    {
        get { lock (_gate) return _token; }
    }

    public string? LastFailure
    {
        get { lock (_gate) return _failure; }
    }

    public event EventHandler? TokenChanged;

    /// <summary>Asks Firebase for the current token; the answer arrives through <see cref="SetToken"/> or <see cref="SetFailure"/>.</summary>
    public void RequestToken()
    {
        if (!_configured) return;
        try
        {
            GetTokenTask().AddOnCompleteListener(new TokenListener(this));
        }
        catch (Exception error)
        {
            SetFailure(error.Message);
        }
    }

    /// <summary>
    /// <c>FirebaseMessaging.getToken()</c>, called through JNI: the binding marks its wrapper obsolete although the
    /// Java method is the documented way to read the current token.
    /// </summary>
    private static global::Android.Gms.Tasks.Task GetTokenTask()
    {
        var messaging = FirebaseMessaging.Instance;
        var method = JNIEnv.GetMethodID(JNIEnv.GetObjectClass(messaging.Handle), "getToken", "()Lcom/google/android/gms/tasks/Task;");
        var handle = JNIEnv.CallObjectMethod(messaging.Handle, method);
        return Java.Lang.Object.GetObject<global::Android.Gms.Tasks.Task>(handle, JniHandleOwnership.TransferLocalRef)
            ?? throw new InvalidOperationException("FirebaseMessaging.getToken returned no task.");
    }

    public void SetToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_token, token, StringComparison.Ordinal);
            _token = token;
            _failure = null;
        }
        if (changed) TokenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetFailure(string? failure)
    {
        lock (_gate) _failure = string.IsNullOrWhiteSpace(failure) ? "unknown" : failure;
    }

    private sealed class TokenListener(AndroidFcmTokenProvider owner) : Java.Lang.Object, IOnCompleteListener
    {
        public void OnComplete(global::Android.Gms.Tasks.Task task)
        {
            if (task.IsSuccessful) owner.SetToken(task.Result?.ToString());
            else owner.SetFailure(task.Exception?.Message);
        }
    }
}
