#nullable enable
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>The way from IMAP Advanced setup to Exchange setup, for an Exchange Server that address-first setup did not find.</summary>
public partial class ImapCalDavSettingsPageViewModel
{
    [RelayCommand]
    private void OpenExchangeSetup()
    {
        if (!IsCreateMode)
            return;

        CancelOAuthSignIn();
        DiscardOAuthSession();

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.ExchangeSetup_Title,
            WinoPage.ExchangeSettingsPage,
            new ExchangeSettingsNavigationContext
            {
                EmailAddress = EmailAddress?.Trim() ?? string.Empty,
                DisplayName = DisplayName?.Trim() ?? string.Empty
            }));
    }
}
