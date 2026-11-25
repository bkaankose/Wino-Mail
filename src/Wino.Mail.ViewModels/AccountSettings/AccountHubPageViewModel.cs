#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// The page of one account: who it is, which apps it provides, and the way to its sign-in, troubleshooting and removal.
/// Every change here applies at once.
/// </summary>
public partial class AccountHubPageViewModel : CoreBaseViewModel, IRecipient<AccountUpdatedMessage>
{
    private static readonly TimeSpan NameSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IPictureStorageService _pictureStorageService;
    private readonly IWinoLogger _logger;
    private readonly IMailProviderRegistry _providers;
    private readonly AccountAppChangeCoordinator _appChanges;

    private bool _isLoading;
    private CancellationTokenSource? _nameSaveCancellation;

    public AccountHubPageViewModel(
        IMessenger messenger,
        IMailDialogService dialogService,
        IAccountService accountService,
        ISynchronizationManager synchronizationManager,
        INotificationBuilder notificationBuilder,
        IPictureStorageService pictureStorageService,
        INewThemeService themeService,
        IWinoLogger logger,
        IMailProviderRegistry providers,
        AccountAppChangeCoordinator appChanges) : base(messenger)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _synchronizationManager = synchronizationManager;
        _notificationBuilder = notificationBuilder;
        _pictureStorageService = pictureStorageService;
        _logger = logger;
        _providers = providers;
        _appChanges = appChanges;

        AvailableColors = themeService.GetAvailableAccountColors().Select(hex => new AppColorViewModel(hex)).ToList();

