#if DEBUG
using System.Collections.ObjectModel;
using AppKit;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Debug bridge commands that open the IMAP onboarding pages for snapshots without typing anything:
/// <c>onboarding-special [NAME]</c> opens the credentials page for a catalog provider (iCloud by
/// default), <c>onboarding-imap</c> opens the custom server page as the wizard does,
/// <c>onboarding-imap-edit</c> opens it for the first IMAP account (from Settings), and
/// <c>imap-failure</c> shows the validation-failed sheet with a sample protocol log.
/// <c>setup-preview [progress|failed|done] [light|dark]</c> opens the account setup progress page in a
/// preview window with sample steps (no account is created) and <c>setup-preview-close</c> closes it.
/// The provider page adds <c>provider NAME</c> and friends; the server page adds <c>imap-step</c>.
/// </summary>
internal static class OnboardingDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("onboarding-special", args =>
        {
            var query = args.Length > 0 ? string.Join(' ', args) : nameof(SpecialImapProvider.iCloud);
            var provider = Services.GetRequiredService<IKnownImapProviderCatalog>().GetAvailableProviders().FirstOrDefault(item =>
                item.SpecialImapProvider != SpecialImapProvider.None &&
                (string.Equals(item.SpecialImapProvider.ToString(), query, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(item.Name, query, StringComparison.OrdinalIgnoreCase)));
            if (provider is null) return Task.FromResult("no catalog provider " + query);
            var context = Services.GetRequiredService<WelcomeWizardContext>();
            context.SelectedProvider = provider;
            context.AccountName ??= provider.Name;
            context.IsMailAccessEnabled = true;
            return Task.FromResult(Navigate(WinoPage.SpecialImapCredentialsPage, null));
        });
        MacDebugBridge.Register("onboarding-imap", args =>
        {
            var type = args.Length > 0 && args[0].Equals("pop3", StringComparison.OrdinalIgnoreCase) ? MailProviderType.POP3 : MailProviderType.IMAP4;
            var provider = Services.GetRequiredService<IKnownImapProviderCatalog>().GetAvailableProviders()
                .FirstOrDefault(item => item.Type == type && item.SpecialImapProvider == SpecialImapProvider.None);
            if (provider is null) return Task.FromResult("no custom provider");
            var context = Services.GetRequiredService<WelcomeWizardContext>();
            context.SelectedProvider = provider;
            context.AccountName ??= "IMAP";
            context.IsMailAccessEnabled = true;
            return Task.FromResult(Navigate(WinoPage.ImapCalDavSettingsPage,
                ImapCalDavSettingsNavigationContext.CreateForWizardMode(context.BuildAccountCreationDialogResult())));
        });
        MacDebugBridge.Register("onboarding-imap-edit", async _ =>
        {
            var accounts = await Services.GetRequiredService<IAccountService>().GetAccountsAsync();
            var account = accounts.FirstOrDefault(item => item.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3);
            if (account is null) return "no IMAP account";
            return Navigate(WinoPage.ImapCalDavSettingsPage, ImapCalDavSettingsNavigationContext.CreateForEditMode(account.Id));
        });
        MacDebugBridge.Register("setup-preview", async args =>
        {
            var state = args.Length > 0 ? args[0].ToLowerInvariant() : "progress";
            var appearance = args.Length > 1 ? args[1].ToLowerInvariant() : null;
            string result = string.Empty;
            await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => result = ShowSetupPreview(state, appearance));
            return result;
        });
        MacDebugBridge.Register("setup-preview-close", async _ =>
        {
            string result = string.Empty;
            await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => result = CloseSetupPreview());
            return result;
        });
        MacDebugBridge.Register("imap-failure", args =>
        {
            _ = Services.GetRequiredService<IMailDialogService>().ShowImapValidationFailedDialogAsync(
                "Authentication failed. The server rejected the user name or password.",
                "S: * OK [CAPABILITY IMAP4rev1 SASL-IR LOGIN-REFERRALS ID ENABLE IDLE AUTH=PLAIN] ready\nC: A00000000 CAPABILITY\nS: * CAPABILITY IMAP4rev1 AUTH=PLAIN\nS: A00000000 OK Capability completed.\nC: A00000001 AUTHENTICATE PLAIN ********\nS: A00000001 NO [AUTHENTICATIONFAILED] Authentication failed.");
            return Task.FromResult("ok");
        });
    }

    private static NSWindow? _setupPreview;
    private static IDisposable? _setupPreviewBinding;

    /// <summary>
    /// The real progress page view over sample steps in its own window. Nothing is authenticated or
    /// saved; Go Back and Try Again only switch between the sample states.
    /// </summary>
    private static string ShowSetupPreview(string state, string? appearance)
    {
        CloseSetupPreview();
        var steps = new ObservableCollection<AccountSetupStepModel>
        {
            new() { Title = string.Format(Translator.AccountSetup_Step_Authenticating, "Outlook") },
            new() { Title = Translator.AccountSetup_Step_FetchingProfile },
            new() { Title = Translator.AccountSetup_Step_SavingAccount },
            new() { Title = Translator.AccountSetup_Step_SyncingFolders },
            new() { Title = Translator.AccountSetup_Step_FetchingCalendarMetadata },
            new() { Title = Translator.AccountSetup_Step_SyncingContacts },
            new() { Title = Translator.AccountSetup_Step_Finalizing }
        };
        var back = new NSButton { BezelStyle = NSBezelStyle.Rounded };
        var retry = new NSButton { BezelStyle = NSBezelStyle.Rounded };
        var page = new Views.AccountSetupProgressPage(back, retry);

        void Apply(string mode)
        {
            var done = mode switch { "done" => steps.Count, "failed" => 3, _ => 3 };
            for (int index = 0; index < steps.Count; index++)
            {
                steps[index].ErrorMessage = null!;
                steps[index].Status = index < done ? AccountSetupStepStatus.Succeeded
                    : index == done && mode == "failed" ? AccountSetupStepStatus.Failed
                    : index == done ? AccountSetupStepStatus.InProgress
                    : AccountSetupStepStatus.Pending;
            }
            if (mode == "failed")
                steps[done].ErrorMessage = "The server closed the connection while listing folders (IMAP NO [UNAVAILABLE]). Check your connection and try again.";
            page.IsSetupComplete = mode == "done";
            page.IsSetupFailed = mode == "failed";
            page.FailureMessage = Translator.AccountSetup_FailureMessage;
        }

        _setupPreviewBinding = page.Steps.Bind(steps, Services.GetRequiredService<IDispatcher>(), exception => Console.Error.WriteLine(exception));
        back.Activated += (_, _) => Apply("failed");
        retry.Activated += (_, _) => Apply("progress");
        Apply(state);

        var host = new NSView(new CGRect(0, 0, 720, 560)) { AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable };
        Wino.Presentation.AppKit.WinoLayout.Fill(page, host);
        var window = new NSWindow(new CGRect(0, 0, 720, 560), NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = "Account setup preview",
            ReleasedWhenClosed = false,
            ContentView = host
        };
        if (appearance is "light" or "dark")
            window.Appearance = NSAppearance.GetAppearance(appearance == "dark" ? NSAppearance.NameDarkAqua : NSAppearance.NameAqua);
        window.Center();
        window.MakeKeyAndOrderFront(null);
        _setupPreview = window;
        return "ok " + state;
    }

    private static string CloseSetupPreview()
    {
        _setupPreviewBinding?.Dispose();
        _setupPreviewBinding = null;
        _setupPreview?.Close();
        _setupPreview = null;
        return "ok";
    }

    private static string Navigate(WinoPage page, object? parameter)
        => Services.GetRequiredService<AppKitNavigationService>().Navigate(page, parameter) ? "ok" : "refused";
}
#endif
