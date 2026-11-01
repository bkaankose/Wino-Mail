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
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The step model the page renders: which step is shown, its app name, question and single-selection list, the
/// known-so-far strip, Back and the primary button.
/// </summary>
public partial class AccountSetupPageViewModel
{
    private const string ProviderChoiceId = "provider";
    private const string LocalChoiceId = "local";
    private const string OffChoiceId = "off";
    private const string EnterServerChoiceId = "enter";

    private bool _isDiscoveryShown;
    private bool _isBuildingChoices;
    private AccountSetupStep _builtChoicesStep = AccountSetupStep.Address;

    [ObservableProperty]
    public partial AccountSetupStep FeatureStep { get; set; } = AccountSetupStep.Mail;

    [ObservableProperty]
    public partial bool IsImapOptionChosen { get; set; }

    [ObservableProperty]
    public partial AccountSetupChoice? SelectedChoice { get; set; }

    public List<AccountSetupChoice> StepChoices { get; private set; } = [];

    public ObservableCollection<AccountSetupKnownItem> KnownItems { get; } = [];

    public AccountSetupStep Step => Phase switch
    {
        AccountSetupPhase.Identity => AccountSetupStep.Address,
        AccountSetupPhase.Resolving => _isDiscoveryShown ? AccountSetupStep.Discovery : AccountSetupStep.Address,
        AccountSetupPhase.NotFound => AccountSetupStep.Discovery,
        AccountSetupPhase.Features => FeatureStep,
        // Gmail and Outlook sign in from the To Do step's footer, so the step stays on screen while that runs.
        AccountSetupPhase.SignIn or AccountSetupPhase.Checking when IsFooterSignInRoute => AccountSetupStep.Tasks,
        AccountSetupPhase.SignIn when ImapOptions.Count > 1 && !IsImapOptionChosen => AccountSetupStep.SignInMethod,
        AccountSetupPhase.SignIn or AccountSetupPhase.Checking => AccountSetupStep.SignIn,
        _ => AccountSetupStep.Progress
    };

    /// <summary>The app the step is about, under its logo: Wino Mail on the address step, then one per mode step.</summary>
    public string StepAppName => Step switch
    {
        AccountSetupStep.Address or AccountSetupStep.Mail => Translator.AccountSetup_AppName_Mail,
        AccountSetupStep.Calendar => Translator.AccountSetup_AppName_Calendar,
        AccountSetupStep.Contacts => Translator.AccountSetup_AppName_People,
        AccountSetupStep.Tasks => Translator.AccountSetup_AppName_ToDo,
        _ => string.Empty
    };

    public string StepQuestion => Step switch
    {
        AccountSetupStep.Mail => Translator.AccountSetup_MailStepQuestion,
        AccountSetupStep.Calendar => Translator.AccountSetup_CalendarStepQuestion,
        AccountSetupStep.Contacts => Translator.AccountSetup_ContactsStepQuestion,
        AccountSetupStep.Tasks => Translator.AccountSetup_TasksStepQuestion,
        AccountSetupStep.SignInMethod => Translator.AccountSetup_SignInOptionsHeader,
        AccountSetupStep.Progress => StepsTitle,
        _ => string.Empty
    };

    public bool HasStepQuestion => !string.IsNullOrEmpty(StepQuestion);

    public int SelectedChoiceIndex => SelectedChoice == null ? -1 : StepChoices.IndexOf(SelectedChoice);

    /// <summary>One line under the step's list: the Everything warning, or the selected sign-in option's description.</summary>
    public string StepCaption => Step switch
    {
        AccountSetupStep.Mail when SelectedInitialSynchronizationRange?.IsEverything == true => Translator.AccountSetup_Range_EverythingCaption,
        AccountSetupStep.SignInMethod => SelectedImapOptionDescription,
        _ => string.Empty
    };

    public bool HasStepCaption => !string.IsNullOrEmpty(StepCaption);

