#if DEBUG
using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Debug bridge commands for Settings (Debug builds only): <c>settingspage WinoPage</c> opens Settings and pushes a page,
/// <c>dialog NAME</c> opens a dialog without waiting for it (alias, category, signature, newlist, reorder, login,
/// register, secret, certificate, purchase, shortcut, text), and <c>dismiss</c> closes whatever is presented on top.
/// </summary>
internal static class SettingsDebug
{
    public static void Register(IServiceProvider services)
    {
        UIKitDebugBridge.Register("settingspage", args =>
        {
            if (args.Length == 0 || !Enum.TryParse<WinoPage>(args[0], true, out var page)) return Task.FromResult("usage: settingspage WinoPage");
            var navigation = services.GetRequiredService<UIKitNavigationService>();
            navigation.ChangeApplicationMode(WinoApplicationMode.Settings);
            return Task.FromResult(navigation.Navigate(page) ? "ok" : "no route");
        });

        // settingsaccount [General|Mail|Calendar|People|ToDo]: account details of the first account on a tab.
        UIKitDebugBridge.Register("settingsaccount", async args =>
        {
            var tab = args.Length > 0 && Enum.TryParse<AccountDetailsTab>(args[0], true, out var parsed) ? parsed : AccountDetailsTab.General;
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
            if (accounts.FirstOrDefault() is not { } account) return "no account";
            string result = "no route";
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                var navigation = services.GetRequiredService<UIKitNavigationService>();
                navigation.ChangeApplicationMode(WinoApplicationMode.Settings);
                result = navigation.Navigate(WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(account.Id, tab)) ? "ok" : "no route";
            });
            return result;
        });

        // settingsscroll Y: scrolls the visible Settings page to Y points (bottom: the end).
        UIKitDebugBridge.Register("settingsscroll", async args =>
        {
            string result = "no scroll view";
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                if (VisibleSettingsPage(services) is not { } page || FindFirst<UIScrollView>(page.View) is not { } scroll) return;
                var bottom = Math.Max(0, scroll.ContentSize.Height - scroll.Bounds.Height + scroll.AdjustedContentInset.Bottom);
                var y = args.Length > 0 && double.TryParse(args[0], out var value) ? Math.Min(value, bottom) : bottom;
                scroll.SetContentOffset(new CoreGraphics.CGPoint(0, y - scroll.AdjustedContentInset.Top), false);
                result = $"ok {y:0}/{bottom:0}";
            });
            return result;
        });

        // settingsdump [DEPTH]: frames of the visible Settings page's views, for layout checks.
        UIKitDebugBridge.Register("settingsdump", async args =>
        {
            var depth = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 8;
            var lines = new System.Text.StringBuilder();
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                void Dump(global::UIKit.UIView view, int level)
                {
                    if (level > depth) return;
                    lines.Append(' ', level * 2).Append(view.Hidden ? "[hidden] " : string.Empty).Append(view.GetType().Name).Append(' ').Append(view.Frame.ToString());
                    foreach (var constraint in view.Constraints)
                        if (constraint.FirstAttribute == global::UIKit.NSLayoutAttribute.Width && constraint.SecondItem is null && constraint.Priority >= 990)
                            lines.Append($" w{constraint.Relation}{constraint.Constant:0}@{constraint.Priority:0}");
                    lines.Append('\n');
                    foreach (var child in view.Subviews) Dump(child, level + 1);
                }
                if (VisibleSettingsPage(services)?.View is { } root)
                {
                    Dump(root, 0);
                    if (FindFirst<global::UIKit.UIScrollView>(root)?.Subviews.FirstOrDefault(view => view is global::UIKit.UIStackView) is { } page)
                        foreach (var constraint in page.GetConstraintsAffectingLayout(global::UIKit.UILayoutConstraintAxis.Horizontal))
                            lines.Append("H: ").Append(constraint.DebugDescription).Append('\n');
                }
            });
            return lines.ToString();
        });

        // settingsexpand N: toggles the Nth (0-based) visible expander of the visible Settings page.
        UIKitDebugBridge.Register("settingsexpand", async args =>
        {
            var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            string result = "no expander";
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                if (VisibleSettingsPage(services)?.View is not { } root) return;
                var expanders = new List<Wino.Mail.Controls.UIKit.Settings.WinoSettingsExpander>();
                Collect(root, expanders);
                if (index < 0 || index >= expanders.Count) { result = $"only {expanders.Count} expanders"; return; }
                expanders[index].IsExpanded = !expanders[index].IsExpanded;
                result = $"ok {expanders.Count} expanders";
            });
            return result;
        });

        UIKitDebugBridge.Register("dialog", args =>
        {
            var dialogs = services.GetRequiredService<IMailDialogService>();
            var name = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
            Task? shown = name switch
            {
                "alias" => dialogs.ShowCreateAccountAliasDialogAsync(),
                "category" => dialogs.ShowEditMailCategoryDialogAsync(),
                "signature" => dialogs.ShowSignatureEditorDialog(),
                "newlist" => dialogs.ShowNewContactListDialogAsync(new List<Wino.Core.Domain.Entities.Shared.MailAccount>(), null),
                "reorder" => dialogs.ShowAccountReorderDialogAsync(new ObservableCollection<IAccountProviderDetailViewModel>()),
                "login" => dialogs.ShowWinoAccountLoginDialogAsync(),
                "register" => dialogs.ShowWinoAccountRegistrationDialogAsync(),
                "secret" => dialogs.ShowWinoAccountSyncSecretDialogAsync(new SyncSnapshotSecretRequest(true, false, true)),
                "certificate" => dialogs.ShowServerCertificateTrustDialogAsync("Subject: CN=imap.example.test\nIssuer: CN=Example Test CA\nValid: 2026-01-01 – 2027-01-01", []),
                "purchase" => dialogs.ShowUnlimitedAccountsPurchaseChannelDialogAsync(),
                "shortcut" => dialogs.ShowKeyboardShortcutDialogAsync(),
                "text" => dialogs.ShowTextInputDialogAsync(string.Empty, "Rename", "Debug text input", "Save"),
                _ => null
            };
            if (shown is null) return Task.FromResult("unknown dialog " + name);
            _ = shown.ContinueWith(task => Serilog.Log.Information("Debug dialog {Name} finished: {Status}", name, task.Status), TaskScheduler.Default);
            return Task.FromResult("ok");
        });

        // hittest X Y: the view the window hit-tests at X,Y (points) and its ancestors with their gesture recognizers,
        // for checking that a settings control receives its touches.
        UIKitDebugBridge.Register("hittest", async args =>
        {
            var lines = new System.Text.StringBuilder();
            if (args.Length < 2 || !double.TryParse(args[0], out var x) || !double.TryParse(args[1], out var y)) return "usage: hittest X Y";
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                var window = services.GetRequiredService<IUIKitShellHost>().TopViewController?.View?.Window;
                var hit = window?.HitTest(new CoreGraphics.CGPoint(x, y), null);
                for (var view = hit; view is not null; view = view.Superview)
                {
                    lines.Append(view.GetType().Name).Append(' ').Append(view.Frame.ToString()).Append(view.UserInteractionEnabled ? string.Empty : " [no-interaction]");
                    foreach (var recognizer in view.GestureRecognizers ?? [])
                        lines.Append(" {").Append(recognizer.GetType().Name).Append(recognizer.Enabled ? string.Empty : " off").Append(recognizer.CancelsTouchesInView ? " cancels" : string.Empty).Append('}');
                    lines.Append('\n');
                }
            });
            return lines.Length == 0 ? "nothing hit" : lines.ToString();
        });

        UIKitDebugBridge.Register("iconsize", _ =>
        {
            var tinted = Wino.Presentation.UIKit.WinoIcons.Image(WinoIconGlyph.Settings, 20, global::UIKit.UIColor.SystemBlue);
            var template = Wino.Presentation.UIKit.WinoIcons.Image(WinoIconGlyph.Settings, 20);
            return Task.FromResult($"tinted {tinted.Size} @{tinted.CurrentScale} template {template.Size} @{template.CurrentScale}");
        });

        UIKitDebugBridge.Register("dismiss", async _ =>
        {
            var host = services.GetRequiredService<IUIKitShellHost>();
            string result = "nothing presented";
            await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            {
                if (host.TopViewController is { PresentingViewController: { } presenting })
                {
                    presenting.DismissViewController(true, null);
                    result = "ok";
                }
            });
            return result;
        });
    }

    private static global::UIKit.UIViewController? VisibleSettingsPage(IServiceProvider services)
    {
        var top = services.GetRequiredService<IUIKitShellHost>().TopViewController;
        return top is global::UIKit.UINavigationController navigation ? navigation.TopViewController : top;
    }

    private static T? FindFirst<T>(global::UIKit.UIView? view) where T : global::UIKit.UIView
    {
        if (view is null) return null;
        if (view is T match) return match;
        foreach (var child in view.Subviews)
            if (FindFirst<T>(child) is { } found) return found;
        return null;
    }

    private static void Collect<T>(global::UIKit.UIView view, List<T> found) where T : global::UIKit.UIView
    {
        if (view.Hidden) return;
        if (view is T match) found.Add(match);
        foreach (var child in view.Subviews) Collect(child, found);
    }
}
#endif
