#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Debug-bridge commands for the Mail pages and the shell extras, usable without an account (the Simulator has no
/// mail server): each page opens on its own, outside the shell routes.
/// <list type="bullet">
/// <item><c>open-compose</c>: the compose page with no draft, as the idiom shows it (full-screen sheet on iPhone, sheet on iPad).</item>
/// <item><c>open-idle [empty|account]</c>: the idle page in a sheet (default, mail-empty or account state).</item>
/// <item><c>open-reader</c>: the reader with no message (command bar defaults, empty body) in a sheet.</item>
/// <item><c>eml-sample</c> / <c>eml-open PATH</c>: a sample or given .eml in the EML reader sheet (exercises the reader).</item>
/// <item><c>eml-activate [PATH]</c>: the .eml activation path (<c>IApplicationShellHost.OpenMailFileAsync</c>).</item>
/// <item><c>seedmail</c>: the demo account (<see cref="MailDebugSeed"/>).</item>
/// <item><c>whatsnew</c>, <c>whatsnew-dump</c>: What's New through its presenter.</item>
/// <item><c>briefing [fake]</c>, <c>briefing-dump</c>: the Daily briefing panel (sheet on iPhone, overlay on iPad); fake fills sample cards.</item>
/// <item><c>close</c>: dismisses the top-most presented sheet.</item>
/// </list>
/// </summary>
internal sealed class MailDebugCommands(IServiceProvider services) : IApplicationLifecycleHook
{
    private static readonly bool IsPhone = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
    private UIViewController? _briefingOverlay;
    private IdlePageViewController? _idle;

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    public Task ServicesReadyAsync()
    {
        UIKitDebugBridge.Register("open-compose", _ => OpenPageAsync<ComposePageViewController>(null, IsPhone ? UIKitPageHost.FullScreenSheet : UIKitPageHost.Sheet, doneButton: false));
        UIKitDebugBridge.Register("open-reader", _ => OpenPageAsync<MailRenderingPageViewController>(null, UIKitPageHost.Sheet, doneButton: true));
        UIKitDebugBridge.Register("open-idle", async args =>
        {
            object? parameter = args.Length > 0 && args[0] == "empty" ? IdlePageViewModel.MailEmptyStateParameter : null;
            var result = await OpenPageAsync<IdlePageViewController>(parameter, UIKitPageHost.Sheet, doneButton: true);
            return result;
        });
        UIKitDebugBridge.Register("idle-dump", _ => OnUIAsync(() => _idle?.Dump() ?? "no idle page"));
        UIKitDebugBridge.Register("eml-sample", async _ =>
        {
            var path = Path.Combine(UIKitDebugBridge.CommandFolder, "sample.eml");
            await File.WriteAllTextAsync(path, SampleMessage, Encoding.UTF8);
            return await EmlReaderSheet.OpenAsync(services, path, await File.ReadAllBytesAsync(path)) ? "ok " + path : "not a message";
        });
        UIKitDebugBridge.Register("eml-open", async args =>
        {
            var path = string.Join(' ', args);
            if (!File.Exists(path)) return "no file " + path;
            return await EmlReaderSheet.OpenAsync(services, path, await File.ReadAllBytesAsync(path)) ? "ok" : "not a message";
        });
        // The .eml activation path (Files, Mail share, AirDrop): IApplicationShellHost.OpenMailFileAsync, as ActivationRouter calls it.
        UIKitDebugBridge.Register("eml-activate", async args =>
        {
            var path = args.Length > 0 ? string.Join(' ', args) : Path.Combine(UIKitDebugBridge.CommandFolder, "sample.eml");
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, SampleMessage, Encoding.UTF8);
            var host = services.GetRequiredService<Wino.Mail.ViewModels.Activation.IApplicationShellHost>();
            return await host.OpenMailFileAsync(path, await File.ReadAllBytesAsync(path)) ? "ok " + path : "not opened";
        });
        UIKitDebugBridge.Register("seedmail", _ => MailDebugSeed.SeedAsync(services));
        UIKitDebugBridge.Register("whatsnew", async _ =>
        {
            await services.GetRequiredService<IUIKitWhatsNewPresenter>().ShowAsync();
            return "ok";
        });
        UIKitDebugBridge.Register("whatsnew-dump", _ => OnUIAsync(() =>
            services.GetRequiredService<WhatsNewPresenter>().Page?.Dump() ?? "no page"));
        UIKitDebugBridge.Register("briefing", args => OnUIAsync(() => Briefing(args)));
        UIKitDebugBridge.Register("briefing-dump", _ => OnUIAsync(() =>
            services.GetRequiredService<DailyBriefingPresenter>().Panel?.Dump() ?? "no panel"));
        UIKitDebugBridge.Register("close", _ => OnUIAsync(() =>
        {
            if (_briefingOverlay is not null) return Briefing([]);
            // The last controller of the window root's presentation chain.
            var current = services.GetRequiredService<IUIKitShellHost>().Window?.RootViewController;
            UIViewController? top = null;
            while (current?.PresentedViewController is { } presented)
            {
                top = presented;
                current = presented;
            }
            if (top?.PresentingViewController is not { } presenting) return "nothing presented";
            presenting.DismissViewController(true, null);
            return "dismissed " + top.GetType().Name;
        }));
        return Task.CompletedTask;
    }

    private async Task<string> OnUIAsync(Func<string> read)
    {
        var result = string.Empty;
        await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => result = read());
        return result;
    }

    /// <summary>Presents a page controller from the container in a sheet and activates it with <paramref name="parameter"/>.</summary>
    private async Task<string> OpenPageAsync<TController>(object? parameter, UIKitPageHost style, bool doneButton) where TController : UIViewController, IWinoViewController
    {
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var host = services.GetRequiredService<IUIKitShellHost>();
        TController? page = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (host.TopViewController is null) return;
            var created = services.GetRequiredService<TController>();
            page = created;
            if (created is IdlePageViewController idle) _idle = idle;
            if (doneButton)
            {
                created.NavigationItem.RightBarButtonItem = new UIBarButtonItem(UIBarButtonSystemItem.Done, (_, _) =>
                {
                    if (created.NavigationController is { } navigation) _ = host.DismissSheetAsync(navigation);
                });
            }
            host.PresentSheet(created, style, () => _ = created.ReleaseAsync());
        });
        if (page is null) return "no window";
        await page.ActivateAsync(NavigationMode.New, parameter);
        return $"ok {typeof(TController).Name} {style}";
    }

    private string Briefing(string[] args)
    {
        var presenter = services.GetRequiredService<DailyBriefingPresenter>();
        if (args.Length > 0 && args[0] == "fake") return Fake(presenter);
        if (IsPhone)
        {
            presenter.PresentSheet();
            return "sheet";
        }
        // iPad: the overlay panel on the trailing edge of the window's root, as the pad shell hosts it.
        if (_briefingOverlay is { } open)
        {
            open.WillMoveToParentViewController(null);
            open.View?.RemoveFromSuperview();
            open.RemoveFromParentViewController();
            _briefingOverlay = null;
            return "closed";
        }
        var root = services.GetRequiredService<IUIKitShellHost>().Window?.RootViewController;
        if (root?.View is not { } content) return "no window";
        var panel = presenter.CreatePanel(() => Briefing([]));
        if (panel.View is not { } panelView) return "no panel view";
        root.AddChildViewController(panel);
        panelView.TranslatesAutoresizingMaskIntoConstraints = false;
        panelView.Layer.CornerRadius = (nfloat)WinoStyle.ZoneRadius;
        panelView.Layer.MaskedCorners = CoreAnimation.CACornerMask.MinXMinYCorner | CoreAnimation.CACornerMask.MinXMaxYCorner;
        panelView.ClipsToBounds = true;
        content.AddSubview(panelView);
        NSLayoutConstraint.ActivateConstraints(
        [
            panelView.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            panelView.TopAnchor.ConstraintEqualTo(content.SafeAreaLayoutGuide.TopAnchor, 8),
            panelView.BottomAnchor.ConstraintEqualTo(content.BottomAnchor, -8),
            panelView.WidthAnchor.ConstraintEqualTo((nfloat)DailyBriefingPanelViewController.PanelWidth)
        ]);
        panel.DidMoveToParentViewController(root);
        _briefingOverlay = panel;
        return "overlay";
    }

    /// <summary>Sample cards matching the Briefing design board (Mac ShellExtrasDebug.Fake).</summary>
    private static string Fake(DailyBriefingPresenter presenter)
    {
        if (presenter.Panel is not { } panel) return "open the panel first";
        var vm = panel.ViewModel;
        var account = new DailyBriefingAccount(new MailAccount { Id = Guid.NewGuid(), Name = "Sample", Address = "sample@example.com" });
        var now = DateTimeOffset.Now;
        DailyBriefingFact Fact(string headline, string summary, DateTimeOffset at, string priority, List<string> labels, List<MailSmartAction> actions)
            => new(account.Account.Id, Guid.NewGuid(), Guid.NewGuid().ToString(), "hash", headline, "Sender", "sender@example.com", at, labels, priority, actions, headline, summary, DateTime.UtcNow);
        var facts = new[]
        {
            Fact("Ayşe needs the Q4 milestones by Friday", "She shared roadmap notes and asks you to confirm the iOS release date before the planning call.", now.AddHours(-1), "urgent", ["action"], [new SuggestedReplyAction("Confirm the release date", "asks you to confirm", default)]),
            Fact("PR #2318 waiting for your review", "GitHub: UIKit reader parity with the Mac command bar. 2 reviewers requested.", now.AddHours(-3), "normal", ["action", "social"], []),
            Fact("Stripe invoice for September", "The Wino Mail tax invoice for Sep 1 – Sep 30 is ready to download.", now.AddHours(-6), "low", ["finance", "receipt"], []),
            Fact("New sign-in to your account", "An iPhone in İstanbul signed in. Nothing to do if this was you.", now.AddDays(-1), "high", ["security"], [])
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

    /// <summary>A multipart message with recipients, an HTML body with a link and a remote image, and two attachments.</summary>
    private const string SampleMessage = """
From: Burak Kaan Köse <burak@example.com>
To: Wino Tester <tester@example.com>, Ayşe Yılmaz <ayse@example.com>
Cc: Design Team <design@example.com>
Subject: Wino Mail for iPhone and iPad: reader preview
Date: Mon, 13 Oct 2026 09:30:00 +0300
Message-ID: <reader-sample-1@winomail.app>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="wino-outer"

--wino-outer
Content-Type: multipart/alternative; boundary="wino-inner"

--wino-inner
Content-Type: text/plain; charset=utf-8

Hello! This is the sample message for the UIKit reader.

--wino-inner
Content-Type: text/html; charset=utf-8

<html><body style="font-family:-apple-system">
<h2>Hello from Wino Mail</h2>
<p>This sample message exercises the <b>UIKit reader</b>: the header, the recipients, the body in the shared
WebKit reader session and the attachment strip.</p>
<p>Links open through the external launcher: <a href="https://www.winomail.app">winomail.app</a>.</p>
<p>A remote image follows; it is blocked until images are allowed:</p>
<p><img src="https://www.winomail.app/images/logo.png" width="64" height="64" alt="Wino logo"></p>
<ul><li>Reply, Reply all and Forward</li><li>Archive, Delete, Move, Flag and Mark read</li><li>Print and Save as PDF</li></ul>
<p>Thanks,<br>Burak</p>
</body></html>

--wino-inner--

--wino-outer
Content-Type: text/plain; charset=utf-8; name="notes.txt"
Content-Disposition: attachment; filename="notes.txt"
Content-Transfer-Encoding: base64

V2lubyBNYWlsIHJlYWRlciBzYW1wbGUgYXR0YWNobWVudC4K

--wino-outer
Content-Type: text/csv; charset=utf-8; name="milestones.csv"
Content-Disposition: attachment; filename="milestones.csv"
Content-Transfer-Encoding: base64

UGhhc2UsRGF0ZQpSZWFkZXIsMjAyNi0xMC0xMwpDb21wb3NlLDIwMjYtMTAtMTQK

--wino-outer--
""";
}
#endif
