#if DEBUG
using Android.App;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>
/// Debug-only commands for emulator checks, where no real account can be added (the iOS debug bridge twin):
/// <c>adb shell am start -n com.winomail.android/com.winomail.android.DebugCommand --es command seedmail</c> creates the
/// shared demo account (<see cref="Wino.Mail.ViewModels.Diagnostics.MailDebugSeed"/>: a local IMAP account on a closed
/// loopback port with placeholder test credentials, folders and mails; nothing leaves the device) and opens Mail.
/// <c>--es command seedcalendar</c> adds the shared demo calendars and events
/// (<see cref="Wino.Mail.ViewModels.Diagnostics.CalendarDebugSeed"/>; <c>--es kind ADDRESS</c> picks the account) and opens
/// Calendar. <c>--es command mail</c> only opens Mail; <c>welcome</c> shows the welcome page; <c>seedrecipients</c> adds remembered correspondents for compose suggestions; <c>seedcontacts</c> and <c>seedtasks</c> fill a local address book and two task lists (shared <see cref="Wino.Mail.ViewModels.Diagnostics.ModeDebugSeed"/>) and open the mode. The activity finishes at once; results go to the log
/// (<c>adb logcat -s DOTNET</c>).
/// </summary>
[Activity(Name = "com.winomail.android.DebugCommand", Exported = true, NoHistory = true, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public sealed class DebugCommandActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var command = Intent?.GetStringExtra("command") ?? string.Empty;
        var kind = Intent?.GetStringExtra("kind") ?? string.Empty;
        var seconds = Intent?.GetIntExtra("seconds", 0) ?? 0;
        var minutes = Intent?.GetIntExtra("minutes", 0) ?? 0;
        _ = RunAsync(command, kind, seconds, minutes);
        Finish();
    }

    private static async Task RunAsync(string command, string kind, int seconds, int minutes)
    {
        try
        {
            if (MainApplication.Services is not { } services) return;
            var navigation = services.GetRequiredService<AndroidNavigationService>();
            switch (command)
            {
                case "seedmail":
                    var result = await Wino.Mail.ViewModels.Diagnostics.MailDebugSeed.SeedAsync(services);
                    Serilog.Log.Information("Debug command seedmail: {Result}", result);
                    await navigation.EnsureShellAsync(WinoApplicationMode.Mail);
                    break;
                case "seedrecipients":
                    // Remembered correspondents for the composer's suggestions (the demo seed has mail but no history).
                    var accounts = await services.GetRequiredService<Wino.Core.Domain.Interfaces.IAccountService>().GetAccountsAsync();
                    var history = services.GetRequiredService<Wino.Core.Domain.Interfaces.IRecipientHistoryService>();
                    foreach (var account in accounts)
                        foreach (var (address, name) in new[] { ("ada@example.com", "Ada Lovelace"), ("grace@example.com", "Grace Hopper"), ("alan@example.com", "Alan Turing") })
                            await history.RecordReceivedAsync(account.Id, address, name, DateTime.UtcNow);
                    Serilog.Log.Information("Debug command seedrecipients: {Count} accounts.", accounts.Count);
                    break;
                case "seedcalendar":
                    // Demo calendars and events (shared CalendarDebugSeed; --es arg ADDRESS picks the account), then Calendar.
                    Serilog.Log.Information("Debug command seedcalendar: {Result}",
                        await Wino.Mail.ViewModels.Diagnostics.CalendarDebugSeed.SeedAsync(services, string.IsNullOrEmpty(kind) ? null : kind));
                    await navigation.EnsureShellAsync(WinoApplicationMode.Calendar);
                    navigation.ChangeApplicationMode(WinoApplicationMode.Calendar);
                    break;
                case "seedcontacts":
                    // Contacts and To Do demo data through the shared seed; the mode reopens so its page reloads.
                    Serilog.Log.Information("Debug command seedcontacts: {Result}", await Wino.Mail.ViewModels.Diagnostics.ModeDebugSeed.SeedContactsAsync(services));
                    await ReopenAsync(navigation, WinoApplicationMode.Contacts);
                    break;
                case "seedtasks":
                    Serilog.Log.Information("Debug command seedtasks: {Result}", await Wino.Mail.ViewModels.Diagnostics.ModeDebugSeed.SeedTasksAsync(services));
                    await ReopenAsync(navigation, WinoApplicationMode.Tasks);
                    break;
                case "mail":
                    await navigation.EnsureShellAsync(WinoApplicationMode.Mail);
                    break;
                case "welcome":
                    // The welcome page as onboarding shows it, without removing the accounts (layout checks); "mail" returns.
                    await navigation.NavigateAsync(WinoPage.WelcomePageV2);
                    break;
                case "graceperiod":
                    // --ei seconds N shortens the background grace period (0 restores 10 minutes); applies from the next background.
                    DebugSettings.GracePeriodOverride = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
                    Serilog.Log.Information("Debug command graceperiod: {Seconds} s.", seconds);
                    break;
                case "testnotification":
                    Serilog.Log.Information("Debug command testnotification {Kind}: {Result}", kind, await DebugNotifications.PostTestAsync(services, kind));
                    break;
                case "seedreminder":
                    // --ei minutes N: an event that starts in N + 1 minutes with an N-minute reminder (due in about a minute).
                    Serilog.Log.Information("Debug command seedreminder: {Result}", await DebugNotifications.SeedReminderAsync(services, minutes > 0 ? minutes : 5));
                    break;
                case "briefing":
                    // Layout check of the Daily briefing sheet without the entitlement (the demo account has none);
                    // --es kind fake fills it with the iOS/Mac sample cards. The app bar action itself stays entitlement-gated.
                    Serilog.Log.Information("Debug command briefing: {Result}", await DebugBriefing.ShowAsync(services, kind));
                    break;
                case "briefingstate":
                    var presenter = services.GetRequiredService<Views.Extras.AndroidDailyBriefingPresenter>();
                    Serilog.Log.Information("Debug command briefingstate: available={Available} unseen={Unseen}", presenter.IsAvailable, presenter.HasUnseenItems);
                    break;
                case "backgroundstate":
                    Serilog.Log.Information("Debug command backgroundstate: {Result}", await DebugNotifications.DescribeAsync(services));
                    break;
                default:
                    Serilog.Log.Warning("Unknown debug command {Command}.", command);
                    break;
            }
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug command {Command} failed.", command); }
    }

    private static async Task ReopenAsync(AndroidNavigationService navigation, WinoApplicationMode mode)
    {
        await navigation.EnsureShellAsync(WinoApplicationMode.Mail);
        navigation.ChangeApplicationMode(WinoApplicationMode.Mail);
        await Task.Delay(800);
        navigation.ChangeApplicationMode(mode);
    }
}
#endif
