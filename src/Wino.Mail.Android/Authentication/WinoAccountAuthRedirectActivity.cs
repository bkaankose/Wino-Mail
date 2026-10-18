using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.Accounts;
using Wino.Platform.Android.Authentication;

namespace Wino.Mail.Android.Authentication;

/// <summary>
/// Receives the Wino Account browser sign-in redirect (<c>wino://auth-complete?code=…</c>, the address the API's
/// <c>Secrets:ExternalAuth:AppCallbackUri</c> names; the client spells it once in <see cref="WinoAccountExternalAuth"/>)
/// and hands it to the waiting <see cref="AndroidExternalAuthCallbacks"/>. It then brings the single-task
/// <see cref="MainActivity"/> back without recreating it, so the sign-in dialog that started the flow is still open.
/// </summary>
[Activity(Exported = true, NoHistory = true, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter([Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = WinoAccountExternalAuth.CallbackScheme,
    DataHost = WinoAccountExternalAuth.CallbackHost)]
public sealed class WinoAccountAuthRedirectActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var delivered = MainApplication.Services?.GetService<AndroidExternalAuthCallbacks>()?.Deliver(Intent?.Data) == true;
        if (!delivered) Serilog.Log.Warning("A Wino Account sign-in redirect arrived while nothing was waiting for it.");

        // ClearTop|SingleTop pops the Custom Tab above the existing MainActivity instance (OnNewIntent, no recreation).
        StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
        Finish();
    }
}
