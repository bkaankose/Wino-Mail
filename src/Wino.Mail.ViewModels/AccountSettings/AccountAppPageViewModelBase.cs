#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Server;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// The page of one app of an account. Every app page has the same first two blocks: the switch, and "Syncs with" with
/// the source and, for CalDAV or CardDAV, the server. The app's own settings follow and are filled by the subclass.
/// </summary>
public abstract partial class AccountAppPageViewModelBase : CoreBaseViewModel, IRecipient<AccountUpdatedMessage>
{
    private bool _isApplyingSource;

    protected AccountAppPageViewModelBase(
        IMessenger messenger,
        WinoApplicationMode app,
        IAccountService accountService,
        IMailDialogService dialogService,
        IAccountCapabilityService capabilityService,
        AccountAppChangeCoordinator appChanges) : base(messenger)
    {
        AccountService = accountService;
        DialogService = dialogService;
        CapabilityService = capabilityService;
        AppChanges = appChanges;
        AppItem = new AccountAppItemViewModel(app, OnToggleRequested);
    }

    protected IAccountService AccountService { get; }
    protected IMailDialogService DialogService { get; }
    protected IAccountCapabilityService CapabilityService { get; }
    protected AccountAppChangeCoordinator AppChanges { get; }

    public WinoApplicationMode App => AppItem.App;

    public AccountAppItemViewModel AppItem { get; }

