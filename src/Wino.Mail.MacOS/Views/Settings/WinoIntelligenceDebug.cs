using AppKit;
#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands for the Intelligence pages: <c>intel signedout|notsub|sub</c> forces a hub state
/// with sample rows, <c>intelquota</c> opens the quota flyout, <c>intelmanage</c> opens the first mailbox's
/// management page and <c>coverage</c> opens its coverage editor. Sample data only lives in the page.
/// </summary>
internal static class WinoIntelligenceDebug
{
    public static WinoAccountManagementPageViewModel? Current { get; set; }
    public static WinoIntelligenceManagementPageViewModel? Management { get; set; }
    public static Action? OpenQuota { get; set; }

    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("intel", args => Task.FromResult(Apply(args.Length > 0 ? args[0] : "sub")));
        MacDebugBridge.Register("infobar", args =>
        {
            var type = args.Length > 0 ? Enum.Parse<InfoBarMessageType>(args[0], true) : InfoBarMessageType.Success;
            Services.GetRequiredService<IMailDialogService>().InfoBarMessage(Translator.GeneralTitle_Info, Translator.WinoIntelligence_Updating, type);
            return Task.FromResult("ok");
        });
        // Shows the consent policy sheet over the Settings window; accepting does nothing.
        MacDebugBridge.Register("consentsheet", args =>
        {
            var window = NSApplication.SharedApplication.DangerousWindows.ToArray().FirstOrDefault(w => w.IsVisible && w.GetType().Name.Contains("Settings"))
                ?? NSApplication.SharedApplication.KeyWindow;
            if (window is null) return Task.FromResult("no window");
            _ = IntelligenceConsentPolicySheet.PresentAsync(window, new Uri("https://www.winomail.app/privacy"), () => Task.FromResult(false));
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("intelquota", _ => { OpenQuota?.Invoke(); return Task.FromResult(OpenQuota is null ? "no page" : "ok"); });
        MacDebugBridge.Register("intelmanage", async _ =>
        {
            var accounts = await Services.GetRequiredService<IAccountService>().GetAccountsAsync();
            // Without accounts the page opens on an unknown id and shows its load error state.
            var id = accounts.Count == 0 ? Guid.NewGuid() : accounts[0].Id;
            return Services.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.WinoIntelligenceManagementPage, id) ? "ok" : "refused";
        });
        MacDebugBridge.Register("coverage", args =>
        {
            if (args.Length > 0 && args[0] == "fake")
                return Task.FromResult(Services.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.IntelligenceCoveragePage, FakeCoverage()) ? "ok" : "refused");
            if (Management is not { } vm) return Task.FromResult("no page");
            if (!vm.OpenCoverageEditorCommand.CanExecute(null)) return Task.FromResult("not ready");
            vm.OpenCoverageEditorCommand.Execute(null);
            return Task.FromResult("ok");
        });
    }

    /// <summary>Sample folders and a two-year inventory for the coverage editor snapshot.</summary>
    private static IntelligenceCoverageEditorArgs FakeCoverage()
    {
        var account = Guid.NewGuid();
        MailItemFolder Folder(string id, string name, SpecialFolderType type = SpecialFolderType.Other, string? parent = null)
            => new() { Id = Guid.NewGuid(), RemoteFolderId = id, FolderName = name, SpecialFolderType = type, ParentRemoteFolderId = parent!, IsSynchronizationEnabled = true, MailAccountId = account };
        var folders = new List<MailItemFolder>
        {
            Folder("inbox", "Inbox", SpecialFolderType.Inbox), Folder("archive", "Archive", SpecialFolderType.Archive), Folder("sent", "Sent Items", SpecialFolderType.Sent),
            Folder("projects", "Projects"), Folder("mac", "macOS release", parent: "projects"), Folder("team", "Wino Team", parent: "projects")
        };
        var counts = new Dictionary<string, int> { ["inbox"] = 6210, ["archive"] = 3944, ["sent"] = 1102, ["projects"] = 0, ["mac"] = 212, ["team"] = 328 };
        var rows = new List<IntelligenceCoverageInventoryRow>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (folder, count) in counts)
            for (int i = 0; i < count; i++)
                rows.Add(new IntelligenceCoverageInventoryRow($"{folder}-{i}", now.AddHours(-i * (17520.0 / Math.Max(1, count)) * (1 + 0.6 * Math.Sin(i * 0.01))), folder));
        var inventory = IntelligenceCoverageInventory.Create(account, rows);
        var indexed = Enumerable.Range(0, 1180).Select(i => $"inbox-{i}").ToHashSet(StringComparer.Ordinal);
        var rules = new[]
        {
            SemanticIndexFolderCoverageRule.Latest("inbox", 2500),
            SemanticIndexFolderCoverageRule.DateRange("archive", SemanticIndexRangePreset.SixMonths, null, null),
            SemanticIndexFolderCoverageRule.Latest("mac", 212)
        };
        return new IntelligenceCoverageEditorArgs(account, folders, inventory, indexed, new HashSet<string>(["inbox", "archive", "mac"], StringComparer.Ordinal), rules, SemanticIndexFolderCoverageRule.Latest(string.Empty, 1000));
    }

    private static string Apply(string state)
    {
        if (Current is not { } vm) return "no page";
        vm.IsSignedIn = state != "signedout";
        vm.AiPackAddOn.IsPurchased = state == "sub";
        vm.HasIntelligenceAccess = state == "sub";
        vm.IsConsentGranted = state == "sub";
        vm.IntelligenceConsentStatusText = state == "sub" ? Translator.WinoAccount_IntelligenceConsentGranted : Translator.WinoAccount_IntelligenceConsentNotGranted;
        vm.PurchaseStatusMessage = string.Empty;
        vm.IntelligenceRefreshError = string.Empty;
        vm.IsIntelligenceRefreshing = false;
        if (state != "sub") return "ok";

        vm.AiPackRenewalOrCancellationText = "Renews 1 November 2026";
        vm.IsIntelligenceUsageAvailable = true;
        vm.IntelligenceResetText = "Resets 1 November";
        vm.IntelligenceUsageItems.Clear();
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("intelligence", Translator.WinoIntelligence_UsageBucketIntelligence, 1284, 5000));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("summarize", Translator.WinoIntelligence_UsageBucketSummarize, 36, 200));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("rewrite", Translator.WinoIntelligence_UsageBucketRewrite, 12, 200));
        vm.IntelligenceUsageItems.Add(new IntelligenceUsageItem("translate", Translator.WinoIntelligence_UsageBucketTranslate, 300, 300));
        vm.IntelligenceMailboxes.Clear();
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@outlook.com", IntelligenceSummary = "8,412 indexed messages • Updated 09:12", ProviderType = MailProviderType.Outlook, LocalAccountId = Guid.NewGuid(), HasServerIntelligence = true, CanToggle = true, IsEnabled = true });
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "team@example.com", IntelligenceSummary = Translator.WinoAccount_Management_NoIntelligenceData, ProviderType = MailProviderType.Gmail, LocalAccountId = Guid.NewGuid(), CanToggle = true });
        vm.IntelligenceMailboxes.Add(new WinoIntelligenceMailboxItemViewModel { MailboxId = Guid.NewGuid(), Address = "name@icloud.com", IntelligenceSummary = Translator.WinoAccount_Management_IntelligenceManageUnavailable, ProviderType = MailProviderType.IMAP4, SpecialProvider = SpecialImapProvider.iCloud });
        return "ok";
    }
}
#endif
