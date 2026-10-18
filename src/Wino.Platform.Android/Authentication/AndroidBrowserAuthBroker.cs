using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Browser.CustomTabs;
using Wino.Core.Domain.Interfaces;
using AndroidUri = Android.Net.Uri;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// The Wino Account browser sign-in flow on Android: the API's authorize page in a Custom Tab (partial height on
/// Android 13 and later, the R2 decision; any browser otherwise), then the <c>wino://auth-complete</c> redirect the
/// head's redirect activity delivers to <see cref="AndroidExternalAuthCallbacks"/>. The tab stays open if the user never
/// returns; the shared service's timeout abandons the wait. Cancellation ends the wait with
/// <see cref="OperationCanceledException"/> (the browser surface stays until the user closes it).
/// </summary>
public sealed class AndroidBrowserAuthBroker(Context context, IDispatcher dispatcher, Func<Activity?> currentActivity, AndroidExternalAuthCallbacks callbacks) : IWinoAccountBrowserAuthBroker
{
    public async Task<Uri> AuthorizeAsync(Uri authorizationUri, string callbackScheme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        cancellationToken.ThrowIfCancellationRequested();

        // Register the wait before the tab opens; a fast redirect must not race it.
        var wait = callbacks.WaitAsync(cancellationToken);
        try
        {
            await dispatcher.ExecuteOnUIThread(() => Launch(authorizationUri)).ConfigureAwait(false);
        }
        catch
        {
            _ = wait.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }

        return await wait.ConfigureAwait(false);
    }

    private void Launch(Uri authorizationUri)
    {
        var nativeUri = AndroidUri.Parse(authorizationUri.AbsoluteUri)
            ?? throw new InvalidOperationException("The sign-in address is not a valid URL.");
        var activity = currentActivity();
        if (activity is null || activity.IsFinishing)
            throw new InvalidOperationException("Browser sign-in requires a visible activity.");

        var builder = new CustomTabsIntent.Builder().SetShowTitle(true);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && activity.Resources?.DisplayMetrics is { } metrics)
            builder.SetInitialActivityHeightPx((int)(metrics.HeightPixels * 0.85), CustomTabsIntent.ActivityHeightAdjustable);
        var intent = builder.Build().Intent;
        intent.SetData(nativeUri);
        try
        {
            activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            // No Custom Tabs provider: any browser will do; the redirect still comes back through the intent filter.
            activity.StartActivity(new Intent(Intent.ActionView, nativeUri));
        }
    }
}