    public ObservableCollection<AccountAppSourceOption> SourceOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    [NotifyPropertyChangedFor(nameof(IsOff))]
    [NotifyPropertyChangedFor(nameof(IsSourceChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsSourceTextVisible))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(SourceDescription))]
    [NotifyPropertyChangedFor(nameof(IsServerRowVisible))]
    [NotifyPropertyChangedFor(nameof(ServerRowTitle))]
    [NotifyPropertyChangedFor(nameof(ServerRowDescription))]
    [NotifyPropertyChangedFor(nameof(ServerStatusText))]
    [NotifyPropertyChangedFor(nameof(ServerActionText))]
    [NotifyPropertyChangedFor(nameof(NeedsSignIn))]
    public partial AccountAppState? State { get; set; }

    [ObservableProperty]
    public partial MailAccount? Account { get; set; }

    [ObservableProperty]
    public partial AccountAppSourceOption? SelectedSourceOption { get; set; }

    [ObservableProperty]
    public partial bool IsChangingSource { get; set; }

    public bool IsOn => State?.IsOn == true;

    public bool IsOff => State != null && !State.IsOn;

    public string OffNote => string.Format(Translator.AccountApps_AppOffNote, AppItem.Title);

    public bool IsSourceChoiceVisible => State is { IsOn: true, HasSourceChoice: true };

    public bool IsSourceTextVisible => State is { IsOn: true, HasSourceChoice: false };

    public string SourceText => Account == null || State == null
        ? string.Empty
        : AccountAppPresenter.GetSourceTitle(Account, State, State.Mode);

    public string SourceDescription => Account == null || State == null
        ? string.Empty
        : State.App == WinoApplicationMode.Mail && Account.ProviderType == MailProviderType.POP3
            ? Translator.AccountApps_SummaryPop3Mail
            : AccountAppPresenter.GetSourceDescription(Account, State, State.Mode);

    public bool NeedsSignIn => State?.NeedsSignIn == true;

    /// <summary>The CalDAV or CardDAV server row, shown while the app is on and the account syncs it with a DAV server.</summary>
    public bool IsServerRowVisible => State is { IsOn: true, IsServiceDavServer: true };

    public string ServerRowTitle => App == WinoApplicationMode.Contacts ? Translator.AccountApps_SourceCardDav : Translator.AccountApps_SourceCalDav;

    public string ServerRowDescription
    {
        get
        {
            if (Account == null || State is not { HasDavServer: true })
                return Translator.AccountApps_ServerNotSet;

            var url = App == WinoApplicationMode.Contacts
                ? Account.ServerInformation?.CardDavServiceUrl
                : Account.ServerInformation?.CalDavServiceUrl;

            var otherApp = App switch
            {
                WinoApplicationMode.Calendar => WinoApplicationMode.Tasks,
                WinoApplicationMode.Tasks => WinoApplicationMode.Calendar,
                _ => (WinoApplicationMode?)null
            };

            var traits = AppChanges.GetTraits(Account);
            var isShared = otherApp is { } other && AccountApps.GetMode(Account, traits, other) == AccountCapabilityMode.Provider;

            return isShared
                ? string.Format(Translator.AccountApps_ServerAlsoUsedBy, url?.Trim(), AccountAppPresenter.GetTitle(otherApp!.Value))
                : url?.Trim() ?? string.Empty;
        }
    }

    public string ServerStatusText => State switch
    {
        { Mode: AccountCapabilityMode.Provider, NeedsSignIn: true } => Translator.AccountApps_NeedsSignIn,
        { Mode: AccountCapabilityMode.Provider } => Translator.AccountApps_ServerConnected,
        { HasDavServer: true } => Translator.AccountApps_ServerNotUsed,
        _ => Translator.AccountApps_ServerNotSetShort
    };

    public string ServerActionText => State is { HasDavServer: true }
        ? Translator.AccountApps_ChangeServer
        : Translator.AccountApps_AddServer;

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeAsync(mode, parameters);

    public async Task InitializeAsync(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is not Guid accountId)
            return;

        await ReloadAsync(accountId);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<AccountUpdatedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        Messenger.Unregister<AccountUpdatedMessage>(this);
        base.UnregisterRecipients();
    }

    public void Receive(AccountUpdatedMessage message)
    {
        if (Account == null || message.Account?.Id != Account.Id || AppItem.IsBusy || IsChangingSource)
            return;

        _ = ExecuteUIThread(() => ApplyAccount(message.Account));
    }

    protected async Task ReloadAsync(Guid accountId)
    {
        var account = await AccountService.GetAccountAsync(accountId);
        if (account == null)
            return;

        await ExecuteUIThread(() => ApplyAccount(account));
        await LoadContentAsync(account);
    }

    /// <summary>Fills the app's own settings. Runs after every load and after the app changed.</summary>
    protected virtual Task LoadContentAsync(MailAccount account) => Task.CompletedTask;

    /// <summary>Called on the UI thread whenever the stored account is read again.</summary>
    protected virtual void OnAccountApplied(MailAccount account) { }

    private void ApplyAccount(MailAccount account)
    {
        var traits = AppChanges.GetTraits(account);
        var state = AccountApps.Evaluate(account, traits, App);

        Account = account;
        State = state;
        AppItem.Apply(account, state, AccountApps.IsLastAppOn(account, traits, App));

        _isApplyingSource = true;

        try
        {
            var options = new List<AccountAppSourceOption>();

            if (state.IsServiceOffered)
            {
                options.Add(new AccountAppSourceOption(
                    AccountCapabilityMode.Provider,
                    AccountAppPresenter.GetSourceTitle(account, state, AccountCapabilityMode.Provider),
                    AccountAppPresenter.GetSourceDescription(account, state, AccountCapabilityMode.Provider)));
            }

            if (state.IsDeviceOffered)
            {
                options.Add(new AccountAppSourceOption(
                    AccountCapabilityMode.Local,
                    AccountAppPresenter.GetSourceTitle(account, state, AccountCapabilityMode.Local),
                    AccountAppPresenter.GetSourceDescription(account, state, AccountCapabilityMode.Local)));
            }

            // Replacing the items makes the radio list drop its selection after it is set again, so the
            // existing choices stay when they read the same.
            var isSameChoice = options.Count == SourceOptions.Count &&
                               options.Zip(SourceOptions).All(pair => pair.First.Mode == pair.Second.Mode &&
                                                                      pair.First.Title == pair.Second.Title &&
                                                                      pair.First.Description == pair.Second.Description);

            if (!isSameChoice)
            {
                SourceOptions.Clear();
                foreach (var option in options)
                    SourceOptions.Add(option);
            }

            SelectedSourceOption = SourceOptions.FirstOrDefault(option => option.Mode == state.Mode);

            // The list may have cleared its own selection while the change ran; the view reads the stored one again.
            OnPropertyChanged(nameof(SelectedSourceOption));
        }
        finally
        {
            _isApplyingSource = false;
        }

        OnPropertyChanged(nameof(ServerRowDescription));
        OnAccountApplied(account);
    }

    private async void OnToggleRequested(AccountAppItemViewModel item, bool isOn)
    {
        await Task.Yield();

        if (Account == null || State == null)
            return;

        var mode = isOn ? State.DefaultOnMode : AccountCapabilityMode.Off;

        if (AppChanges.RequiresServerSetup(Account, State, mode))
        {
            item.Revert();
            AppChanges.OpenServerSetup(Account, App, mode);
            return;
        }

        item.IsBusy = true;

        try
        {
            await ChangeAsync(mode, () => _ = ExecuteUIThread(() => item.IsOn = isOn));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    // RadioButtons clears its selection before it selects the clicked item, so the previous value can be null.
    partial void OnSelectedSourceOptionChanged(AccountAppSourceOption? value)
    {
        if (_isApplyingSource || value == null || State == null || value.Mode == State.Mode)
            return;

        _ = ChangeSourceAsync(value);
    }

    private async Task ChangeSourceAsync(AccountAppSourceOption option)
    {
        await Task.Yield();

        if (Account == null || State == null)
            return;

        if (AppChanges.RequiresServerSetup(Account, State, option.Mode))
        {
            RestoreSourceSelection();
            AppChanges.OpenServerSetup(Account, App, option.Mode);
            return;
        }

        IsChangingSource = true;

        try
        {
            await ChangeAsync(option.Mode, () => _ = ExecuteUIThread(() => SelectedSourceOption = option));
        }
        finally
        {
            IsChangingSource = false;
        }
    }

    private async Task ChangeAsync(AccountCapabilityMode mode, Action retry)
    {
        if (Account == null)
            return;

        var updated = await AppChanges.ChangeAsync(Account.Id, App, mode, retry);

        if (updated == null)
        {
            AppItem.Revert();
            RestoreSourceSelection();
            return;
        }

        await ExecuteUIThread(() => ApplyAccount(updated));
        await LoadContentAsync(updated);
    }

    private void RestoreSourceSelection()
    {
        _isApplyingSource = true;

        try
        {
            SelectedSourceOption = SourceOptions.FirstOrDefault(option => option.Mode == State?.Mode);
            OnPropertyChanged(nameof(SelectedSourceOption));
        }
        finally
        {
            _isApplyingSource = false;
        }
    }

    /// <summary>Opens Sign-in and servers on the server this app uses, offering to move the app there when it is not.</summary>
    [RelayCommand]
    private void OpenServerSettings()
    {
        if (Account == null || State == null)
            return;

        if (State.Mode == AccountCapabilityMode.Provider)
            AppChanges.OpenSignIn(Account);
        else
            AppChanges.OpenServerSetup(Account, App, AccountCapabilityMode.Provider);
    }

    /// <summary>
    /// A refused password is fixed on Sign-in and servers. A provider that withdrew its consent for People or To Do
    /// asks for it again in the browser, through the same transition the old account page used.
    /// </summary>
    [RelayCommand]
    private async Task SignInAgainAsync()
    {
        var account = Account;
        if (account == null)
            return;

        var traits = AppChanges.GetTraits(account);
        var isCredentialProblem = account.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed;

        if (isCredentialProblem || !traits.ServesModesFromProvider || App is not (WinoApplicationMode.Contacts or WinoApplicationMode.Tasks))
        {
            AppChanges.OpenSignIn(account);
            return;
        }

        AppItem.IsBusy = true;

        try
        {
            var updated = await CapabilityService.ApplyAsync(
                account,
                account.IsMailAccessGranted,
                account.IsCalendarAccessGranted,
                account.IsContactAccessEnabled,
                account.IsTaskAccessEnabled);

            if (App == WinoApplicationMode.Contacts)
                Messenger.Send(new NewContactSynchronizationRequested(new ContactSynchronizationOptions { AccountId = updated.Id, Type = ContactSynchronizationType.Delta }));
            else
                Messenger.Send(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions { AccountId = updated.Id, Type = TaskSynchronizationType.Full, IsUserInitiated = true }));

            await ExecuteUIThread(() => ApplyAccount(updated));
        }
        catch (Exception ex)
        {
            DialogService.InfoBarMessage(Translator.AccountSetup_Failure_SignInTitle, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            AppItem.IsBusy = false;
        }
    }
}
