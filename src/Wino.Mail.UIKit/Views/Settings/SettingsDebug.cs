#if DEBUG
using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
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
}
#endif
