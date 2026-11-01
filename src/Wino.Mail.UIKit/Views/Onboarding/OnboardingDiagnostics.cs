#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Debug bridge commands that open the server pages for snapshots without typing anything (Mac OnboardingDebug):
/// <c>onboarding-imap</c> opens the custom server page as the wizard does, <c>onboarding-imap-edit</c> opens it for the
/// first IMAP account. The page adds <c>imap-*</c> commands while it is open.
/// </summary>
internal static class OnboardingDiagnostics
{
    public static void Register(IServiceProvider services)
    {
        UIKitDebugBridge.Register("onboarding-imap", _ =>
        {
            var provider = services.GetRequiredService<IKnownImapProviderCatalog>().GetAvailableProviders()
                .FirstOrDefault(item => item.Type == MailProviderType.IMAP4 && item.SpecialImapProvider == SpecialImapProvider.None);
            if (provider is null) return Task.FromResult("no custom provider");
            var context = services.GetRequiredService<WelcomeWizardContext>();
            context.SelectedProvider = provider;
            context.AccountName ??= "IMAP";
            context.IsMailAccessEnabled = true;
            return Navigate(services, WinoPage.ImapCalDavSettingsPage, ImapCalDavSettingsNavigationContext.CreateForWizardMode(context.BuildAccountCreationDialogResult()));
        });
        UIKitDebugBridge.Register("onboarding-imap-edit", async _ =>
        {
            var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
            var account = accounts.FirstOrDefault(item => item.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3);
            if (account is null) return "no IMAP account";
            return await Navigate(services, WinoPage.ImapCalDavSettingsPage, ImapCalDavSettingsNavigationContext.CreateForEditMode(account.Id));
        });
    }

    private static async Task<string> Navigate(IServiceProvider services, WinoPage page, object? parameter)
    {
        var result = "no route";
        await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            result = services.GetRequiredService<UIKitNavigationService>().Navigate(page, parameter) ? "ok" : "no route");
        return result;
    }
}
#endif
