#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;
using Wino.Core.Domain.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The step model the page renders: which step is shown, its title, the known-so-far strip, Back and the primary
/// button. The IMAP route goes Address, Discovery, Sign in, Review; Gmail, Outlook and Exchange go Address, Discovery,
/// Review and sign in from there, because their sign-in asks only for the apps the review left on.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private bool _isDiscoveryShown;
    private bool _isBuildingChoices;
    private AccountSetupStep? _choicesStep;

    [ObservableProperty]
    public partial bool IsImapOptionChosen { get; set; }

    [ObservableProperty]
    public partial AccountSetupChoice? SelectedChoice { get; set; }

    /// <summary>The list of the sign-in method step or the server kind step, the two single-selection steps.</summary>
    public List<AccountSetupChoice> StepChoices { get; private set; } = [];

    public ObservableCollection<AccountSetupKnownItem> KnownItems { get; } = [];

    public AccountSetupStep Step => Phase switch
    {
        AccountSetupPhase.Identity when IsChoosingServerKind => AccountSetupStep.ServerKind,
        AccountSetupPhase.Identity => AccountSetupStep.Address,
        AccountSetupPhase.Resolving => _isDiscoveryShown ? AccountSetupStep.Discovery : AccountSetupStep.Address,
        AccountSetupPhase.NotFound => AccountSetupStep.Discovery,
        AccountSetupPhase.Features => AccountSetupStep.Review,
        // Gmail, Outlook and modern-auth Exchange sign in from the review step's footer, so the review stays on screen.
        AccountSetupPhase.SignIn or AccountSetupPhase.Checking when IsFooterSignInRoute => AccountSetupStep.Review,
        AccountSetupPhase.SignIn when ImapOptions.Count > 1 && !IsImapOptionChosen => AccountSetupStep.SignInMethod,
        AccountSetupPhase.SignIn or AccountSetupPhase.Checking => AccountSetupStep.SignIn,
        _ => AccountSetupStep.Progress
    };

    /// <summary>The headline of the step: what the user does on it.</summary>
    public string StepTitle => Step switch
    {
        AccountSetupStep.Address when IsLocalOnlyMode => Translator.AccountSetup_Title_Local,
        // Settings already names the page in its breadcrumb.
        AccountSetupStep.Address => IsSettingsHost && !IsImporting ? string.Empty : Translator.AccountSetup_Title_Address,
        AccountSetupStep.Discovery when Phase == AccountSetupPhase.NotFound => Translator.AccountSetup_Title_NotFound,
        AccountSetupStep.Discovery => Translator.AccountSetup_Title_Discovery,
        AccountSetupStep.ServerKind => Translator.AccountSetup_ServerKind_Title,
        AccountSetupStep.SignInMethod => Translator.AccountSetup_SignInOptionsHeader,
        AccountSetupStep.SignIn => string.Format(Translator.AccountSetup_SignIn_Title, SignInTargetName),
        AccountSetupStep.Review => Translator.AccountSetup_Review_Title,
        _ => StepsTitle
    };

    public string StepSubtitle => Step switch
    {
        AccountSetupStep.Address when IsLocalOnlyMode => Translator.AccountSetup_Subtitle_Local,
        AccountSetupStep.Address when IsImporting => ImportedAccountSubtitle,
        AccountSetupStep.Discovery when Phase != AccountSetupPhase.NotFound => Translator.AccountSetup_Subtitle_Discovery,
        AccountSetupStep.Review when !IsLocalOnlyMode => Translator.AccountSetup_Review_Subtitle,
        _ => string.Empty
    };

    public bool HasStepTitle => !string.IsNullOrEmpty(StepTitle);

    public bool HasStepSubtitle => !string.IsNullOrEmpty(StepSubtitle);

    public int SelectedChoiceIndex => SelectedChoice == null ? -1 : StepChoices.IndexOf(SelectedChoice);

    /// <summary>One line under the sign-in method list: the selected option's description.</summary>
    public string StepCaption => Step == AccountSetupStep.SignInMethod ? SelectedImapOptionDescription : string.Empty;

    public bool HasStepCaption => !string.IsNullOrEmpty(StepCaption);

    /// <summary>
    /// The wizard's breadcrumb steps aside for this page and has no Welcome item, so the address step's Back returns to
    /// Welcome there; Settings has its own Back. Progress has no Back; its failure InfoBar carries Go back.
    /// </summary>
    public bool IsBackVisible => Step != AccountSetupStep.Progress && (Step != AccountSetupStep.Address || IsLocalOnlyMode || !IsSettingsHost);

    public bool CanGoBack => IsBackVisible && !IsReviewBusy;

    private bool IsImapRoute => !IsLocalOnlyMode && Route == AccountRoute.Imap;

    /// <summary>
    /// The review step's primary button adds the account for a local account, an account whose apps need no server and
    /// the IMAP route, which is already signed in. An Exchange password account continues to its sign-in step instead.
    /// </summary>
    private bool IsAddedFromReview => IsLocalOnlyMode || IsImapRoute || !Capabilities.RequiresRemoteService;

    private string SignInTargetName => IsExchangeRoute
        ? ExchangeHostName
        : string.IsNullOrWhiteSpace(ProviderLabel) ? AddressDomain : ProviderLabel;

    public string PrimaryText => Step switch
    {
        AccountSetupStep.Review when IsAddedFromReview => Translator.ProviderSelection_AddAccountButton,
        AccountSetupStep.SignIn when !IsImapRoute && IsSignInValidated => Translator.ProviderSelection_AddAccountButton,
        AccountSetupStep.Progress => IsSettingsHost ? Translator.AccountSetup_Done : Translator.AccountSetup_OpenWino,
        _ => Translator.ProviderSelection_ContinueButton
    };

    public bool IsPrimaryVisible => Step switch
    {
        AccountSetupStep.Discovery => false,
        AccountSetupStep.Review when IsFooterSignInRoute && Capabilities.RequiresRemoteService => false,
        AccountSetupStep.SignIn when IsBrowserPanelVisible => false,
        AccountSetupStep.Progress => Phase == AccountSetupPhase.Added,
        _ => true
    };

    /// <summary>The server kind step shows the typed address, if there is one, so the user sees it is kept.</summary>
    public bool IsKnownCardVisible => Step != AccountSetupStep.Address &&
                                      (Step != AccountSetupStep.ServerKind || !string.IsNullOrWhiteSpace(EmailAddress));

    public bool IsChangeAddressVisible => !IsLocalOnlyMode &&
                                          Step is not (AccountSetupStep.Address or AccountSetupStep.ServerKind or AccountSetupStep.Discovery or AccountSetupStep.Progress);

    public string ChangeAddressAutomationName => $"{Translator.AccountSetup_ChangeAddress}, {EmailAddress?.Trim()}";

    public string KnownText => string.Join(" · ", KnownItems.Select(item => item.Text));

    private bool CanExecutePrimary() => Step switch
    {
        AccountSetupStep.Address => CanContinue(),
        AccountSetupStep.ServerKind => SelectedChoice != null,
        AccountSetupStep.Review => Phase == AccountSetupPhase.Features && !IsReviewBusy && Capabilities.HasAnyCapability && HasAccountName,
        AccountSetupStep.SignIn when IsImapRoute => CanCheckPassword(),
        AccountSetupStep.SignIn when IsSignInValidated => CanAddAccount(),
        AccountSetupStep.SignIn when IsExchangePasswordVisible => CanCheckExchangePassword(),
        AccountSetupStep.SignIn => false,
        AccountSetupStep.Progress => Phase == AccountSetupPhase.Added,
        AccountSetupStep.Discovery => false,
        _ => true
    };

    [RelayCommand(CanExecute = nameof(CanExecutePrimary))]
    private async Task PrimaryAsync()
    {
        switch (Step)
        {
            case AccountSetupStep.Address:
                await ContinueAsync();
                break;
            case AccountSetupStep.ServerKind:
                await ContinueWithServerKindAsync();
                break;
            case AccountSetupStep.Review:
                await ContinueFromReviewAsync();
                break;
            case AccountSetupStep.SignInMethod:
                IsImapOptionChosen = true;
                // Choosing the browser is the decision to sign in, so it opens at once; an issuer the user must
                // confirm first keeps the sign-in step's check box and button.
                if (CanSignInWithBrowser())
                    await SignInWithBrowserAsync();
                break;
            case AccountSetupStep.SignIn when IsImapRoute:
                await CheckPasswordAsync();
                break;
            case AccountSetupStep.SignIn when IsSignInValidated:
                await AddAccountAsync();
                break;
            case AccountSetupStep.SignIn when IsExchangePasswordVisible:
                await CheckExchangePasswordAsync();
                break;
            case AccountSetupStep.Progress:
                Finish();
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        FeaturesError = string.Empty;
        IdentityError = string.Empty;
        AccountNameError = string.Empty;

        switch (Step)
        {
            case AccountSetupStep.Address when IsLocalOnlyMode:
                await LeaveLocalOnlyAsync();
                break;
            case AccountSetupStep.Address:
                await CancelAsync();
                break;
            case AccountSetupStep.ServerKind:
                IsChoosingServerKind = false;
                break;
            case AccountSetupStep.Discovery:
            case AccountSetupStep.SignInMethod:
                await ReturnToAddressAsync();
                break;
            case AccountSetupStep.SignIn:
                await LeaveSignInAsync();
                break;
            case AccountSetupStep.Review when IsLocalOnlyMode:
                await LeaveLocalOnlyAsync();
                break;
            case AccountSetupStep.Review when IsImapRoute:
                await LeaveImapReviewAsync();
                break;
            case AccountSetupStep.Review when Phase != AccountSetupPhase.Features:
                CancelOperation();
                await DiscardSignInAsync();
                Phase = AccountSetupPhase.Features;
                break;
            case AccountSetupStep.Review:
                await ReturnToAddressAsync();
                break;
        }
    }

    /// <summary>
    /// Back from Sign in keeps a typed password that was never checked; a finished sign-in or check is discarded, so
    /// the token file, the OAuth session and the checked server never outlive the step.
    /// </summary>
    private async Task LeaveSignInAsync()
    {
        CancelOperation();

        var isSignedIn = IsSignInValidated ||
                         Phase == AccountSetupPhase.Checking ||
                         OAuthState != AccountSetupOAuthState.Idle ||
                         _pendingServer != null;
        var password = Password;

        await DiscardSignInAsync();

        if (!isSignedIn)
            Password = password;

        if (!IsImapRoute)
        {
            Phase = AccountSetupPhase.Features;
            return;
        }

        if (ImapOptions.Count > 1 && IsImapOptionChosen)
        {
            IsImapOptionChosen = false;
            Phase = AccountSetupPhase.SignIn;
            NotifyStepChanged();
            return;
        }

        await ReturnToAddressAsync();
    }

    /// <summary>Back from the IMAP review signs out: the review described what that sign-in can reach.</summary>
    private async Task LeaveImapReviewAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();

        if (_resolution != null)
            ConfigureFeatures(_resolution);

        Phase = AccountSetupPhase.SignIn;
        NotifyStepChanged();
    }

    private void Finish()
    {
        if (IsImporting)
        {
            ContinueImport();
            return;
        }

        // In the wizard the App opens the shell as soon as AccountCreatedMessage arrives, so there is nothing left to do.
        if (IsSettingsHost)
            Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
    }

    partial void OnIsImapOptionChosenChanged(bool value) => NotifyStepChanged();

    partial void OnSelectedChoiceChanged(AccountSetupChoice? value)
    {
        OnPropertyChanged(nameof(SelectedChoiceIndex));

        if (_isBuildingChoices || value == null)
            return;

        if (_choicesStep == AccountSetupStep.SignInMethod &&
            int.TryParse(value.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < ImapOptions.Count)
            SelectedImapOption = ImapOptions[index];

        NotifyStepContentChanged();
    }

    /// <summary>Rebuilds the sign-in method list and selects the option the sign-in already uses.</summary>
    private void BuildStepChoices()
    {
        var choices = new List<AccountSetupChoice>();

        for (var i = 0; i < ImapOptions.Count; i++)
        {
            choices.Add(new AccountSetupChoice(
                i.ToString(CultureInfo.InvariantCulture),
                ImapOptions[i].Title,
                isRecommended: i == 0 && ImapOptions[i].IsBrowser,
                caption: ImapOptions[i].IsBrowser ? Translator.AccountSetup_MostSecure : null));
        }

        var selectedId = SelectedImapOption == null ? "0" : ImapOptions.IndexOf(SelectedImapOption).ToString(CultureInfo.InvariantCulture);

        _isBuildingChoices = true;
        try
        {
            _choicesStep = AccountSetupStep.SignInMethod;
            StepChoices = choices;
            OnPropertyChanged(nameof(StepChoices));
            SelectedChoice = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedChoiceIndex));
        }
        finally
        {
            _isBuildingChoices = false;
        }
    }

    /// <summary>
    /// The strip names the account, its provider and why Wino chose it. The result step adds what was set up; the
    /// review step shows that itself.
    /// </summary>
    private void BuildKnownItems()
    {
        var items = new List<AccountSetupKnownItem>();
        var step = Step;

        if (step != AccountSetupStep.Address)
        {
            if (IsLocalOnlyMode)
            {
                var name = AccountName?.Trim() ?? string.Empty;
                if (name.Length > 0)
                    items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Identity, name));
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Provider, Translator.AccountSetup_Known_ThisDevice));
            }
            else
            {
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Identity, EmailAddress?.Trim() ?? string.Empty));

                if (step != AccountSetupStep.Discovery && HasRouteShortTitle)
                {
                    items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Provider, RouteShortTitle, RouteEvidenceText));

                    var reason = RouteReasonText;
                    if (step != AccountSetupStep.Progress && reason.Length > 0)
                        items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Reason, reason));
                }
            }
        }

        if (!IsLocalOnlyMode && step != AccountSetupStep.Address && IsSignInComplete)
        {
            var signedInAddress = SignedInAddress?.Trim() ?? string.Empty;
            var text = IsNativeRoute && signedInAddress.Length > 0 &&
                       !string.Equals(signedInAddress, EmailAddress?.Trim(), System.StringComparison.OrdinalIgnoreCase)
                ? string.Format(Translator.AccountSetup_Known_SignedInAs, signedInAddress)
                : Translator.AccountSetup_Known_SignedIn;

            items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.SignIn, text));
        }

        if (items.Select(item => (item.Kind, item.Text)).SequenceEqual(KnownItems.Select(item => (item.Kind, item.Text))))
            return;

        KnownItems.Clear();
        foreach (var item in items)
            KnownItems.Add(item);

        OnPropertyChanged(nameof(KnownText));
    }

    private bool IsSignInComplete => Capabilities.RequiresRemoteService &&
                                     (IsSignInValidated || Phase is AccountSetupPhase.Adding or AccountSetupPhase.Added or AccountSetupPhase.Failed);

    /// <summary>Everything the step host reads changes together when the step does.</summary>
    private void NotifyStepChanged()
    {
        if (Step == AccountSetupStep.SignInMethod && (_choicesStep != AccountSetupStep.SignInMethod || StepChoices.Count != ImapOptions.Count))
            BuildStepChoices();
        else if (Step == AccountSetupStep.ServerKind && _choicesStep != AccountSetupStep.ServerKind)
            BuildServerKindChoices();

        BuildKnownItems();

        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(HasStepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(HasStepSubtitle));
        OnPropertyChanged(nameof(IsBackVisible));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsKnownCardVisible));
        OnPropertyChanged(nameof(IsChangeAddressVisible));
        OnPropertyChanged(nameof(IsChangeProviderVisible));
        OnPropertyChanged(nameof(ChangeAddressAutomationName));
        OnPropertyChanged(nameof(IsFooterVisible));
        OnPropertyChanged(nameof(IsSkipImportedAccountVisible));
        NotifyStepContentChanged();

        BackCommand.NotifyCanExecuteChanged();
        ChangeAddressCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStepContentChanged()
    {
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(IsPrimaryVisible));
        OnPropertyChanged(nameof(IsFooterSocialSignInVisible));
        OnPropertyChanged(nameof(IsConnectionStatusVisible));
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(HasStepCaption));
        NotifyReviewStateChanged();
        PrimaryCommand.NotifyCanExecuteChanged();
        AddAccountCommand.NotifyCanExecuteChanged();
        SignInWithProviderCommand.NotifyCanExecuteChanged();
    }
}