    /// <summary>
    /// The wizard's breadcrumb steps aside for this page and has no Welcome item, so the address step's Back returns to
    /// Welcome there; Settings has its own Back. Progress has no Back; its failure InfoBar carries Go back.
    /// </summary>
    public bool IsBackVisible => Step != AccountSetupStep.Progress && (Step != AccountSetupStep.Address || IsLocalOnlyMode || !IsSettingsHost);

    public bool CanGoBack => IsBackVisible;

    /// <summary>
    /// A local account, or one whose modes need no server, is added from the To Do step; every other account from the
    /// sign-in step once it is validated.
    /// </summary>
    private bool IsAddedFromTasksStep => IsLocalOnlyMode || !Capabilities.RequiresRemoteService;

    /// <summary>The To Do step's note while a local account has Calendar, Contacts and To Do all off.</summary>
    public bool IsCapabilityRequiredVisible => Step == AccountSetupStep.Tasks && IsAddedFromTasksStep && !Capabilities.HasAnyCapability;

    /// <summary>
    /// The sign-in step's primary is Add account, enabled once signed in or checked. Password routes show Continue,
    /// which runs the checks, until then.
    /// </summary>
    public string PrimaryText => Step switch
    {
        AccountSetupStep.Tasks when IsAddedFromTasksStep => Translator.ProviderSelection_AddAccountButton,
        AccountSetupStep.SignIn when IsSignInValidated || !(IsPasswordPanelVisible || IsDavPasswordVisible || IsExchangePasswordVisible) => Translator.ProviderSelection_AddAccountButton,
        AccountSetupStep.Progress => IsSettingsHost ? Translator.AccountSetup_Done : Translator.AccountSetup_OpenWino,
        _ => Translator.ProviderSelection_ContinueButton
    };

    public bool IsPrimaryVisible => Step switch
    {
        AccountSetupStep.Discovery => false,
        AccountSetupStep.Tasks when IsFooterSignInRoute => false,
        AccountSetupStep.Progress => Phase == AccountSetupPhase.Added,
        _ => true
    };

    public bool IsKnownCardVisible => Step != AccountSetupStep.Address;

    public bool IsChangeAddressVisible => !IsLocalOnlyMode &&
                                          Step is not (AccountSetupStep.Address or AccountSetupStep.Discovery or AccountSetupStep.Progress);

    public string ChangeAddressAutomationName => $"{Translator.AccountSetup_ChangeAddress}, {EmailAddress?.Trim()}";

    public string KnownText => string.Join(" · ", KnownItems.Select(item => item.Text));

