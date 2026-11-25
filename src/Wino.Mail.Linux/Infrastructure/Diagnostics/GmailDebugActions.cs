#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Authentication;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-gmail-receiver</c> logs the type of the resolved Google authorization receiver; <c>debug-gmail-signin</c>
/// runs it with a request that carries no client id (it checks the receiver, the listener and the waiting dialog, not
/// Google) and logs the fake request's state, then the redirect without query and whether a verifier came back;
/// <c>debug-gmail-cancel</c> cancels it.
/// </summary>
internal static class GmailDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var receiver = Gio.SimpleAction.New("debug-gmail-receiver", null);
        receiver.OnActivate += (sender, args) =>
        {
            if (services() is { } provider)
                Serilog.Log.Information("Debug action: gmail receiver={Type}", provider.GetRequiredService<IGoogleAuthorizationCodeReceiver>().GetType().Name);
        };
        application.AddAction(receiver);

        var signIn = Gio.SimpleAction.New("debug-gmail-signin", null);
        signIn.OnActivate += (sender, args) =>
        {
            if (services() is { } provider) Observe(SignInAsync(provider));
        };
        application.AddAction(signIn);

        var cancel = Gio.SimpleAction.New("debug-gmail-cancel", null);
        cancel.OnActivate += (sender, args) => SignInDebugActions.CancelCurrent();
        application.AddAction(cancel);
    }

    private static async Task SignInAsync(IServiceProvider provider)
    {
        var receiver = provider.GetRequiredService<IGoogleAuthorizationCodeReceiver>();
        using var source = SignInDebugActions.NewCall();
        try
        {
            var code = await Task.Run(() => receiver.ReceiveCodeAsync((redirect, state) =>
            {
                Serilog.Log.Information("Debug action: gmail fake request state={State}", state);
                return new Uri($"https://accounts.google.com/o/oauth2/v2/auth?response_type=code&redirect_uri={Uri.EscapeDataString(redirect.AbsoluteUri)}&state={state}&scope=openid");
            }, source.Token));
            Serilog.Log.Information("Debug action: gmail completed redirect={Redirect} verifier={Verifier}",
                code.RedirectUri.GetLeftPart(UriPartial.Path), string.IsNullOrEmpty(code.CodeVerifier) ? "absent" : "present");
        }
        catch (Exception exception) { SignInDebugActions.LogFailure("gmail sign-in", exception); }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "A Gmail debug action failed."); }
    }
}
#endif
