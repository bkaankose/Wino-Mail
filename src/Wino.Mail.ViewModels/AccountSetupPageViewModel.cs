#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Validation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Step-by-step, address-first account setup: one step at a time (<see cref="Step"/>) with a known-so-far card above
/// it. Nothing is written to the database before Add account; native sign-ins, OAuth sessions and passwords live here
/// until then and are discarded when the user goes back past the sign-in, changes the address, or leaves the page.
/// </summary>
public partial class AccountSetupPageViewModel : CoreBaseViewModel
{
    private readonly IAccountService _accountService;
    private readonly IAccountRouteResolver _routeResolver;
    private readonly IAccountSetupRunner _setupRunner;
    private readonly IKnownImapProviderCatalog _providerCatalog;
    private readonly INewThemeService _themeService;
    private readonly IMailDialogService _dialogService;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly ICalDavClient _calDavClient;
    private readonly ICardDavClient? _cardDavClient;
    private readonly IOAuthTokenService? _oauthTokenService;
    private readonly IDavOAuthAuthenticationProvider? _davOAuthAuthenticationProvider;
    private readonly IExternalLauncher? _externalLauncher;

    private ProviderSelectionHostMode _hostMode = ProviderSelectionHostMode.Wizard;
    private CancellationTokenSource? _operation;
    private bool _isAccountNameDerived;

    public WelcomeWizardContext WizardContext { get; }

    /// <summary>Set by heads that render the browser sign-in option; without it the IMAP option (a) is never offered.</summary>
    public bool IsSecureSignInSupportedByHost { get; set; }

    public List<AppColorViewModel> AvailableColors { get; private set; } = [];

