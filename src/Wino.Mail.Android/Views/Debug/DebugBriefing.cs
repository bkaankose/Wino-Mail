#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Extras;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>
/// Debug-only layout check of the Daily briefing sheet (iOS MailDebugCommands "briefing [fake]" twin):
/// <c>DebugCommand --es command briefing [--es kind fake]</c> opens the sheet for the current width class without the
/// entitlement; <c>fake</c> fills it with sample cards. Nothing is written to the intelligence store.
/// </summary>
internal static class DebugBriefing
{
    public static async Task<string> ShowAsync(IServiceProvider services, string kind)
    {
        var activity = services.GetRequiredService<AndroidActivityTracker>().Current;
        if (activity is null) return "no activity";
        var presenter = services.GetRequiredService<AndroidDailyBriefingPresenter>();
        var compact = services.GetRequiredService<AndroidWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        var dispatcher = services.GetRequiredService<IDispatcher>();
        DailyBriefingPanelViewModel? viewModel = null;
        await dispatcher.ExecuteOnUIThread(() => viewModel = presenter.ShowForLayoutCheck(activity, compact));
        if (kind != "fake" || viewModel is null) return compact ? "sheet" : "side sheet";
        // Let the panel's own load finish first, then replace its state with the samples.
        await Task.Delay(1500);
        await dispatcher.ExecuteOnUIThread(() => Fake(viewModel));
        return "fake";
    }

    private static void Fake(DailyBriefingPanelViewModel vm)
    {
        var account = new DailyBriefingAccount(new MailAccount { Id = Guid.NewGuid(), Name = "Sample", Address = "sample@example.com" });
        var now = DateTimeOffset.Now;
        DailyBriefingFact Fact(string headline, string summary, DateTimeOffset at, string priority, List<string> labels, List<MailSmartAction> actions)
            => new(account.Account.Id, Guid.NewGuid(), Guid.NewGuid().ToString(), "hash", headline, "Sender", "sender@example.com", at, labels, priority, actions, headline, summary, DateTime.UtcNow);
        var facts = new[]
        {
            Fact("Ayşe needs the Q4 milestones by Friday", "She shared roadmap notes and asks you to confirm the Android release date before the planning call.", now.AddHours(-1), "urgent", ["action"], [new SuggestedReplyAction("Confirm the release date", "asks you to confirm", default)]),
            Fact("PR #2355 waiting for your review", "GitHub: Android shell parity with the Windows command bar. 2 reviewers requested.", now.AddHours(-3), "normal", ["action", "social"], []),
            Fact("Stripe invoice for September", "The Wino Mail tax invoice for Sep 1 – Sep 30 is ready to download.", now.AddHours(-6), "low", ["finance", "receipt"], []),
            Fact("New sign-in to your account", "A Pixel in İstanbul signed in. Nothing to do if this was you.", now.AddDays(-1), "high", ["security"], [])
        };
        vm.Items.Clear();
        var index = 0;
        foreach (var fact in facts) vm.Items.Add(new DailyBriefingItem(fact, account) { IsNew = index++ < 2 });
        vm.IsLoading = false;
        vm.IsUnavailable = false;
        vm.IsEmpty = false;
        vm.IsFilteredEmpty = false;
        vm.LoadError = string.Empty;
    }
}
#endif