        foreach (var app in AccountApps.All)
            Apps.Add(new AccountAppItemViewModel(app, OnAppToggleRequested));
    }

    public ObservableCollection<AccountAppItemViewModel> Apps { get; } = [];

    public List<AppColorViewModel> AvailableColors { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Address))]
    [NotifyPropertyChangedFor(nameof(ProviderName))]
    [NotifyPropertyChangedFor(nameof(HeaderDescription))]
    [NotifyPropertyChangedFor(nameof(HasProfilePicture))]
    [NotifyPropertyChangedFor(nameof(CanRefreshProfilePicture))]
    [NotifyPropertyChangedFor(nameof(ProfilePictureDescription))]
    [NotifyPropertyChangedFor(nameof(IsSignInRowVisible))]
    [NotifyPropertyChangedFor(nameof(IsSignInStatusVisible))]
    [NotifyPropertyChangedFor(nameof(SignInStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SignInStatusDetail))]
    [NotifyPropertyChangedFor(nameof(SignInRowDescription))]
    [NotifyPropertyChangedFor(nameof(SyncStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SyncStatusDetail))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    [NotifyPropertyChangedFor(nameof(IsTroubleshootingVisible))]
    [NotifyPropertyChangedFor(nameof(RemoveDescription))]
    [NotifyCanExecuteChangedFor(nameof(ChangeProfilePictureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProfilePictureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshProfilePictureCommand))]
    public partial MailAccount? Account { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AppColorViewModel? SelectedColor { get; set; }

    [ObservableProperty]
    public partial bool IsIdentityEditorOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeProfilePictureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProfilePictureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshProfilePictureCommand))]
    public partial bool IsProfilePictureBusy { get; set; }

    public string Address => Account?.Address ?? string.Empty;

    public string ProviderName => Account == null
        ? string.Empty
        : new ProviderDetail(Account.ProviderType, Account.SpecialImapProvider).Name;

    public string HeaderDescription => string.IsNullOrWhiteSpace(Address) ? ProviderName : $"{Address} · {ProviderName}";

    public bool HasProfilePicture => Account?.ProfilePictureFileId.HasValue == true;

    public bool CanRefreshProfilePicture => Account != null && Traits.SupportsProfileInfoSync;

    public string ProfilePictureDescription => CanRefreshProfilePicture
        ? Translator.AccountDetailsPage_ProfilePictureDescription
        : Translator.AccountDetailsPage_ProfilePictureChooseDescription;

    public bool NeedsAttention => Account?.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed;

    public bool IsSignInRowVisible => Account is { IsLocalOnly: false };

    public bool IsSignInStatusVisible => IsSignInRowVisible;

    public string SignInStatusTitle => NeedsAttention ? Translator.AccountHub_SignInNeeded : Translator.AccountHub_SignedIn;

    public string SignInStatusDetail
    {
        get
        {
            if (Account == null)
                return string.Empty;

            if (Traits.UsesInteractiveOAuth)
                return ProviderName;

            var server = Account.ServerInformation;
            var usesSecureSignIn = server != null &&
                (server.UseOAuthAuthentication || server.IncomingAuthenticationMethod == ImapAuthenticationMethod.OAuth2);

            return usesSecureSignIn ? Translator.AccountHub_SignInMethodSecure : Translator.AccountHub_SignInMethodPassword;
        }
    }

    public string SignInRowDescription => Account == null || Traits.ServesModesFromProvider
        ? Translator.AccountHub_SignInRowProviderDescription
        : Translator.AccountHub_SignInRowServersDescription;

    public string SyncStatusTitle => Account == null
        ? string.Empty
        : NeedsAttention
            ? Translator.AccountHub_SyncNeedsAttention
            : _synchronizationManager.IsAccountSynchronizing(Account.Id)
                ? Translator.AccountHub_SyncRunning
                : Translator.AccountHub_SyncUpToDate;

    public string SyncStatusDetail => Account?.IsLocalOnly == true
        ? Translator.AccountHub_SyncLocalOnly
        : Translator.AccountHub_SyncDetail;

    /// <summary>Protocol logs exist for the accounts that talk to IMAP, POP3 and SMTP servers.</summary>
    public bool IsTroubleshootingVisible => Account is { IsLocalOnly: false, ServerInformation: not null } && !Traits.ServesModesFromProvider;

    public string RemoveDescription => Account?.IsLocalOnly == true
        ? Translator.SettingsDeleteAccount_LocalDescription
        : Translator.AccountHub_RemoveDescription;

    private MailProviderTraits Traits => Account == null ? MailProviderTraits.None : _providers.GetTraits(Account.ProviderType);

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeAsync(mode, parameters);

    public async Task InitializeAsync(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        var accountId = parameters switch
        {
            Guid id => id,
            AccountDetailsNavigationContext context => context.AccountId,
            _ => Guid.Empty
        };

        if (accountId == Guid.Empty)
            return;

        await LoadAsync(accountId);

        // Coming back from a page this one opened must not open it again.
        if (mode == NavigationMode.Back || parameters is not AccountDetailsNavigationContext navigationContext || Account == null)
            return;

        if (navigationContext.OpenServerSettings)
        {
            await ExecuteUIThread(OpenSignIn);
            return;
        }

        var requestedApp = navigationContext.SelectedTab switch
        {
            AccountDetailsTab.Mail => WinoApplicationMode.Mail,
            AccountDetailsTab.Calendar => WinoApplicationMode.Calendar,
            AccountDetailsTab.People => WinoApplicationMode.Contacts,
            AccountDetailsTab.ToDo => WinoApplicationMode.Tasks,
            _ => (WinoApplicationMode?)null
        };

        if (requestedApp is { } app && AccountApps.GetMode(Account, Traits, app) != AccountCapabilityMode.Off)
            await ExecuteUIThread(() => OpenApp(app));
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        FlushPendingNameSave();
        base.OnNavigatedFrom(mode, parameters);
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
        if (Account == null || message.Account?.Id != Account.Id || _isLoading)
            return;

        _ = ExecuteUIThread(() => ApplyAccount(message.Account, refreshIdentity: false));
    }

    private async Task LoadAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId);
        if (account == null)
            return;

        await ExecuteUIThread(() => ApplyAccount(account, refreshIdentity: true));
    }

    private void ApplyAccount(MailAccount account, bool refreshIdentity)
    {
        _isLoading = true;

        try
        {
            Account = account;

            if (refreshIdentity)
            {
                AccountName = account.Name ?? string.Empty;
                SelectedColor = string.IsNullOrEmpty(account.AccountColorHex)
                    ? null
                    : AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, account.AccountColorHex, StringComparison.OrdinalIgnoreCase));
            }

            var traits = Traits;
            foreach (var item in Apps)
            {
                var state = AccountApps.Evaluate(account, traits, item.App);
                item.Apply(account, state, AccountApps.IsLastAppOn(account, traits, item.App));
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void OnAppToggleRequested(AccountAppItemViewModel item, bool isOn)
    {
        // Let the switch finish its own change before it is put back or busy.
        await Task.Yield();

        if (Account == null || item.State is not { } state)
            return;

        var mode = isOn ? state.DefaultOnMode : AccountCapabilityMode.Off;

        if (_appChanges.RequiresServerSetup(Account, state, mode))
        {
            item.Revert();
            _appChanges.OpenServerSetup(Account, item.App, mode);
            return;
        }

        await ChangeAppAsync(item, mode);
    }

    private async Task ChangeAppAsync(AccountAppItemViewModel item, AccountCapabilityMode mode)
    {
        if (Account == null)
            return;

        var accountId = Account.Id;
        item.IsBusy = true;

        try
        {
            var updated = await _appChanges.ChangeAsync(accountId, item.App, mode, () => _ = ExecuteUIThread(() => item.IsOn = mode != AccountCapabilityMode.Off));

            if (updated == null)
            {
                item.Revert();
                return;
            }

            await ExecuteUIThread(() => ApplyAccount(updated, refreshIdentity: false));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenApp(WinoApplicationMode app)
    {
        if (Account == null)
            return;

        Messenger.Send(new BreadcrumbNavigationRequested(AccountAppPresenter.GetTitle(app), AccountAppPresenter.GetPage(app), Account.Id));
    }

    [RelayCommand]
    private void OpenSignIn()
    {
        if (Account != null)
            _appChanges.OpenSignIn(Account);
    }

    [RelayCommand]
    private void OpenTroubleshooting()
    {
        if (Account != null)
            Messenger.Send(new BreadcrumbNavigationRequested(Translator.AccountHub_TroubleshootingTitle, WinoPage.AccountTroubleshootingPage, Account.Id));
    }

    [RelayCommand]
    private void ToggleIdentityEditor() => IsIdentityEditorOpen = !IsIdentityEditorOpen;

    [RelayCommand]
    private void ResetColor() => SelectedColor = null;

    partial void OnAccountNameChanged(string value)
    {
        if (_isLoading || Account == null)
            return;

        _nameSaveCancellation?.Cancel();
        _nameSaveCancellation = new CancellationTokenSource();
        _ = SaveNameAfterDelayAsync(_nameSaveCancellation.Token);
    }

    private async Task SaveNameAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(NameSaveDelay, cancellationToken);
            await SaveIdentityAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FlushPendingNameSave()
    {
        if (_nameSaveCancellation is not { IsCancellationRequested: false } pending)
            return;

        pending.Cancel();
        _ = SaveIdentityAsync();
    }

    partial void OnSelectedColorChanged(AppColorViewModel? value)
    {
        if (!_isLoading && Account != null)
            _ = SaveIdentityAsync();
    }

    /// <summary>Saves the account name and color. An empty name keeps the stored one.</summary>
    private async Task SaveIdentityAsync()
    {
        var account = Account;
        if (account == null)
            return;

        var name = AccountName?.Trim();
        var colorHex = SelectedColor?.Hex ?? string.Empty;

        if ((string.IsNullOrEmpty(name) || name == account.Name) && colorHex == (account.AccountColorHex ?? string.Empty))
            return;

        try
        {
            var stored = await _accountService.GetAccountAsync(account.Id);
            if (stored == null)
                return;

            if (!string.IsNullOrEmpty(name))
                stored.Name = name;

            stored.AccountColorHex = colorHex;
            await _accountService.UpdateAccountAsync(stored);

            account.Name = stored.Name;
            account.AccountColorHex = stored.AccountColorHex;
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "SaveAccountIdentity");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
    }

    private bool CanChangeProfilePicture() => Account != null && !IsProfilePictureBusy;

    [RelayCommand(CanExecute = nameof(CanChangeProfilePicture))]
    private async Task ChangeProfilePictureAsync()
    {
        var account = Account;
        if (account == null)
            return;

        var imageData = await _dialogService.PickWindowsFileContentAsync(".jpg", ".jpeg", ".png", ".webp");
        if (imageData == null || imageData.Length == 0)
            return;

        IsProfilePictureBusy = true;

        try
        {
            var previousPictureId = account.ProfilePictureFileId;
            var newPictureId = await _pictureStorageService.SavePictureAsync(PictureKind.AccountProfile, imageData);
            account.ProfilePictureFileId = newPictureId;
            account.Base64ProfilePictureData = string.Empty;
            account.IsProfilePictureBackfillComplete = true;

            try
            {
                await _accountService.UpdateAccountAsync(account);
            }
            catch
            {
                await _pictureStorageService.DeletePictureAsync(PictureKind.AccountProfile, newPictureId);
                account.ProfilePictureFileId = previousPictureId;
                throw;
            }

            if (previousPictureId is { } obsoletePictureId)
                await _pictureStorageService.DeletePictureAsync(PictureKind.AccountProfile, obsoletePictureId);

            await LoadAsync(account.Id);
            _dialogService.InfoBarMessage(
                Translator.AccountDetailsPage_ProfilePictureUpdatedTitle,
                Translator.AccountDetailsPage_ProfilePictureUpdatedMessage,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "ChangeAccountProfilePicture");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsProfilePictureBusy = false;
        }
    }

    private bool CanRemoveProfilePicture() => HasProfilePicture && !IsProfilePictureBusy;

    [RelayCommand(CanExecute = nameof(CanRemoveProfilePicture))]
    private async Task RemoveProfilePictureAsync()
    {
        var account = Account;
        if (account == null)
            return;

        IsProfilePictureBusy = true;

        try
        {
            var previousPictureId = account.ProfilePictureFileId;
            account.ProfilePictureFileId = null;
            account.Base64ProfilePictureData = string.Empty;
            account.IsProfilePictureBackfillComplete = true;
            await _accountService.UpdateAccountAsync(account);

            if (previousPictureId is { } obsoletePictureId)
                await _pictureStorageService.DeletePictureAsync(PictureKind.AccountProfile, obsoletePictureId);

            await LoadAsync(account.Id);
            _dialogService.InfoBarMessage(
                Translator.AccountDetailsPage_ProfilePictureRemovedTitle,
                Translator.AccountDetailsPage_ProfilePictureRemovedMessage,
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "RemoveAccountProfilePicture");
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            IsProfilePictureBusy = false;
        }
    }

    private bool CanRefreshProfilePictureFromProvider() => CanRefreshProfilePicture && !IsProfilePictureBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshProfilePictureFromProvider))]
    private async Task RefreshProfilePictureAsync()
    {
        var account = Account;
        if (account == null)
            return;

        IsProfilePictureBusy = true;

        try
        {
            var result = await _synchronizationManager.SynchronizeProfileAsync(account.Id);
            if (result.ProfileInformation == null)
                throw result.Exception ?? new InvalidOperationException(Translator.AccountDetailsPage_ProfilePictureRefreshFailedMessage);

            await _accountService.UpdateProfileInformationAsync(account.Id, result.ProfileInformation, removePictureWhenConfirmedAbsent: true);
            await LoadAsync(account.Id);

            var isFailure = result.ProfileInformation.ProfilePicture?.Status == ProfilePictureFetchStatus.FetchFailed;
            _dialogService.InfoBarMessage(
                isFailure ? Translator.AccountDetailsPage_ProfilePictureRefreshFailedTitle : Translator.AccountDetailsPage_ProfilePictureUpdatedTitle,
                isFailure ? Translator.AccountDetailsPage_ProfilePictureRefreshFailedMessage : Translator.AccountDetailsPage_ProfilePictureUpdatedMessage,
                isFailure ? InfoBarMessageType.Error : InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "RefreshAccountProfilePicture");
            _dialogService.InfoBarMessage(
                Translator.AccountDetailsPage_ProfilePictureRefreshFailedTitle,
                Translator.AccountDetailsPage_ProfilePictureRefreshFailedMessage,
                InfoBarMessageType.Error);
        }
        finally
        {
            IsProfilePictureBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAccountAsync()
    {
        var account = Account;
        if (account == null)
            return;

        // The title and message arguments are swapped on purpose; the translations were made that way.
        var isConfirmed = await _dialogService.ShowConfirmationDialogAsync(
            account.IsLocalOnly
                ? Translator.DialogMessage_DeleteLocalAccountConfirmationTitle
                : account.ProviderType == MailProviderType.POP3
                    ? Translator.DialogMessage_DeletePop3AccountConfirmationTitle
                    : Translator.DialogMessage_DeleteAccountConfirmationTitle,
            string.Format(Translator.DialogMessage_DeleteAccountConfirmationMessage, account.Name),
            Translator.Buttons_Remove);

        if (!isConfirmed)
            return;

        await _synchronizationManager.DestroySynchronizerAsync(account.Id);
        await _accountService.DeleteAccountAsync(account);
        await _notificationBuilder.UpdateTaskbarIconBadgeAsync();

        _dialogService.InfoBarMessage(Translator.Info_AccountDeletedTitle, string.Format(Translator.Info_AccountDeletedMessage, account.Name), InfoBarMessageType.Success);

        Messenger.Send(new BackBreadcrumNavigationRequested());
    }
}