    [ObservableProperty]
    public partial AccountSetupPhase Phase { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial string EmailAddress { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColorSelected))]
    public partial AppColorViewModel? SelectedColor { get; set; }

    /// <summary>The address step's error under the email field: an invalid address or one Wino already has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentityError))]
    public partial string IdentityError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountNameError))]
    public partial string AccountNameError { get; set; } = string.Empty;

    /// <summary>Local account setup: a name, then calendar, contacts and To Do on this device. No address and no server.</summary>
    [ObservableProperty]
    public partial bool IsLocalOnlyMode { get; set; }

    public bool HasIdentityError => !string.IsNullOrWhiteSpace(IdentityError);
    public bool HasAccountNameError => !string.IsNullOrWhiteSpace(AccountNameError);

    public bool IsEmailFieldVisible => !IsLocalOnlyMode;
    public bool IsFooterVisible => Step == AccountSetupStep.Address && !IsLocalOnlyMode;

    public bool IsColorSelected => SelectedColor != null;

    public AccountSetupPageViewModel(
        IAccountService accountService,
        IAccountRouteResolver routeResolver,
        IAccountSetupRunner setupRunner,
        IKnownImapProviderCatalog providerCatalog,
        INewThemeService themeService,
        IMailDialogService dialogService,
        ISpecialImapProviderConfigResolver specialImapProviderConfigResolver,
        ISynchronizationManager synchronizationManager,
        ICalDavClient calDavClient,
        WelcomeWizardContext wizardContext,
        ICardDavClient? cardDavClient = null,
        IOAuthTokenService? oauthTokenService = null,
        IDavOAuthAuthenticationProvider? davOAuthAuthenticationProvider = null,
        IExternalLauncher? externalLauncher = null)
    {
        _accountService = accountService;
        _routeResolver = routeResolver;
        _setupRunner = setupRunner;
        _providerCatalog = providerCatalog;
        _themeService = themeService;
        _dialogService = dialogService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
        _synchronizationManager = synchronizationManager;
        _calDavClient = calDavClient;
        _cardDavClient = cardDavClient;
        _oauthTokenService = oauthTokenService;
        _davOAuthAuthenticationProvider = davOAuthAuthenticationProvider;
        _externalLauncher = externalLauncher;
        WizardContext = wizardContext;

        SelectedInitialSynchronizationRange = InitialSynchronizationRanges.First(option => option.Range == InitialSynchronizationRange.SixMonths);
        Capabilities.PropertyChanged += OnCapabilitiesPropertyChanged;
        Checks.CollectionChanged += OnChecksCollectionChanged;
    }

    public bool IsSettingsHost => _hostMode == ProviderSelectionHostMode.SettingsAddAccount;

    public override void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        _hostMode = (parameters as ProviderSelectionNavigationContext)?.HostMode ?? ProviderSelectionHostMode.Wizard;
        OnPropertyChanged(nameof(IsSettingsHost));

        AvailableColors = _themeService.GetAvailableAccountColors()
            .Select(hex => new AppColorViewModel(hex))
            .ToList();
        OnPropertyChanged(nameof(AvailableColors));

        if (mode == NavigationMode.Back)
        {
            // Advanced setup took over; coming back starts again from the address the user typed.
            AccountName = WizardContext.AccountName ?? string.Empty;
            EmailAddress = WizardContext.EmailAddress ?? string.Empty;
            SelectedColor = AvailableColors.FirstOrDefault(color => color.Hex == WizardContext.AccountColorHex);
        }
        else
        {
            SelectedColor = null;
            WizardContext.Reset();
        }

        Phase = AccountSetupPhase.Identity;
        NotifyPhaseLayoutChanged();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        CancelOperation();

        if (Phase is not (AccountSetupPhase.Added or AccountSetupPhase.Adding))
            _ = DiscardSignInAsync();

        base.OnNavigatedFrom(mode, parameters);
    }

    [RelayCommand]
    private void SelectColor(string? hex)
        => SelectedColor = string.IsNullOrEmpty(hex) ? null : AvailableColors.FirstOrDefault(color => color.Hex == hex);

    private bool CanContinue() => Phase == AccountSetupPhase.Identity &&
                                  (IsLocalOnlyMode ? !string.IsNullOrWhiteSpace(AccountName) : !string.IsNullOrWhiteSpace(EmailAddress));

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task ContinueAsync()
    {
        IdentityError = string.Empty;
        AccountNameError = string.Empty;

        var name = AccountName?.Trim() ?? string.Empty;
        var address = EmailAddress?.Trim() ?? string.Empty;

        if (IsLocalOnlyMode)
        {
            if (name.Length == 0)
            {
                AccountNameError = Translator.AccountSetup_AccountNameRequired;
                return;
            }

            if (await IsAccountNameTakenAsync(name))
                return;

            _isAccountNameDerived = false;
            SaveIdentityDraft();
            ConfigureLocalFeatures();
            FeatureStep = AccountSetupStep.Calendar;
            Phase = AccountSetupPhase.Features;
            return;
        }

        if (!MailAccountAddressValidator.IsValid(address))
        {
            IdentityError = Translator.AccountSetup_EmailInvalid;
            return;
        }

        if (await IsAddressTakenAsync(address))
            return;

        if (_isAccountNameDerived)
        {
            AccountName = string.Empty;
            _isAccountNameDerived = false;
        }
        else if (name.Length > 0 && await IsAccountNameTakenAsync(name))
        {
            return;
        }

        EmailAddress = address;
        SaveIdentityDraft();

        await ResolveAsync(address);
    }

    private bool CanChangeAddress() => IsChangeAddressVisible;

    /// <summary>Returns to the address step and discards the route, the selections and any sign-in.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeAddress))]
    private Task ChangeAddressAsync() => ReturnToAddressAsync();

    private async Task ReturnToAddressAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();
        ClearRoute();

        if (_isAccountNameDerived)
        {
            AccountName = string.Empty;
            _isAccountNameDerived = false;
        }

        IdentityError = string.Empty;
        AccountNameError = string.Empty;
        Phase = AccountSetupPhase.Identity;
    }

    [RelayCommand]
    private async Task StartLocalOnlyAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();
        ClearRoute();

        IsLocalOnlyMode = true;
        IdentityError = string.Empty;
        AccountNameError = string.Empty;
        Phase = AccountSetupPhase.Identity;
        NotifyStepChanged();
    }

    private async Task LeaveLocalOnlyAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();
        ClearRoute();

        IsLocalOnlyMode = false;
        IdentityError = string.Empty;
        AccountNameError = string.Empty;
        Phase = AccountSetupPhase.Identity;
        NotifyStepChanged();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        CancelOperation();
        await DiscardSignInAsync();

        Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
    }

    [RelayCommand]
    private async Task OpenHelpLinkAsync(string? url)
    {
        if (_externalLauncher == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        try
        {
            await _externalLauncher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening an account setup help link failed.");
        }
    }

    private async Task<bool> IsAccountNameTakenAsync(string name)
    {
        try
        {
            if (!await _accountService.AccountNameExistsAsync(name))
                return false;

            AccountNameError = Translator.DialogMessage_AccountNameExistsMessage;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Checking the account name during setup failed.");
            return false;
        }
    }

    private async Task<bool> IsAddressTakenAsync(string address)
    {
        try
        {
            if (!await _accountService.AccountAddressExistsAsync(address))
                return false;

            IdentityError = Translator.DialogMessage_AccountAddressExistsMessage;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Checking the account address during setup failed.");
            return false;
        }
    }

    private void SaveIdentityDraft()
    {
        WizardContext.AccountName = _isAccountNameDerived ? string.Empty : AccountName?.Trim() ?? string.Empty;
        WizardContext.EmailAddress = IsLocalOnlyMode ? string.Empty : EmailAddress?.Trim() ?? string.Empty;
        WizardContext.AccountColorHex = SelectedColor?.Hex ?? string.Empty;
    }

    private CancellationTokenSource StartOperation()
    {
        CancelOperation();

        var operation = new CancellationTokenSource();
        _operation = operation;
        return operation;
    }

    private void CancelOperation()
    {
        var operation = _operation;
        _operation = null;

        try
        {
            operation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished and released its source between reading the field and cancelling it.
        }
    }

    partial void OnPhaseChanged(AccountSetupPhase value)
    {
        if (value != AccountSetupPhase.Resolving)
            _isDiscoveryShown = false;

        NotifyPhaseLayoutChanged();
    }

    partial void OnIsLocalOnlyModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmailFieldVisible));
        OnPropertyChanged(nameof(RouteShortTitle));
        ContinueCommand.NotifyCanExecuteChanged();
        PrimaryCommand.NotifyCanExecuteChanged();
    }

    partial void OnAccountNameChanged(string value)
    {
        _isAccountNameDerived = false;

        if (HasAccountNameError)
            AccountNameError = string.Empty;
    }

    partial void OnEmailAddressChanged(string value)
    {
        if (HasIdentityError)
            IdentityError = string.Empty;
    }

    private void NotifyPhaseLayoutChanged()
    {
        NotifyRoutingLayoutChanged();
        NotifyFeaturesLayoutChanged();
        NotifySignInLayoutChanged();
        NotifyProgressLayoutChanged();
        NotifyStepChanged();

        ContinueCommand.NotifyCanExecuteChanged();
        SignInWithProviderCommand.NotifyCanExecuteChanged();
        SignInWithBrowserCommand.NotifyCanExecuteChanged();
        CheckPasswordCommand.NotifyCanExecuteChanged();
        CheckDavPasswordCommand.NotifyCanExecuteChanged();
        AddAccountCommand.NotifyCanExecuteChanged();
    }
}
