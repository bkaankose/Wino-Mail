#if DEBUG
using Android.App;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// Debug-only Settings commands for emulator checks (the iOS SettingsDebug twin), for example
/// <c>adb shell am start -n com.winomail.android/com.winomail.android.SettingsDebug --es command page --es arg MessageListPage</c>.
/// Commands: <c>settings</c> (open the root list), <c>page X</c> (open the Settings page <see cref="WinoPage"/> X),
/// <c>accountpage X</c> (an account sub-page for the first account), <c>close</c>, <c>theme Name</c> (select a predefined theme), <c>mode light|dark|system</c>, <c>accent #RRGGBB</c> and
/// <c>dialog X</c> (<see cref="SettingsDebugDialogs"/>; opened without awaiting, cancel it on screen) and <c>intel signedout|notsub|sub</c>
/// (sample Wino Intelligence state on the open Wino Intelligence or Smart Labels page, the Mac debug bridge twin). Results go to the log.
/// </summary>
[Activity(Name = "com.winomail.android.SettingsDebug", Exported = true, NoHistory = true, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
public sealed class SettingsDebugActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var command = Intent?.GetStringExtra("command") ?? string.Empty;
        var argument = Intent?.GetStringExtra("arg") ?? string.Empty;
        Finish();
        _ = RunAsync(command, argument);
    }

    private static async Task RunAsync(string command, string argument)
    {
        try
        {
            if (MainApplication.Services is not { } services) return;
            var navigation = services.GetRequiredService<AndroidNavigationService>();
            var host = (INavigationHost)navigation;
            var theme = services.GetRequiredService<INewThemeService>();
            switch (command)
            {
                case "settings":
                    await host.ShowSettingsAsync(null, null);
                    break;
                case "page" when Enum.TryParse<WinoPage>(argument, out var page):
                    await host.ShowSettingsAsync(null, null);
                    await navigation.SelectSettingsRootPageAsync(page);
                    break;
                case "accountpage" when Enum.TryParse<WinoPage>(argument, out var accountPage):
                    // Account details first, then the sub-page with the first account's id (what AccountDetails sends).
                    var account = (await services.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault();
                    if (account is null) break;
                    await host.ShowSettingsAsync(null, null);
                    await navigation.SelectSettingsRootPageAsync(WinoPage.AccountDetailsPage, account.Id);
                    await host.ShowSettingsAsync(accountPage, account.Id);
                    break;
                case "close":
                    await host.CloseSettingsAsync();
                    break;
                case "theme":
                    var match = WinoThemeCatalog.PredefinedThemes.FirstOrDefault(item => string.Equals(item.Name, argument, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) await theme.SelectThemeAsync(match.Id);
                    break;
                case "mode":
                    theme.RootTheme = argument switch
                    {
                        "light" => ApplicationElementTheme.Light,
                        "dark" => ApplicationElementTheme.Dark,
                        _ => ApplicationElementTheme.Default
                    };
                    break;
                case "accent":
                    theme.AccentColor = argument;
                    break;
                case "dialog":
                    SettingsDebugDialogs.Open(services, argument);
                    break;
                case "intel":
                    if (navigation.SettingsEntries.LastOrDefault()?.ViewModel is not Wino.Core.ViewModels.WinoAccountManagementPageViewModel intelligence) break;
                    await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => ApplyIntelligenceSample(intelligence, argument));
                    break;
                default:
                    Serilog.Log.Warning("Unknown settings debug command {Command}.", command);
                    break;
            }
            Serilog.Log.Information("Settings debug command {Command} {Argument} done.", command, argument);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Settings debug command {Command} failed.", command); }
    }

    private static void ApplyIntelligenceSample(Wino.Core.ViewModels.WinoAccountManagementPageViewModel vm, string state)
    {
        bool subscribed = state is "sub" or "";
        vm.IsSignedIn = state != "signedout";
        vm.AiPackAddOn.IsPurchased = subscribed;
        vm.HasIntelligenceAccess = subscribed;
        vm.IsConsentGranted = subscribed;
        vm.PurchaseStatusMessage = string.Empty;
        vm.IntelligenceRefreshError = string.Empty;
        vm.IsIntelligenceRefreshing = false;
        if (!subscribed) return;

        vm.SubscriptionRenewalLine = "Renews and usage resets on 1 December 2026";
        vm.ExhaustedBucketsText = string.Format(Wino.Core.Domain.Translator.WinoIntelligence_QuotaExhaustedInfo, Wino.Core.Domain.Translator.WinoIntelligence_UsageBucketTranslate, DateTime.Now.AddDays(10));
        vm.HasExhaustedBuckets = true;
        vm.IsLimitReached = true;
        vm.IsIntelligenceUsageAvailable = true;
        vm.ConsentAcceptedText = string.Format(Wino.Core.Domain.Translator.WinoIntelligence_Policy_Accepted, DateTime.Now.AddDays(-20));
        vm.IntelligenceUsageItems.Clear();
        vm.IntelligenceUsageItems.Add(new Wino.Core.Domain.Models.Intelligence.IntelligenceUsageItem("intelligence", Wino.Core.Domain.Translator.WinoIntelligence_UsageBucketIntelligence, 1284, 2500));
        vm.IntelligenceUsageItems.Add(new Wino.Core.Domain.Models.Intelligence.IntelligenceUsageItem("summarize", Wino.Core.Domain.Translator.WinoIntelligence_UsageBucketSummarize, 36, 200));
        vm.IntelligenceUsageItems.Add(new Wino.Core.Domain.Models.Intelligence.IntelligenceUsageItem("translate", Wino.Core.Domain.Translator.WinoIntelligence_UsageBucketTranslate, 300, 300));
        vm.IntelligenceMailboxes.Clear();
        vm.IntelligenceMailboxes.Add(new Wino.Core.ViewModels.Data.WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@outlook.com", IntelligenceSummary = "8,412 indexed messages", ProviderType = MailProviderType.Outlook, LocalAccountId = Guid.NewGuid(), HasServerIntelligence = true, CanToggle = true, IsEnabled = true, IsInDailyBriefing = true });
        vm.IntelligenceMailboxes.Add(new Wino.Core.ViewModels.Data.WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "team@example.com", IntelligenceSummary = string.Empty, ProviderType = MailProviderType.Gmail, LocalAccountId = Guid.NewGuid(), CanToggle = true });
        vm.IntelligenceMailboxes.Add(new Wino.Core.ViewModels.Data.WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@icloud.com", IntelligenceSummary = string.Empty, ProviderType = MailProviderType.IMAP4, SpecialProvider = SpecialImapProvider.iCloud });
    }
}
#endif