    private bool CanExecutePrimary() => Step switch
    {
        AccountSetupStep.Address => CanContinue(),
        AccountSetupStep.Tasks when IsAddedFromTasksStep => CanAddAccount(),
        AccountSetupStep.SignIn when IsSignInValidated => CanAddAccount(),
        AccountSetupStep.SignIn when IsDavPasswordVisible => CanCheckDavPassword(),
        AccountSetupStep.SignIn when IsPasswordPanelVisible => CanCheckPassword(),
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
            case AccountSetupStep.Mail:
            case AccountSetupStep.Calendar:
            case AccountSetupStep.Contacts:
                AdvanceFeatureStep();
                break;
            case AccountSetupStep.Tasks:
                await ContinueFromFeaturesAsync();
                break;
            case AccountSetupStep.SignInMethod:
                IsImapOptionChosen = true;
                break;
            case AccountSetupStep.SignIn when IsSignInValidated:
                await AddAccountAsync();
                break;
            case AccountSetupStep.SignIn when IsDavPasswordVisible:
                await CheckDavPasswordAsync();
                break;
            case AccountSetupStep.SignIn when IsPasswordPanelVisible:
                await CheckPasswordAsync();
                break;
            case AccountSetupStep.SignIn when IsExchangePasswordVisible:
                await CheckExchangePasswordAsync();
                break;
            case AccountSetupStep.Progress:
                Finish();
                break;
        }
    }

    private void AdvanceFeatureStep()
    {
        FeaturesError = string.Empty;

        var isManualEntry = (Step == AccountSetupStep.Calendar && IsCalDavManualEntryVisible) ||
                            (Step == AccountSetupStep.Contacts && IsCardDavManualEntryVisible);

        var manualUrl = Step == AccountSetupStep.Contacts ? ManualCardDavUrl : ManualCalDavUrl;
        if (isManualEntry && ParseDavUri(manualUrl) == null)
        {
            FeaturesError = Translator.AccountSetup_ServerAddressInvalid;
            return;
        }

        FeatureStep = FeatureStep switch
        {
            AccountSetupStep.Mail => AccountSetupStep.Calendar,
            AccountSetupStep.Calendar => AccountSetupStep.Contacts,
            _ => AccountSetupStep.Tasks
        };
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
            case AccountSetupStep.Discovery:
                await ReturnToAddressAsync();
                break;
            case AccountSetupStep.Mail:
                await ReturnToAddressAsync();
                break;
            case AccountSetupStep.Calendar when IsLocalOnlyMode:
                ClearRoute();
                Phase = AccountSetupPhase.Identity;
                break;
            case AccountSetupStep.Calendar:
                FeatureStep = AccountSetupStep.Mail;
                break;
            case AccountSetupStep.Contacts:
                FeatureStep = AccountSetupStep.Calendar;
                break;
            case AccountSetupStep.Tasks when Phase != AccountSetupPhase.Features:
                await LeaveSignInAsync();
                FeatureStep = AccountSetupStep.Contacts;
                break;
            case AccountSetupStep.Tasks:
                FeatureStep = AccountSetupStep.Contacts;
                break;
            case AccountSetupStep.SignInMethod:
                ReturnToFeatures();
                break;
            case AccountSetupStep.SignIn:
                await LeaveSignInAsync();
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

        if (ImapOptions.Count > 1)
        {
            IsImapOptionChosen = false;
            Phase = AccountSetupPhase.SignIn;
            NotifyStepChanged();
            return;
        }

        ReturnToFeatures();
    }

    private void ReturnToFeatures()
    {
        FeatureStep = AccountSetupStep.Tasks;
        Phase = AccountSetupPhase.Features;
    }

    private void Finish()
    {
        // In the wizard the App opens the shell as soon as AccountCreatedMessage arrives, so there is nothing left to do.
        if (IsSettingsHost)
            Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
    }

    partial void OnFeatureStepChanged(AccountSetupStep value)
    {
        if (Phase == AccountSetupPhase.Features)
            NotifyStepChanged();
    }

    partial void OnIsImapOptionChosenChanged(bool value) => NotifyStepChanged();

    partial void OnSelectedChoiceChanged(AccountSetupChoice? value)
    {
        OnPropertyChanged(nameof(SelectedChoiceIndex));

        if (_isBuildingChoices || value == null)
            return;

        ApplyChoice(_builtChoicesStep, value.Id);
    }

    private void ApplyChoice(AccountSetupStep step, string id)
    {
        switch (step)
        {
            case AccountSetupStep.Mail:
                var range = InitialSynchronizationRanges.FirstOrDefault(option => option.Range.ToString() == id);
                if (range != null)
                    SelectedInitialSynchronizationRange = range;
                break;
            case AccountSetupStep.Calendar:
                ApplyCalendarChoice(id);
                break;
            case AccountSetupStep.Contacts:
                ApplyContactChoice(id);
                break;
            case AccountSetupStep.Tasks:
                Capabilities.TaskMode = ToMode(id);
                break;
            case AccountSetupStep.SignInMethod:
                if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < ImapOptions.Count)
                    SelectedImapOption = ImapOptions[index];
                break;
        }

        FeaturesError = string.Empty;
        NotifyStepContentChanged();
    }

    private void ApplyCalendarChoice(string id)
    {
        if (Capabilities.IsCalendarManualEntryAvailable)
        {
            var isEnter = id == EnterServerChoiceId;
            IsCalDavManualEntryVisible = isEnter;
            Capabilities.CalendarProviderName = Translator.ProviderSelection_SourceCalDav;
            Capabilities.IsCalendarProviderModeAvailable = isEnter;
            Capabilities.CalendarMode = isEnter ? AccountCapabilityMode.Provider : ToMode(id);
            return;
        }

        Capabilities.CalendarMode = ToMode(id);
    }

    private void ApplyContactChoice(string id)
    {
        if (Capabilities.IsContactManualEntryAvailable)
        {
            var isEnter = id == EnterServerChoiceId;
            IsCardDavManualEntryVisible = isEnter;
            Capabilities.ContactProviderName = Translator.ProviderSelection_SourceCardDav;
            Capabilities.IsContactProviderModeAvailable = isEnter;
            Capabilities.ContactMode = isEnter ? AccountCapabilityMode.Provider : ToMode(id);
            return;
        }

        Capabilities.ContactMode = ToMode(id);
    }

    private static AccountCapabilityMode ToMode(string id) => id switch
    {
        ProviderChoiceId => AccountCapabilityMode.Provider,
        LocalChoiceId => AccountCapabilityMode.Local,
        _ => AccountCapabilityMode.Off
    };

    private static string ToChoiceId(AccountCapabilityMode mode) => mode switch
    {
        AccountCapabilityMode.Provider => ProviderChoiceId,
        AccountCapabilityMode.Local => LocalChoiceId,
        _ => OffChoiceId
    };

    /// <summary>Rebuilds the list for the current step and selects what the selections already say.</summary>
    private void BuildStepChoices()
    {
        var step = Step;
        var choices = new List<AccountSetupChoice>();
        var selectedId = string.Empty;

        switch (step)
        {
            case AccountSetupStep.Mail:
                choices.Add(new AccountSetupChoice(nameof(InitialSynchronizationRange.ThreeMonths), Translator.AccountSetup_Range_3Months));
                choices.Add(new AccountSetupChoice(nameof(InitialSynchronizationRange.SixMonths), Translator.AccountSetup_Range_6Months, isRecommended: true));
                choices.Add(new AccountSetupChoice(nameof(InitialSynchronizationRange.NineMonths), Translator.AccountSetup_Range_9Months));
                choices.Add(new AccountSetupChoice(nameof(InitialSynchronizationRange.OneYear), Translator.AccountSetup_Range_Year));
                choices.Add(new AccountSetupChoice(nameof(InitialSynchronizationRange.Everything), Translator.AccountSetup_Range_Everything));
                selectedId = (SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths).ToString();
                break;
            case AccountSetupStep.Calendar:
                AddModeChoices(choices,
                    Capabilities.IsCalendarManualEntryAvailable,
                    Capabilities.IsCalendarProviderModeAvailable,
                    GetCalendarSyncedText(),
                    Translator.AccountSetup_Choice_EnterCalDav);
                selectedId = IsCalDavManualEntryVisible ? EnterServerChoiceId : ToChoiceId(Capabilities.CalendarMode);
                break;
            case AccountSetupStep.Contacts:
                AddModeChoices(choices,
                    Capabilities.IsContactManualEntryAvailable,
                    Capabilities.IsContactProviderModeAvailable,
                    GetContactSyncedText(),
                    Translator.AccountSetup_Choice_EnterCardDav);
                selectedId = IsCardDavManualEntryVisible ? EnterServerChoiceId : ToChoiceId(Capabilities.ContactMode);
                break;
            case AccountSetupStep.Tasks:
                AddModeChoices(choices,
                    false,
                    Capabilities.IsTaskProviderModeAvailable,
                    string.Format(Translator.AccountSetup_Choice_SyncedWith, Route switch
                    {
                        AccountRoute.Gmail => Translator.AccountSetup_Provider_GoogleTasks,
                        AccountRoute.Exchange => ExchangeProviderName,
                        _ => Translator.AccountSetup_Provider_MicrosoftToDo
                    }),
                    string.Empty);
                selectedId = ToChoiceId(Capabilities.TaskMode);
                break;
            case AccountSetupStep.SignInMethod:
                for (var i = 0; i < ImapOptions.Count; i++)
                {
                    choices.Add(new AccountSetupChoice(
                        i.ToString(CultureInfo.InvariantCulture),
                        ImapOptions[i].Title,
                        isRecommended: i == 0 && ImapOptions[i].IsBrowser,
                        caption: ImapOptions[i].IsBrowser ? Translator.AccountSetup_MostSecure : null));
                }

                selectedId = SelectedImapOption == null ? "0" : ImapOptions.IndexOf(SelectedImapOption).ToString(CultureInfo.InvariantCulture);
                break;
        }

        _isBuildingChoices = true;
        try
        {
            _builtChoicesStep = step;
            StepChoices = choices;
            OnPropertyChanged(nameof(StepChoices));
            SelectedChoice = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices.FirstOrDefault(choice => choice.IsRecommended) ?? choices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedChoiceIndex));
        }
        finally
        {
            _isBuildingChoices = false;
        }
    }

    /// <summary>
    /// Synced (recommended) · On this device · Off when the route syncs the capability; otherwise On this device
    /// (recommended), for IMAP without a discovered service "A … server I enter", then Off, which is always last.
    /// </summary>
    private void AddModeChoices(List<AccountSetupChoice> choices, bool isManualEntryShape, bool isProviderAvailable, string syncedText, string enterText)
    {
        if (!IsLocalOnlyMode && !isManualEntryShape && isProviderAvailable)
        {
            choices.Add(new AccountSetupChoice(ProviderChoiceId, syncedText, isRecommended: true));
            choices.Add(new AccountSetupChoice(LocalChoiceId, Translator.AccountSetup_Choice_OnThisDevice));
            choices.Add(new AccountSetupChoice(OffChoiceId, Translator.AccountSetup_Choice_Off));
            return;
        }

        choices.Add(new AccountSetupChoice(LocalChoiceId, Translator.AccountSetup_Choice_OnThisDevice, isRecommended: true));

        if (!IsLocalOnlyMode && isManualEntryShape && !string.IsNullOrEmpty(enterText))
            choices.Add(new AccountSetupChoice(EnterServerChoiceId, enterText));

        choices.Add(new AccountSetupChoice(OffChoiceId, Translator.AccountSetup_Choice_Off));
    }

    private string GetCalendarSyncedText()
        => IsExchangeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, ExchangeProviderName)
            : IsNativeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, NativeProviderName)
            : string.Format(Translator.AccountSetup_Choice_DavAt, _resolution?.Support.Calendar.ServiceUri?.Host ?? ImapHostName);

    private string GetContactSyncedText()
        => IsExchangeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, ExchangeProviderName)
            : IsNativeRoute
            ? string.Format(Translator.AccountSetup_Choice_SyncedWith, NativeProviderName)
            : string.Format(Translator.AccountSetup_Choice_CardDavAt, _resolution?.Support.Contacts.ServiceUri?.Host ?? ImapHostName);

    /// <summary>The strip lists what the steps before the current one decided; Back removes what the left step added.</summary>
    private void BuildKnownItems()
    {
        var items = new List<AccountSetupKnownItem>();
        var step = Step;
        var stepOrder = (int)step;

        bool IsPast(AccountSetupStep candidate) => stepOrder > (int)candidate;

        if (IsLocalOnlyMode)
        {
            if (step != AccountSetupStep.Address)
            {
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Identity, AccountName?.Trim() ?? string.Empty));
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Provider, Translator.AccountSetup_Known_ThisDevice));
            }
        }
        else if (step != AccountSetupStep.Address)
        {
            items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Identity, EmailAddress?.Trim() ?? string.Empty));

            if (IsPast(AccountSetupStep.Discovery) && HasRouteShortTitle)
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Provider, RouteShortTitle, RouteEvidenceText));

            if (IsPast(AccountSetupStep.Mail) && Capabilities.IsMailEnabled)
                items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Feature, string.Format(Translator.AccountSetup_Known_Mail, LowerFirst(GetRangeLabel()))));
        }

        if (step != AccountSetupStep.Address)
        {
            AddModeItem(items, IsPast(AccountSetupStep.Calendar), Capabilities.CalendarMode, Translator.ProviderSelection_UseForCalendar);
            AddModeItem(items, IsPast(AccountSetupStep.Contacts), Capabilities.ContactMode, Translator.ProviderSelection_UseForContacts);
            AddModeItem(items, IsPast(AccountSetupStep.Tasks), Capabilities.TaskMode, Translator.ProviderSelection_UseForTasks);
        }

        if (!IsLocalOnlyMode && ImapOptions.Count > 1 && SelectedImapOption != null && IsPast(AccountSetupStep.SignInMethod))
        {
            items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Feature, SelectedImapOption.IsBrowser
                ? Translator.AccountSetup_Known_BrowserSignIn
                : SelectedImapOption.Title));
        }

        if (!IsLocalOnlyMode && IsSignInComplete)
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

    private static void AddModeItem(List<AccountSetupKnownItem> items, bool isPast, AccountCapabilityMode mode, string name)
    {
        if (!isPast || mode == AccountCapabilityMode.Off)
            return;

        items.Add(new AccountSetupKnownItem(AccountSetupKnownItemKind.Feature, mode == AccountCapabilityMode.Local
            ? string.Format(Translator.AccountSetup_Known_OnDevice, name)
            : name));
    }

    private bool IsSignInComplete => Capabilities.RequiresRemoteService &&
                                     (IsSignInValidated || Phase is AccountSetupPhase.Adding or AccountSetupPhase.Added or AccountSetupPhase.Failed);

    private string GetRangeLabel() => (SelectedInitialSynchronizationRange?.Range ?? InitialSynchronizationRange.SixMonths) switch
    {
        InitialSynchronizationRange.ThreeMonths => Translator.AccountSetup_Range_3Months,
        InitialSynchronizationRange.NineMonths => Translator.AccountSetup_Range_9Months,
        InitialSynchronizationRange.OneYear => Translator.AccountSetup_Range_Year,
        InitialSynchronizationRange.Everything => Translator.AccountSetup_Range_Everything,
        _ => Translator.AccountSetup_Range_6Months
    };

    private static string LowerFirst(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToLower(value[0], CultureInfo.CurrentCulture) + value[1..];

    /// <summary>Everything the step host reads changes together when the step does.</summary>
    private void NotifyStepChanged()
    {
        if (_builtChoicesStep != Step || StepChoices.Count == 0)
            BuildStepChoices();

        BuildKnownItems();

        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepAppName));
        OnPropertyChanged(nameof(StepQuestion));
        OnPropertyChanged(nameof(HasStepQuestion));
        OnPropertyChanged(nameof(IsBackVisible));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsKnownCardVisible));
        OnPropertyChanged(nameof(IsChangeAddressVisible));
        OnPropertyChanged(nameof(ChangeAddressAutomationName));
        OnPropertyChanged(nameof(IsFooterVisible));
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
        OnPropertyChanged(nameof(IsCapabilityRequiredVisible));
        PrimaryCommand.NotifyCanExecuteChanged();
        AddAccountCommand.NotifyCanExecuteChanged();
    }
}
