#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Debug bridge commands for the Wino Intelligence pages (the Mac WinoIntelligenceDebug): <c>intel signedout|notsub|sub</c>
/// forces the visible Intelligence or Smart Labels page into a state with sample rows, and <c>intelcmd NAME</c> runs that
/// page's NAMECommand (OpenMailClassification, OpenDailyBriefing, OpenIntelligencePage, OpenConsentPolicy, RefreshPurchases), and
/// <c>intelsmartlabels</c> pushes the Smart Labels page without the access check. Sample data only lives in the page.
/// </summary>
internal static class WinoIntelligenceDebug
{
    public static void Register(IServiceProvider services)
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();
        UIKitDebugBridge.Register("intel", async args =>
        {
            var result = "no page";
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (VisibleViewModel(services) is { } vm) result = Apply(vm, args.Length > 0 ? args[0] : "sub");
            });
            return result;
        });
        UIKitDebugBridge.Register("intelsmartlabels", _ => PushSmartLabelsAsync(services));
        UIKitDebugBridge.Register("intelcmd", async args =>
        {
            if (args.Length == 0) return "usage: intelcmd NAME";
            var result = "no page";
            await dispatcher.ExecuteOnUIThread(() =>
            {
                if (VisibleViewModel(services) is not { } vm) return;
                System.Windows.Input.ICommand? command = args[0] switch
                {
                    "OpenMailClassification" => vm.OpenMailClassificationCommand,
                    "OpenDailyBriefing" => vm.OpenDailyBriefingCommand,
                    "OpenIntelligencePage" => vm.OpenIntelligencePageCommand,
                    "OpenConsentPolicy" => vm.OpenConsentPolicyCommand,
                    "RefreshPurchases" => vm.RefreshPurchasesCommand,
                    _ => null
                };
                if (command is null) { result = "no command " + args[0]; return; }
                if (!command.CanExecute(null)) { result = "cannot execute"; return; }
                command.Execute(null);
                result = "ok";
            });
            return result;
        });
    }

    /// <summary>Pushes the Smart Labels page past the router's access check, so the page can be checked without a subscription.</summary>
    private static async Task<string> PushSmartLabelsAsync(IServiceProvider services)
    {
        IWinoViewController? routed = null;
        await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
        {
            if (services.GetRequiredService<IUIKitShellHost>().TopViewController is not global::UIKit.UINavigationController sheet) return;
            var page = services.GetRequiredService<MailClassificationPageViewController>();
            sheet.PushViewController(page, true);
            routed = page;
        });
        if (routed is null) return "no settings sheet";
        await routed.ActivateAsync(Wino.Core.Domain.Models.Navigation.NavigationMode.New, null);
        return "ok";
    }

    private static WinoAccountManagementPageViewModel? VisibleViewModel(IServiceProvider services)
    {
        var top = services.GetRequiredService<IUIKitShellHost>().TopViewController;
        var page = top is global::UIKit.UINavigationController navigation ? navigation.TopViewController : top;
        return (page as IViewModelHost)?.AssociatedViewModel as WinoAccountManagementPageViewModel;
    }

    private static string Apply(WinoAccountManagementPageViewModel vm, string state)
    {
        vm.IsSignedIn = state != "signedout";
        vm.AiPackAddOn.IsPurchased = state == "sub";
        vm.HasIntelligenceAccess = state == "sub";
        vm.IsConsentGranted = state == "sub";
        vm.IntelligenceConsentStatusText = state == "sub" ? Translator.WinoAccount_IntelligenceConsentGranted : Translator.WinoAccount_IntelligenceConsentNotGranted;
        vm.PurchaseStatusMessage = string.Empty;
        vm.IntelligenceRefreshError = string.Empty;
        vm.IsIntelligenceRefreshing = false;
        if (state != "sub") return "ok";

        vm.ConsentAcceptedText = "Accepted on 1 October 2026";
        vm.AiPackRenewalOrCancellationText = "Renews 1 December 2026";
        vm.SubscriptionRenewalLine = "Renews and usage resets on 1 December 2026";
        vm.ExhaustedBucketsText = string.Format(Translator.WinoIntelligence_QuotaExhaustedInfo, Translator.WinoIntelligence_UsageBucketTranslate, DateTime.Now.AddDays(10));
        vm.HasExhaustedBuckets = true;
        vm.IsLimitReached = true;
        vm.IsIntelligenceUsageAvailable = true;
        vm.IntelligenceUsageItems.Clear();
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("intelligence", Translator.WinoIntelligence_UsageBucketIntelligence, 1284, 5000));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("summarize", Translator.WinoIntelligence_UsageBucketSummarize, 36, 200));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("rewrite", Translator.WinoIntelligence_UsageBucketRewrite, 12, 200));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("translate", Translator.WinoIntelligence_UsageBucketTranslate, 300, 300));
        vm.IntelligenceMailboxes.Clear();
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@outlook.com", IntelligenceSummary = "8,412 indexed messages • Updated 09:12", ProviderType = MailProviderType.Outlook, LocalAccountId = Guid.NewGuid(), HasServerIntelligence = true, CanToggle = true, IsEnabled = true, IsInDailyBriefing = true });
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "team@example.com", IntelligenceSummary = Translator.WinoAccount_Management_NoIntelligenceData, ProviderType = MailProviderType.Gmail, LocalAccountId = Guid.NewGuid(), CanToggle = true });
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@icloud.com", IntelligenceSummary = Translator.WinoAccount_Management_IntelligenceManageUnavailable, ProviderType = MailProviderType.IMAP4, SpecialProvider = SpecialImapProvider.iCloud });
        return "ok";
    }
}
#endif
