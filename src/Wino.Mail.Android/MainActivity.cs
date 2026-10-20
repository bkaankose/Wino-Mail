using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Android.Infrastructure;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.Authentication;
using Wino.Platform.Android.Notifications;

namespace Wino.Mail.Android;

/// <summary>
/// The single activity: edge to edge over the theme wallpaper, one fragment container that the navigation service
/// fills, and back through the OnBackPressedDispatcher (predictive back on Android 13 and later). The activity is
/// recreated on configuration changes; pages survive because their ViewModels live in <see cref="PageEntry"/> objects
/// in the application container, so fragment state from the saved instance state is never restored.
/// </summary>
[Activity(Label = "Wino Mail", MainLauncher = true, Exported = true, Theme = "@style/Theme.Wino",
    LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
public sealed class MainActivity : AppCompatActivity
{
    public const int ContentContainerId = Resource.Id.wino_main_content;

    private AndroidNavigationService? _navigation;
    private AndroidThemeService? _theme;
    private BackCallback? _back;
    private global::Android.Graphics.Color? _accent;
    private Wino.Core.Domain.Enums.WinoIconStyle _iconStyle;
    private Wino.Core.Domain.Enums.WinoIconSet _iconSet;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Fragments are recreated from the navigation service's entries, not from saved state (see the class remarks).
        base.OnCreate(null);
        WindowCompat.SetDecorFitsSystemWindows(Window ?? throw new InvalidOperationException("The activity has no window."), false);
        SetContentView(Resource.Layout.activity_main);

        if (MainApplication.Services is not { } services) return;
        services.GetRequiredService<AndroidWindowSizeClassService>().Update(this);

        _theme = services.GetRequiredService<AndroidThemeService>();
        _theme.AppearanceChanged += OnAppearanceChanged;
        // Views take the accent and the icon font when they are created; a change recreates the activity (below).
        _accent = global::Wino.Presentation.Android.WinoColors.AccentOverride;
        _iconStyle = global::Wino.Presentation.Android.WinoIcons.Style;
        _iconSet = global::Wino.Presentation.Android.WinoIcons.Set;
        ApplyWallpaper();

        _navigation = services.GetRequiredService<AndroidNavigationService>();
        _back = new BackCallback(_navigation);
        OnBackPressedDispatcher.AddCallback(this, _back);
        _navigation.BackStateChanged += OnBackStateChanged;
        _navigation.AttachActivity(this);
        _back.Enabled = _navigation.CanHandleBack;
        HandleNotificationIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
        HandleNotificationIntent(intent);
    }

    /// <summary>
    /// A notification tap or an action that needs the app (reply all, forward, join, sign in): the shared response
    /// rules (<see cref="INotificationResponseService"/>) run once the runtime started, then the notification goes away.
    /// </summary>
    private static void HandleNotificationIntent(Intent? intent)
    {
        if (!AndroidNotificationIntents.TryRead(intent, out var response, out var identity)) return;
        AndroidNotificationIntents.Consume(intent!);
        _ = HandleNotificationResponseAsync(response, identity);
    }

    private static async Task HandleNotificationResponseAsync(Wino.Core.Domain.Models.Notifications.NotificationResponse response,
        AndroidNotificationIntents.NotificationIdentity identity)
    {
        try
        {
            if (MainApplication.Lifecycle?.Startup is { } startup) await startup;
            if (MainApplication.Services is not { } services) return;
            services.GetRequiredService<AndroidNotificationBuilder>().Cancel(identity);
            await services.GetRequiredService<INotificationResponseService>().HandleAsync(response);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Opening a notification failed.");
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        MainApplication.Services?.GetService<AndroidNotificationPermission>()?.OnRequestPermissionsResult(requestCode, grantResults);
    }

    /// <summary>Sign-in screens started for a result (Play services consent, MSAL's browser activity).</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        MainApplication.Services?.GetService<AndroidActivityResults>()?.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        MainApplication.Services?.GetService<AndroidWindowSizeClassService>()?.Update(this);
    }

    protected override void OnDestroy()
    {
        if (_theme is not null) _theme.AppearanceChanged -= OnAppearanceChanged;
        if (_navigation is not null)
        {
            _navigation.BackStateChanged -= OnBackStateChanged;
            _navigation.DetachActivity(this);
        }
        _back?.Remove();
        base.OnDestroy();
    }

    private void OnBackStateChanged(object? sender, EventArgs args)
    {
        if (_back is not null && _navigation is not null) _back.Enabled = _navigation.CanHandleBack;
    }

    /// <summary>
    /// The theme service applied a theme: the wallpaper is repainted in place; a new accent or icon style recreates the
    /// activity so every view is built again in it (pages keep their state in the navigation service's entries). Night
    /// mode changes are recreated by AppCompat itself.
    /// </summary>
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (IsFinishing || IsDestroyed || IsChangingConfigurations) return;
        if (_accent != global::Wino.Presentation.Android.WinoColors.AccentOverride || _iconStyle != global::Wino.Presentation.Android.WinoIcons.Style
            || _iconSet != global::Wino.Presentation.Android.WinoIcons.Set)
        {
            Recreate();
            return;
        }
        ApplyWallpaper();
    }

    private void ApplyWallpaper()
    {
        if (_theme is null || Resources is not { } resources || FindViewById<ImageView>(Resource.Id.wino_backdrop) is not { } backdrop) return;
        var wallpaper = _theme.LoadWallpaper(resources);
        backdrop.SetImageBitmap(wallpaper);
        // Bars over the wallpaper (WinoBarBackdropView) blur the same bitmap.
        global::Wino.Presentation.Android.WinoBackdrop.Set(wallpaper);
    }

    /// <summary>Back while the app has somewhere to go; disabled otherwise so the system handles it.</summary>
    private sealed class BackCallback(AndroidNavigationService navigation) : OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed()
        {
            if (!navigation.TryHandleBack())
            {
                // Nothing left inside the app: let the system finish the task with its own animation.
                Enabled = false;
            }
        }
    }
}
