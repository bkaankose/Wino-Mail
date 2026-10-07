#if DEBUG
using System.Reflection;
using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Views.Extras;

/// <summary>
/// Debug bridge commands for the shell extras, usable before the shell wires its title-bar buttons:
/// <c>briefing</c> toggles the Daily briefing panel on the main window's right edge, <c>briefing fake</c>
/// fills it with sample cards, <c>whatsnew</c> opens the What's New window and <c>account</c> shows the
/// Wino Account popover. Until a presenter has been resolved the provider is read from the bridge.
/// </summary>
internal static class ShellExtrasDebug
{
    public static IServiceProvider? Services { get; set; }

    private static NSViewController? _panel;

    public static void Register()
    {
        MacDebugBridge.Register("briefing", BriefingAsync);
        MacDebugBridge.Register("whatsnew", async _ => { await Resolve<IWhatsNewPresenter>().ShowAsync(); return "ok"; });
        MacDebugBridge.Register("account", _ =>
        {
            var window = MainWindow();
            if (window?.ContentView is not { } content) return Task.FromResult("no window");
            var anchor = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
            content.AddSubview(anchor);
            NSLayoutConstraint.ActivateConstraints(
            [
                anchor.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor, -16),
                anchor.TopAnchor.ConstraintEqualTo(content.TopAnchor, 8),
                anchor.WidthAnchor.ConstraintEqualTo(30), anchor.HeightAnchor.ConstraintEqualTo(30)
            ]);
            content.LayoutSubtreeIfNeeded();
            Resolve<IWinoAccountPresenter>().Show(anchor);
            return Task.FromResult("ok");
        });
    }

    private static T Resolve<T>() where T : notnull
    {
        Services ??= typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider;
        return Services is null ? throw new InvalidOperationException("no service provider") : Services.GetRequiredService<T>();
    }

    private static NSWindow? MainWindow()
        => NSApplication.SharedApplication.MainWindow ?? NSApplication.SharedApplication.DangerousWindows.ToArray().FirstOrDefault(window => window.IsVisible);

    private static Task<string> BriefingAsync(string[] args)
    {
        var presenter = Resolve<IDailyBriefingPresenter>();
        if (args.Length > 0 && args[0] == "fake") return Task.FromResult(Fake(presenter));
        if (_panel is not null)
        {
            _panel.View.RemoveFromSuperview();
            _panel = null;
            return Task.FromResult("closed");
        }
        var window = MainWindow();
        if (window?.ContentView is not { } content) return Task.FromResult("no window");
        var panel = presenter.CreatePanel(() => { _panel?.View.RemoveFromSuperview(); _panel = null; });
        _panel = panel;
        content.AddSubview(panel.View);
        NSLayoutConstraint.ActivateConstraints(
        [
            panel.View.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            panel.View.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            panel.View.TopAnchor.ConstraintEqualTo(content.SafeAreaLayoutGuide.TopAnchor)
        ]);
        return Task.FromResult("opened");
    }

    /// <summary>Sample cards matching the Briefing design board, so the layout can be checked without Intelligence access.</summary>
    private static string Fake(IDailyBriefingPresenter presenter)
    {
        if (presenter is not DailyBriefingPresenter { Panel: { } panel }) return "open the panel first";
        var vm = panel.ViewModel;
        var account = new DailyBriefingAccount(new MailAccount { Id = Guid.NewGuid(), Name = "Sample", Address = "sample@example.com" });
        var now = DateTimeOffset.Now;
        DailyBriefingFact Fact(string headline, string summary, DateTimeOffset at, string priority, string[] labels, MailSmartAction[] actions)
            => new(account.Account.Id, Guid.NewGuid(), Guid.NewGuid().ToString(), "hash", headline, "Sender", "sender@example.com", at, labels, priority, actions, headline, summary, DateTime.UtcNow);
        var facts = new[]
        {
            Fact("Ayşe needs the Q4 milestones by Friday", "She shared roadmap notes and asks you to confirm the macOS release date before the planning call.", now.AddHours(-1), "urgent", ["action"], [new SuggestedReplyAction("Confirm the release date", "asks you to confirm", default)]),
            Fact("PR #2318 waiting for your review", "GitHub: AppKit sidebar parity with account avatars. 2 reviewers requested.", now.AddHours(-3), "normal", ["action", "social"], []),
            Fact("Stripe invoice for September", "The Wino Mail tax invoice for Sep 1 – Sep 30 is ready to download.", now.AddHours(-6), "low", ["finance", "receipt"], []),
            Fact("New sign-in to your OpenAI account", "A Mac in İstanbul signed in. Nothing to do if this was you.", now.AddDays(-1), "high", ["security"], [])
        };
        vm.Items.Clear();
        var index = 0;
        foreach (var fact in facts) vm.Items.Add(new DailyBriefingItem(fact, account) { IsNew = index++ < 2 });
        vm.IsLoading = false;
        vm.IsUnavailable = false;
        vm.IsEmpty = false;
        vm.IsFilteredEmpty = false;
        vm.LoadError = string.Empty;
        return "ok";
    }
}
#endif
