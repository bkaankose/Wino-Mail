#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.Linux.Views.Settings.Intelligence;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-intelligence</c> (<c>as</c>), the Linux twin of the macOS intel bridge commands:
/// <c>['open', 'home'|'classification'|'manage'|'coverage-fake']</c> opens a page in Settings (manage: first account;
/// coverage-fake: the editor over sample folders and a two-year inventory); <c>['access', 'on'|'off']</c> lets the
/// sub-pages open without an entitlement; <c>['fake', 'signedout'|'notsub'|'sub']</c> forces a hub state with sample rows
/// on the open page's ViewModel (the real views render it; nothing reaches the backend); <c>['consent', 'ok'|'fail']</c>
/// opens the consent dialog over Settings with an accept that answers the parameter; <c>['consent-ack', 'on'|'off']</c>,
/// <c>['consent-accept']</c>, <c>['consent-close']</c>; <c>['expand']</c> opens the home page's expanders; <c>['coverage-select'|'coverage-preset', i]</c>,
/// <c>['coverage-range', start, end]</c>, <c>['coverage-mode', i]</c>, <c>['coverage-shot', png]</c> (the editor card), <c>['coverage-editor-only', 'on'|'off']</c>; <c>['state', path]</c> writes the open page's state.
/// </summary>
internal static class IntelligenceDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-intelligence", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            Observe(RunAsync(services, parts));
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var presenter = provider.GetRequiredService<SettingsWindowPresenter>();
        var shell = provider.GetRequiredService<IApplicationShellHost>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        var result = "ok";
        switch (op)
        {
            case "open":
                switch (value)
                {
                    case "home": await shell.ShowSettingsAsync(WinoPage.WinoIntelligencePage, null); break;
                    case "classification": await shell.ShowSettingsAsync(WinoPage.MailClassificationPage, null); break;
                    case "manage":
                    {
                        var accounts = await provider.GetRequiredService<IAccountService>().GetAccountsAsync();
                        // Without accounts the page opens on an unknown id and shows its load error state.
                        await shell.ShowSettingsAsync(WinoPage.WinoIntelligenceManagementPage, accounts.Count == 0 ? Guid.NewGuid() : accounts[0].Id);
                        break;
                    }
                    case "coverage-fake": await shell.ShowSettingsAsync(WinoPage.IntelligenceCoveragePage, FakeCoverage()); break;
                    default: throw new ArgumentException($"Unknown page '{value}'.");
                }
                break;
            case "access":
                await dispatcher.ExecuteOnUIThread(() => presenter.DebugIntelligenceAccess = value == "on");
                break;
            case "fake":
                await dispatcher.ExecuteOnUIThread(() => result = presenter.CurrentView?.AssociatedViewModel is WinoAccountManagementPageViewModel vm ? Apply(vm, value) : "no page");
                break;
            case "consent":
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (presenter.Window?.Window is not { } window) { result = "no window"; return; }
                    var ok = value == "ok";
                    Observe(LogAsync(IntelligenceConsentDialog.PresentAsync(window, new Uri(AppUrls.PrivacyPolicy),
                        provider.GetRequiredService<IExternalLauncher>(), () => Task.FromResult(ok))));
                });
                break;
            case "expand":
                await dispatcher.ExecuteOnUIThread(() => WinoIntelligencePage.Current?.DebugExpandAll());
                break;
            case "consent-ack":
                await dispatcher.ExecuteOnUIThread(() => IntelligenceConsentDialog.Acknowledgement?.SetActive(value == "on"));
                break;
            case "consent-accept":
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    // Activate would emit clicked on an insensitive button too; a user cannot, so neither may the action.
                    if (IntelligenceConsentDialog.Current?.GetDefaultWidget() is Gtk.Button accept) result = accept.GetSensitive() && accept.Activate() ? "activated" : "insensitive";
                    else result = "no dialog";
                });
                break;
            case "consent-close":
                await dispatcher.ExecuteOnUIThread(() => IntelligenceConsentDialog.Current?.Close());
                break;
            case "coverage-select" when int.TryParse(value, out var index):
                await dispatcher.ExecuteOnUIThread(() => IntelligenceCoveragePage.Current?.DebugSelect(index));
                break;
            case "coverage-preset" when int.TryParse(value, out var index):
                await dispatcher.ExecuteOnUIThread(() => IntelligenceCoveragePage.Current?.DebugPreset(index));
                break;
            case "coverage-shot" when value.Length > 0:
                await dispatcher.ExecuteOnUIThread(() => IntelligenceCoveragePage.Current?.DebugCapture(value));
                break;
            case "coverage-editor-only":
                await dispatcher.ExecuteOnUIThread(() => IntelligenceCoveragePage.Current?.DebugEditorOnly(value == "on"));
                break;
            case "coverage-mode" when int.TryParse(value, out var index):
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    if (IntelligenceCoveragePage.Current is { } page) page.ViewModel.CoverageModeIndex = index;
                });
                break;
            case "coverage-range" when parts.Length > 2 && int.TryParse(value, out var start) && int.TryParse(parts[2], out var end):
                await dispatcher.ExecuteOnUIThread(() => IntelligenceCoveragePage.Current?.DebugSetRange(start, end));
                break;
            case "state" when value.Length > 0:
            {
                var text = string.Empty;
                await dispatcher.ExecuteOnUIThread(() => text = presenter.CurrentView switch
                {
                    WinoIntelligencePage page => "home " + page.Describe(),
                    MailClassificationPage page => "classification " + page.Describe(),
                    WinoIntelligenceManagementPage page => "manage " + page.Describe(),
                    IntelligenceCoveragePage page => "coverage " + page.Describe(),
                    _ => $"page={presenter.CurrentPage?.ToString() ?? "-"}"
                } + $"\naccess={presenter.DebugIntelligenceAccess} consentDialog={IntelligenceConsentDialog.Current is not null}");
                await File.WriteAllTextAsync(value, text + Environment.NewLine);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-intelligence operation '{op}'.");
        }
        Serilog.Log.Information("Debug action: intelligence {Operation} {Value}: {Result}.", op, value, result);
    }

    private static async Task LogAsync(Task<bool> dialog)
        => Serilog.Log.Information("Debug action: intelligence consent dialog result={Result}.", await dialog);

    /// <summary>Port of the macOS sample state: ViewModel properties only, so every row is drawn by the real page.</summary>
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

        vm.AiPackRenewalOrCancellationText = "Renews 1 December 2026";
        vm.SubscriptionRenewalLine = "Renews and usage resets on 1 December 2026";
        vm.MailClassificationSummary = string.Format(Translator.WinoIntelligence_MailClassification_Summary, 1, 3);
        vm.ExhaustedBucketsText = string.Format(Translator.WinoIntelligence_QuotaExhaustedInfo, Translator.WinoIntelligence_UsageBucketTranslate, DateTime.Now.AddDays(10));
        vm.HasExhaustedBuckets = true;
        vm.IsLimitReached = true;
        vm.IsIntelligenceUsageAvailable = true;
        vm.IntelligenceResetText = "Resets 1 December";
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

    /// <summary>Sample folders and a two-year inventory for the coverage editor (macOS FakeCoverage).</summary>
    private static IntelligenceCoverageEditorArgs FakeCoverage()
    {
        var account = Guid.NewGuid();
        MailItemFolder Folder(string id, string name, SpecialFolderType type = SpecialFolderType.Other, string? parent = null)
            => new() { Id = Guid.NewGuid(), RemoteFolderId = id, FolderName = name, SpecialFolderType = type, ParentRemoteFolderId = parent ?? string.Empty, IsSynchronizationEnabled = true, MailAccountId = account };
        var folders = new List<MailItemFolder>
        {
            Folder("inbox", "Inbox", SpecialFolderType.Inbox), Folder("archive", "Archive", SpecialFolderType.Archive), Folder("sent", "Sent Items", SpecialFolderType.Sent),
            Folder("projects", "Projects"), Folder("linux", "Linux release", parent: "projects"), Folder("team", "Wino Team", parent: "projects")
        };
        var counts = new Dictionary<string, int> { ["inbox"] = 6210, ["archive"] = 3944, ["sent"] = 1102, ["projects"] = 0, ["linux"] = 212, ["team"] = 328 };
        var rows = new List<IntelligenceCoverageInventoryRow>();
        var now = DateTimeOffset.UtcNow;
        foreach (var (folder, count) in counts)
            for (var i = 0; i < count; i++)
                rows.Add(new IntelligenceCoverageInventoryRow($"{folder}-{i}", now.AddHours(-i * (17520.0 / Math.Max(1, count)) * (1 + 0.6 * Math.Sin(i * 0.01))), folder));
        var inventory = IntelligenceCoverageInventory.Create(account, rows);
        var indexed = Enumerable.Range(0, 1180).Select(i => $"inbox-{i}").ToHashSet(StringComparer.Ordinal);
        var rules = new[]
        {
            SemanticIndexFolderCoverageRule.Latest("inbox", 2500),
            SemanticIndexFolderCoverageRule.DateRange("archive", SemanticIndexRangePreset.SixMonths, null, null),
            SemanticIndexFolderCoverageRule.Latest("linux", 212)
        };
        return new IntelligenceCoverageEditorArgs(account, folders, inventory, indexed, new HashSet<string>(["inbox", "archive", "linux"], StringComparer.Ordinal), rules, SemanticIndexFolderCoverageRule.Latest(string.Empty, 1000));
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-intelligence failed."); }
    }
}
#endif
