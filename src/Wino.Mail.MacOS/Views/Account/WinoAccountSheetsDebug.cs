#if DEBUG
using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Debug bridge commands that open the Wino Account sheets for snapshots: <c>signin [busy|error|forgot]</c>,
/// <c>register [filled]</c> and <c>confirmemail</c>. Fields only ever get placeholder text; nothing is submitted.
/// </summary>
internal static class WinoAccountSheetsDebug
{
    private static string? _state;

    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("signin", args => Open(args, () => Services.GetRequiredService<IMailDialogService>().ShowWinoAccountLoginDialogAsync()));
        MacDebugBridge.Register("register", args => Open(args, () => Services.GetRequiredService<IMailDialogService>().ShowWinoAccountRegistrationDialogAsync()));
        MacDebugBridge.Register("confirmemail", args =>
        {
            var window = NSApplication.SharedApplication.KeyWindow ?? NSApplication.SharedApplication.MainWindow ?? NSApplication.SharedApplication.DangerousWindows.ToArray().FirstOrDefault(w => w.IsVisible && !w.IsSheet);
            if (window is null) return Task.FromResult("no window");
            var details = new EmailConfirmationRequiredDetailsDto("debug", "debug", null, DateTimeOffset.UtcNow.AddSeconds(42));
            _ = new WinoAccountEmailConfirmationSheet(Services.GetRequiredService<IWinoAccountProfileService>(), "name@example.com", details).PresentAsync(window);
            return Task.FromResult("ok");
        });
    }

    private static Task<string> Open(string[] args, Func<Task> show)
    {
        _state = args.Length > 0 ? args[0] : "empty";
        _ = show();
        return Task.FromResult("ok");
    }

    /// <summary>Applies the requested debug state once the sheet is on screen.</summary>
    public static void Prepare<T>(WinoAccountSheet<T> sheet, NSTextField email, NSTextField? name = null)
    {
        var state = _state;
        _state = null;
        if (state is null || state == "empty") return;
        email.StringValue = "name@example.com";
        if (name is not null) name.StringValue = "Display name";
        NSTimer.CreateScheduledTimer(0.4, _ => sheet.ShowDebugState(state));
    }
}
#endif
