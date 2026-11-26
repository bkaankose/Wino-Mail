#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Advanced setup from the address step: the user says which kind of server the account is on before entering it.
/// IMAP or POP3 opens the mail server page and Exchange Server opens the Exchange page, both with the typed address.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private const string ImapServerKindId = "imap";
    private const string ExchangeServerKindId = "exchange";

    [ObservableProperty]
    public partial bool IsChoosingServerKind { get; set; }

    [RelayCommand]
    private void ChooseServerKind()
    {
        if (Phase != AccountSetupPhase.Identity || IsLocalOnlyMode)
            return;

        CancelOperation();
        IdentityError = string.Empty;
        AccountNameError = string.Empty;
        IsChoosingServerKind = true;
    }

    private async Task ContinueWithServerKindAsync()
    {
        if (SelectedChoice?.Id == ExchangeServerKindId)
            await OpenExchangeAdvancedSetupAsync();
        else
            await OpenAdvancedSetupAsync();

        // The step stays on screen while the Exchange notice is open; the page is gone once the target opens.
        IsChoosingServerKind = false;
    }

    private void BuildServerKindChoices()
    {
        var choices = new List<AccountSetupChoice>
        {
            new(ImapServerKindId, Translator.AccountSetup_ServerKind_Imap, description: Translator.AccountSetup_ServerKind_ImapDescription),
            new(ExchangeServerKindId, Translator.AccountSetup_ProviderChoice_Exchange, description: Translator.AccountSetup_ServerKind_ExchangeDescription)
        };

        _isBuildingChoices = true;
        try
        {
            _choicesStep = AccountSetupStep.ServerKind;
            StepChoices = choices;
            OnPropertyChanged(nameof(StepChoices));
            SelectedChoice = choices.First();
            OnPropertyChanged(nameof(SelectedChoiceIndex));
        }
        finally
        {
            _isBuildingChoices = false;
        }
    }

    partial void OnIsChoosingServerKindChanged(bool value) => NotifyStepChanged();
}
