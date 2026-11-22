#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Platform.Linux.Authentication;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-outlook-signin 'hint'</c> runs the Outlook host's interactive sign-in for mail scopes and logs presence,
/// scope count and expiry only (no token, no address), or the exception type and MSAL error code;
/// <c>debug-outlook-cancel</c> cancels it; <c>debug-outlook-cache</c> logs the cached account count and whether the
/// secure item exists.
/// </summary>
internal static class OutlookDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var signIn = Gio.SimpleAction.New("debug-outlook-signin", GLib.VariantType.New("s"));
        signIn.OnActivate += (sender, args) =>
        {
            if (services() is not { } provider) return;
            Observe(SignInAsync(provider, args.Parameter?.GetString(out _) ?? string.Empty));
        };
        application.AddAction(signIn);

        var cancel = Gio.SimpleAction.New("debug-outlook-cancel", null);
        cancel.OnActivate += (sender, args) => SignInDebugActions.CancelCurrent();
        application.AddAction(cancel);

        var cache = Gio.SimpleAction.New("debug-outlook-cache", null);
        cache.OnActivate += (sender, args) =>
        {
            if (services() is { } provider) Observe(CacheAsync(provider));
        };
        application.AddAction(cache);
    }

    private static async Task SignInAsync(IServiceProvider provider, string hint)
    {
        var host = provider.GetRequiredService<IOutlookAuthenticationHost>();
        var scopes = provider.GetRequiredService<IAuthenticatorConfig>().GetOutlookScopes(new ProviderAuthorizationRequest(true, false, []));
        using var source = SignInDebugActions.NewCall();
        try
        {
            var result = await Task.Run(() => host.AcquireTokenInteractiveAsync(scopes, null, hint, source.Token));
            Serilog.Log.Information("Debug action: outlook sign-in completed account={Account} scopes={Scopes} expires={Expires:u}",
                result.Account is null ? "absent" : "present", result.Scopes.Count(), result.ExpiresOn.UtcDateTime);
        }
        catch (MsalException exception)
        {
            Serilog.Log.Information("Debug action: outlook sign-in failed type={Type} code={Code}", exception.GetType().Name, exception.ErrorCode);
        }
        catch (Exception exception) { SignInDebugActions.LogFailure("outlook sign-in", exception); }
    }

    private static async Task CacheAsync(IServiceProvider provider)
    {
        var host = provider.GetRequiredService<IOutlookAuthenticationHost>();
        await host.EnsureTokenCacheAttachedAsync();
        var accounts = await host.Client.GetAccountsAsync();
        var stored = provider.GetRequiredService<ISecureItemStore>().Read(LinuxOutlookAuthenticationHost.CacheScope, LinuxOutlookAuthenticationHost.CacheAccount) is { Length: > 0 };
        Serilog.Log.Information("Debug action: outlook cache accounts={Count} item={Item}", accounts.Count(), stored ? "present" : "absent");
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "An Outlook debug action failed."); }
    }
}
#endif
