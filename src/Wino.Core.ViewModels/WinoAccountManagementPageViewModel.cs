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
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel : CoreBaseViewModel,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoAccountDataSyncService _syncService;
    private readonly IMailDialogService _dialogService;
    private readonly IWinoBillingService _billingService;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly IAccountService _accountService;
    private readonly ISemanticIndexCoordinator _semanticIndexCoordinator;
    private readonly IWinoAccountIntelligenceSnapshotService? _snapshotService;
    private readonly WinoAddOnItemViewModel _aiPackAddOn;
    private readonly WinoAddOnItemViewModel _unlimitedAccountsAddOn;
    private bool _isLoading;
    private string _intelligencePolicyVersion = string.Empty;

    public ObservableCollection<WinoAddOnItemViewModel> AddOns { get; } = [];
    public ObservableCollection<WinoIntelligenceMailboxItemViewModel> IntelligenceMailboxes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshPurchasesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteIntelligenceCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string AccountEmail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PurchaseAddOnCommand))]
    public partial bool IsCheckoutInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntelligenceStatusShortText))]
    public partial bool HasIntelligenceAccess { get; set; }

    [ObservableProperty]
    public partial bool IsIntelligenceUsageAvailable { get; set; }

    [ObservableProperty]
    public partial double IntelligenceUsagePercentage { get; set; }

    [ObservableProperty]
    public partial string IntelligenceUsageSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IntelligenceResetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IntelligenceStorageSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IntelligenceConsentStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConsentBusy { get; set; }

    [ObservableProperty]
    public partial bool IsConsentGranted { get; set; }

    [ObservableProperty]
    public partial Uri? ConsentPolicyUri { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConsentDeletionPending))]
    [NotifyPropertyChangedFor(nameof(IsConsentDeletionFailed))]
    public partial string ConsentDataDeletionStatus { get; set; } = IntelligenceDeletionStatuses.NotRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConsentError))]
    public partial string ConsentErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntelligenceDataError))]
    public partial string IntelligenceDataError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsIntelligenceRefreshing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntelligenceRefreshError))]
    public partial string IntelligenceRefreshError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IntelligenceLastUpdatedText { get; set; } = string.Empty;

    /// <summary>
    /// Billing window for the AI pack, from the subscription's current period.
    /// </summary>
    [ObservableProperty]
    public partial string AiPackBillingPeriodText { get; set; } = string.Empty;

    /// <summary>
    /// Reads "Renews {date}" normally, but "Cancels {date}" once the subscription is
    /// set to lapse at the end of the period.
    /// </summary>
    [ObservableProperty]
    public partial string AiPackRenewalOrCancellationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiPackSubtitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UnlimitedAccountsSubtitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountUsageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double AccountUsagePercentage { get; set; }

    public WinoAddOnItemViewModel AiPackAddOn => _aiPackAddOn;

    public WinoAddOnItemViewModel UnlimitedAccountsAddOn => _unlimitedAccountsAddOn;

    /// <summary>
    /// Signed-out benefit blurb, which has to name the free account limit.
    /// </summary>
    public string UnlimitedAccountsBenefitDescription
        => string.Format(Translator.WinoAccount_Management_BenefitUnlimitedDescription, Constants.FreeAccountLimit);

    /// <summary>
    /// Compact intelligence state for the account header, next to the mail account count.
    /// </summary>
    public string IntelligenceStatusShortText => HasIntelligenceAccess
        ? Translator.WinoAccount_Management_ActiveShort
        : Translator.WinoAccount_Management_NotActiveShort;

    public bool HasIntelligenceDataError => !string.IsNullOrWhiteSpace(IntelligenceDataError);
    public bool HasIntelligenceRefreshError => !string.IsNullOrWhiteSpace(IntelligenceRefreshError);

    public bool HasConsentError => !string.IsNullOrWhiteSpace(ConsentErrorMessage);

    public bool IsConsentDeletionPending => ConsentDataDeletionStatus == IntelligenceDeletionStatuses.Pending;

    public bool IsConsentDeletionFailed => ConsentDataDeletionStatus == IntelligenceDeletionStatuses.Failed;

    public bool IsSignedOut => !IsSignedIn;

    public WinoAccountManagementPageViewModel(IWinoAccountProfileService profileService,
                                               IWinoAccountDataSyncService syncService,
                                               IMailDialogService dialogService,
                                               IWinoBillingService billingService,
                                               IWinoAccountApiClient apiClient,
                                               IAccountService accountService,
                                               ISemanticIndexCoordinator semanticIndexCoordinator,
                                               IWinoAccountIntelligenceSnapshotService? snapshotService = null)
    {
        _profileService = profileService;
        _syncService = syncService;
        _dialogService = dialogService;
        _billingService = billingService;
        _apiClient = apiClient;
        _accountService = accountService;
        _semanticIndexCoordinator = semanticIndexCoordinator;
        _snapshotService = snapshotService;

        _aiPackAddOn = CreateAddOnItem(WinoAddOnProductType.AI_PACK);
        _unlimitedAccountsAddOn = CreateAddOnItem(WinoAddOnProductType.UNLIMITED_ACCOUNTS);
        AddOns.Add(_aiPackAddOn);
        AddOns.Add(_unlimitedAccountsAddOn);

        ApplySubtitleTexts();
        ApplyAccountUsage(mailAccountCount: 0, hasUnlimitedAccounts: false);
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        var forceProfileRefresh = parameters is WinoAccountManagementActivationReason.CheckoutCompleted;
        _ = InitializeAsync(forceProfileRefresh);
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        var account = await _dialogService.ShowWinoAccountRegistrationDialogAsync();
        if (account == null)
        {
            return;
        }

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_RegisterSuccessMessage, account.Email),
                                      InfoBarMessageType.Success);
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        var account = await _dialogService.ShowWinoAccountLoginDialogAsync();
        if (account == null)
        {
            return;
        }

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_LoginSuccessMessage, account.Email),
                                      InfoBarMessageType.Success);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        var account = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (account == null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                          Translator.WinoAccount_SignOut_NoAccountMessage,
                                          InfoBarMessageType.Warning);
            return;
        }

        await _profileService.SignOutAsync().ConfigureAwait(false);

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_SignOut_SuccessMessage, account.Email),
                                      InfoBarMessageType.Success);
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var account = await _profileService.GetActiveAccountAsync();
        if (account == null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                          Translator.WinoAccount_SignOut_NoAccountMessage,
                                          InfoBarMessageType.Warning);
            return;
        }

        var shouldContinue = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.WinoAccount_ChangePassword_ConfirmationMessage, account.Email),
            Translator.WinoAccount_ChangePassword_Title,
            Translator.WinoAccount_ChangePassword_Action);

        if (!shouldContinue)
        {
            return;
        }

        var response = await _profileService.ForgotPasswordAsync(account.Email);
        if (!response.IsSuccess)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          TranslateForgotPasswordError(response.ErrorCode),
                                          InfoBarMessageType.Error);
            return;
        }

        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info,
                                      string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, account.Email),
                                      InfoBarMessageType.Success);
    }

    private static string TranslateForgotPasswordError(string? errorCode)
        => errorCode switch
        {
            ApiErrorCodes.EmailNotRegistered => Translator.WinoAccount_Error_EmailNotRegistered,
            ApiErrorCodes.ValidationFailed => Translator.WinoAccount_Error_ValidationFailed,
            _ when string.IsNullOrWhiteSpace(errorCode) => Translator.GeneralTitle_Error,
            _ => errorCode!
        };

    [RelayCommand(CanExecute = nameof(CanPurchaseAddOn))]
    private async Task PurchaseAddOnAsync(WinoAddOnItemViewModel? addOn)
    {
        if (addOn == null)
        {
            return;
        }

        if (await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false) == null)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Warning,
                Translator.WinoAccount_Management_CheckoutSignInRequired,
                InfoBarMessageType.Warning);
            return;
        }

        await ExecuteUIThread(() =>
        {
            IsCheckoutInProgress = true;
            addOn.IsPurchaseInProgress = true;
        });

        try
        {
            if (!await _billingService.OpenCheckoutAsync(addOn.ProductType).ConfigureAwait(false))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                Translator.WinoAccount_Management_CheckoutOpened,
                InfoBarMessageType.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_PurchaseStartFailed,
                                          InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                IsCheckoutInProgress = false;
                addOn.IsPurchaseInProgress = false;
            });
        }
    }

    private bool CanPurchaseAddOn(WinoAddOnItemViewModel? addOn)
        => addOn != null && !addOn.IsPurchased && !addOn.IsLoading && !IsCheckoutInProgress;

    [RelayCommand(CanExecute = nameof(CanRefreshPurchases))]
    private Task RefreshPurchasesAsync() => LoadAsync(forceProfileRefresh: true);

    private bool CanRefreshPurchases() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanManageIntelligenceMailbox))]
    private void ManageIntelligenceMailbox(WinoIntelligenceMailboxItemViewModel? mailbox)
    {
        if (mailbox?.LocalAccountId is not Guid localAccountId)
        {
            return;
        }

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.SettingsManageAccountSettings_Title,
            WinoPage.ManageAccountsPage));
        Messenger.Send(new BreadcrumbNavigationRequested(
            mailbox.Address,
            WinoPage.AccountDetailsPage,
            localAccountId));
        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.SemanticIndex_PageTitle,
            WinoPage.WinoIntelligenceManagementPage,
            localAccountId));
    }

    private static bool CanManageIntelligenceMailbox(WinoIntelligenceMailboxItemViewModel? mailbox)
        => mailbox?.CanManage == true;

    [RelayCommand]
    private void OpenIntelligenceManagement()
        => Messenger.Send(new SettingsRootNavigationRequested(WinoPage.WinoIntelligencePage));

    public async Task<bool> SetIntelligenceConsentAsync(bool granted)
    {
        await ExecuteUIThread(() =>
        {
            IsConsentBusy = true;
            ConsentErrorMessage = string.Empty;
        });

        try
        {
            var consent = granted
                ? await _apiClient.AcceptIntelligenceConsentAsync(
                    _intelligencePolicyVersion,
                    ConsentActionSources.ConsentPage).ConfigureAwait(false)
                : await _apiClient.RevokeIntelligenceConsentAsync(
                    ConsentActionSources.ConsentPage).ConfigureAwait(false);

            if (!granted)
                await DisableAndClearAllLocalIntelligenceAsync().ConfigureAwait(false);

            await ExecuteUIThread(() => ApplyIntelligenceConsent(consent));
            await LoadIntelligenceDataAsync().ConfigureAwait(false);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
            return IsCurrentIntelligenceConsent(consent);
        }
        catch (Exception exception)
        {
            await ExecuteUIThread(() =>
                ConsentErrorMessage = WinoAccountApiErrorTranslator.Translate(exception.Message));
            return IsConsentGranted;
        }
        finally
        {
            await ExecuteUIThread(() => IsConsentBusy = false);
        }
    }

    private async Task DisableAndClearAllLocalIntelligenceAsync()
    {
        foreach (var account in await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [])
        {
            await _semanticIndexCoordinator.DeleteLocalIndexAsync(account.Id).ConfigureAwait(false);
            if (!account.Preferences.IsSemanticIndexingEnabled)
                continue;

            account.Preferences.IsSemanticIndexingEnabled = false;
            await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteIntelligence))]
    private async Task DeleteIntelligenceAsync(WinoIntelligenceMailboxItemViewModel? mailbox)
    {
        if (mailbox == null || !await _dialogService.ShowConfirmationDialogAsync(
                string.Format(Translator.WinoAccount_Management_IntelligenceDeleteConfirmation, mailbox.Address),
                Translator.WinoAccount_Management_IntelligenceDeleteTitle,
                Translator.Buttons_Delete))
        {
            return;
        }

        await ExecuteUIThread(() =>
        {
            mailbox.IsDeleting = true;
            DeleteIntelligenceCommand.NotifyCanExecuteChanged();
        });

        try
        {
            if (mailbox.LocalAccountId is Guid localAccountId)
            {
                await _semanticIndexCoordinator.DeleteIndexAsync(localAccountId).ConfigureAwait(false);
                var account = await _accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
                if (account != null)
                {
                    account.Preferences.IsSemanticIndexingEnabled = false;
                    await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                }
            }
            else
            {
                await _apiClient.DeleteIntelligenceAsync(mailbox.MailboxId).ConfigureAwait(false);
            }

            await LoadIntelligenceDataAsync().ConfigureAwait(false);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        }
        catch (Exception exception)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Translate(exception.Message),
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                mailbox.IsDeleting = false;
                DeleteIntelligenceCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private bool CanDeleteIntelligence(WinoIntelligenceMailboxItemViewModel? mailbox)
        => mailbox?.HasServerIntelligence == true && !mailbox.IsDeleting && !IsBusy;

    [RelayCommand]
    private async Task ToggleIntelligenceMailboxAsync(WinoIntelligenceMailboxItemViewModel? mailbox)
    {
        if (mailbox?.LocalAccountId is not Guid localAccountId || mailbox.IsChangingEnabled)
            return;

        var requested = !mailbox.IsEnabled;
        await ExecuteUIThread(() => mailbox.IsChangingEnabled = true);
        try
        {
            var account = await _accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
            if (account == null)
                return;

            if (requested)
                await _semanticIndexCoordinator.EnsureMailboxAsync(localAccountId).ConfigureAwait(false);
            else
                await _semanticIndexCoordinator.DeleteIndexAsync(localAccountId).ConfigureAwait(false);

            account.Preferences.IsSemanticIndexingEnabled = requested;
            await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            await ExecuteUIThread(() => mailbox.IsEnabled = requested);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        }
        catch (Exception exception)
        {
            await ExecuteUIThread(() =>
            {
                // The CheckBox is intentionally one-way bound. Pulse the value so a failed
                // server enable immediately restores the authoritative preference on screen.
                mailbox.IsEnabled = requested;
                mailbox.IsEnabled = !requested;
            });
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                WinoAccountApiErrorTranslator.Translate(exception.Message),
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => mailbox.IsChangingEnabled = false);
        }
    }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        try
        {
            var result = await _dialogService.ShowWinoAccountExportDialogAsync().ConfigureAwait(false);
            if (result == null)
            {
                return;
            }

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                BuildExportSuccessMessage(result),
                InfoBarMessageType.Success);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                ex.Message,
                InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        await ExecuteUIThread(() => IsBusy = true);

        try
        {
            var result = await _syncService.ImportAsync(new WinoAccountSyncSelection());

            if (!result.HasAnyRemoteData)
            {
                _dialogService.InfoBarMessage(
                    Translator.GeneralTitle_Info,
                    Translator.WinoAccount_Management_NoRemoteSettings,
                    InfoBarMessageType.Information);
                return;
            }

            var messageType = result.FailedPreferenceCount > 0
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Success;

            _dialogService.InfoBarMessage(
                result.FailedPreferenceCount > 0 ? Translator.GeneralTitle_Warning : Translator.GeneralTitle_Info,
                BuildImportMessage(result),
                messageType);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Error,
                ex.Message,
                InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Register<WinoAccountProfileDeletedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Unregister<WinoAccountProfileDeletedMessage>(this);
    }

    public void Receive(WinoAccountProfileUpdatedMessage message)
        => _ = LoadAsync();

    public void Receive(WinoAccountProfileDeletedMessage message)
        => _ = LoadAsync();

    private async Task InitializeAsync(bool forceProfileRefresh)
    {
        await LoadAsync(forceProfileRefresh).ConfigureAwait(false);
    }

    private async Task LoadAsync(bool forceProfileRefresh = false)
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        WinoAccount? cachedAccount = null;

        try
        {
            cachedAccount = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);

            if (cachedAccount != null)
            {
                await ApplyAccountStateAsync(cachedAccount).ConfigureAwait(false);
            }

            if (cachedAccount is null)
            {
                await ResetStateAsync().ConfigureAwait(false);
                return;
            }

            if (_snapshotService is null)
            {
                await ExecuteUIThread(() => IsBusy = true);
                await ResetAddOnStatesAsync().ConfigureAwait(false);
                await ResetIntelligenceDataAsync().ConfigureAwait(false);
                var resolvedAccount = cachedAccount;
                try
                {
                    var authenticatedAccount = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);
                    if (authenticatedAccount is not null)
                    {
                        resolvedAccount = authenticatedAccount;
                        if (forceProfileRefresh || IsAccessTokenExpired(cachedAccount))
                        {
                            var refreshed = await _profileService.RefreshProfileAsync().ConfigureAwait(false);
                            if (refreshed.IsSuccess && refreshed.Account is not null) resolvedAccount = refreshed.Account;
                        }
                    }
                }
                catch { }
                await ApplyAccountStateAsync(resolvedAccount).ConfigureAwait(false);
                await LoadAddOnsAsync(resolvedAccount).ConfigureAwait(false);
                return;
            }

            var cachedSnapshot = await _snapshotService.GetCachedAsync(cachedAccount.Id).ConfigureAwait(false);
            if (cachedSnapshot?.HasData == true)
                await ApplyAccountIntelligenceSnapshotAsync(cachedSnapshot, null).ConfigureAwait(false);
            else
                await ExecuteUIThread(() => IsBusy = true);

            _ = RefreshAccountIntelligenceSnapshotAsync(forceProfileRefresh);
        }
        catch (Exception)
        {
            if (cachedAccount == null)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_LoadFailed,
                                              InfoBarMessageType.Error);
                await ResetStateAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
            _isLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshPurchases))]
    private Task RetryIntelligenceRefreshAsync() => RefreshAccountIntelligenceSnapshotAsync(forceProfileRefresh: true);

    private async Task RefreshAccountIntelligenceSnapshotAsync(bool forceProfileRefresh)
    {
        if (_snapshotService is null) return;
        await ExecuteUIThread(() =>
        {
            IsIntelligenceRefreshing = true;
            IntelligenceRefreshError = string.Empty;
        });
        try
        {
            if (forceProfileRefresh)
                await _profileService.RefreshProfileAsync().ConfigureAwait(false);
            var result = await _snapshotService.RefreshAsync().ConfigureAwait(false);
            if (result is not null)
                await ApplyAccountIntelligenceSnapshotAsync(result.Snapshot, result.Error).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ExecuteUIThread(() => IntelligenceRefreshError = WinoAccountApiErrorTranslator.Translate(exception.Message));
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                IsBusy = false;
                IsIntelligenceRefreshing = false;
            });
        }
    }

    private async Task ApplyAccountIntelligenceSnapshotAsync(WinoAccountIntelligenceSnapshot snapshot, string? refreshError)
    {
        var localAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var mailboxItems = snapshot.Mailboxes.Select(mailbox => CreateCachedIntelligenceMailboxItem(
            mailbox, localAccounts, snapshot.MailboxStatuses.GetValueOrDefault(mailbox.MailboxId))).ToArray();
        mailboxItems = [.. mailboxItems, .. localAccounts.Where(account => mailboxItems.All(item => item.LocalAccountId != account.Id))
            .Select(CreateLocalIntelligenceMailboxItem)];
        var aiPack = snapshot.Billing?.AiPack;
        var usage = snapshot.Usage;
        await ExecuteUIThread(() =>
        {
            _aiPackAddOn.IsPurchased = aiPack?.HasAccess == true;
            _aiPackAddOn.IsLoading = false;
            _aiPackAddOn.ErrorText = string.Empty;
            _aiPackAddOn.RenewalText = aiPack?.RenewsAtUtc is DateTimeOffset renewal ? string.Format(Translator.WinoAccount_Management_AiPackRenews, renewal.LocalDateTime) : string.Empty;
            HasIntelligenceAccess = _aiPackAddOn.IsPurchased;
            ApplyAiPackBillingTexts(aiPack);
            IntelligenceMailboxes.Clear();
            foreach (var item in mailboxItems.OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase)) IntelligenceMailboxes.Add(item);
            IsIntelligenceUsageAvailable = usage is not null;
            IntelligenceUsagePercentage = usage is null ? 0 : (double)usage.UsagePercentage;
            IntelligenceUsageSummary = usage is null ? Translator.WinoAccount_Management_IntelligenceUsageUnavailable : string.Format(Translator.WinoAccount_Management_IntelligenceUsageSummary, usage.UsagePercentage, usage.RemainingPercentage);
            IntelligenceResetText = usage?.ResetsAtUtc is DateTimeOffset reset ? string.Format(Translator.WinoAccount_Management_IntelligenceResets, reset.LocalDateTime) : string.Empty;
            IntelligenceStorageSummary = string.Format(Translator.WinoAccount_Management_IntelligenceStorageSummary, mailboxItems.Count(item => item.HasServerIntelligence), FormatStorageSize(mailboxItems.Sum(item => item.StorageSizeBytes)));
            if (snapshot.Consent is not null) ApplyIntelligenceConsent(snapshot.Consent);
            IntelligenceLastUpdatedText = snapshot.LastSuccessfulRefreshUtc is DateTimeOffset updated ? updated.LocalDateTime.ToString("g") : string.Empty;
            IntelligenceRefreshError = string.IsNullOrWhiteSpace(refreshError) ? string.Empty : string.Format(
                Translator.WinoIntelligence_CachedRefreshFailed,
                string.IsNullOrWhiteSpace(IntelligenceLastUpdatedText) ? Translator.GeneralTitle_Info : IntelligenceLastUpdatedText);
            ApplySubtitleTexts();
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });
    }

    private IntelligenceMailboxData CreateCachedIntelligenceMailboxItem(SemanticMailboxDto mailbox, IReadOnlyList<MailAccount> localAccounts, IntelligenceMailboxStatusDto? status)
    {
        var localAccount = localAccounts.FirstOrDefault(account => (int)account.ProviderType == mailbox.ProviderType && string.Equals(account.Address?.Trim(), mailbox.Address?.Trim(), StringComparison.OrdinalIgnoreCase));
        var storage = status?.StorageSizeBytes ?? mailbox.IndexState?.StorageSizeBytes ?? 0;
        return new IntelligenceMailboxData
        {
            MailboxId = mailbox.MailboxId, Address = mailbox.Address, ProviderType = (MailProviderType)mailbox.ProviderType,
            SpecialProvider = localAccount?.SpecialImapProvider ?? SpecialImapProvider.None,
            LocalAccountId = localAccount?.Id, Account = localAccount, HasServerIntelligence = storage > 0,
            IsEnabled = localAccount?.Preferences?.IsSemanticIndexingEnabled == true,
            CanToggle = localAccount is not null && (localAccount.Preferences?.IsSemanticIndexingEnabled == true || HasIntelligenceAccess && IsConsentGranted),
            StorageSizeBytes = storage,
            IntelligenceSummary = string.Format(Translator.WinoAccount_Management_IntelligenceMailboxSummary, 0, 0, FormatStorageSize(storage)),
            ManageCommand = ManageIntelligenceMailboxCommand, DeleteCommand = DeleteIntelligenceCommand, ToggleEnabledCommand = ToggleIntelligenceMailboxCommand
        };
    }

    private async Task ApplyAccountStateAsync(Wino.Core.Domain.Entities.Shared.WinoAccount? account)
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = account != null;
            AccountEmail = account?.Email ?? string.Empty;
        });
    }

    private async Task ResetStateAsync()
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = false;
            AccountEmail = string.Empty;
            IsCheckoutInProgress = false;
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });

        await ResetAddOnStatesAsync().ConfigureAwait(false);
        await ResetIntelligenceDataAsync().ConfigureAwait(false);
    }

    private WinoAddOnItemViewModel CreateAddOnItem(WinoAddOnProductType productType)
    {
        return new WinoAddOnItemViewModel(productType)
        {
            PurchaseCommand = PurchaseAddOnCommand,
            RenewalText = string.Empty
        };
    }

    private async Task ResetAddOnStatesAsync()
    {
        await ExecuteUIThread(() =>
        {
            ResetAddOnItem(_aiPackAddOn);
            ResetAddOnItem(_unlimitedAccountsAddOn);
            AiPackBillingPeriodText = string.Empty;
            AiPackRenewalOrCancellationText = string.Empty;
            ApplySubtitleTexts();
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });
    }

    private void ApplyAccountUsage(int mailAccountCount, bool hasUnlimitedAccounts)
    {
        AccountUsageText = hasUnlimitedAccounts
            ? string.Format(Translator.WinoAccount_Management_AccountUsageUnlimited, mailAccountCount)
            : string.Format(Translator.WinoAccount_Management_AccountUsage, mailAccountCount, Constants.FreeAccountLimit);

        // A full meter reads as "no headroom left", which is exactly wrong once the limit is lifted.
        AccountUsagePercentage = hasUnlimitedAccounts
            ? 0
            : Math.Min(100d, mailAccountCount * 100d / Constants.FreeAccountLimit);
    }

    private void ApplyAiPackBillingTexts(AiPackBillingStatusDto? aiPack)
    {
        AiPackBillingPeriodText = aiPack?.CurrentPeriodStartUtc is DateTimeOffset periodStart &&
                                  aiPack.CurrentPeriodEndUtc is DateTimeOffset periodEnd
            ? string.Format(Translator.WinoAccount_Management_AiPackBillingPeriodValue,
                            periodStart.LocalDateTime,
                            periodEnd.LocalDateTime)
            : string.Empty;

        if (aiPack?.CancelAtPeriodEnd == true && aiPack.CurrentPeriodEndUtc is DateTimeOffset cancelsAt)
        {
            AiPackRenewalOrCancellationText = string.Format(
                Translator.WinoAccount_Management_AiPackCancels,
                cancelsAt.LocalDateTime);
        }
        else
        {
            AiPackRenewalOrCancellationText = _aiPackAddOn.RenewalText;
        }
    }

    private void ApplySubtitleTexts()
    {
        AiPackSubtitleText = _aiPackAddOn.IsPurchased
            ? string.Join(" · ",
                          Translator.WinoAccount_Management_SubscriptionLabel,
                          Translator.WinoAccount_Management_AiPackPromoPrice)
            : string.Format(Translator.WinoAccount_Management_AiPackUnownedSubtitle,
                            Translator.WinoAccount_Management_AiPackPromoPrice);

        UnlimitedAccountsSubtitleText = _unlimitedAccountsAddOn.IsPurchased
            ? Translator.WinoAccount_Management_UnlimitedOwnedSubtitle
            : string.Format(Translator.WinoAccount_Management_UnlimitedUnownedSubtitle, Constants.FreeAccountLimit);
    }

    private static void ResetAddOnItem(WinoAddOnItemViewModel addOn)
    {
        addOn.IsLoading = true;
        addOn.IsPurchased = false;
        addOn.IsPurchaseInProgress = false;
        addOn.ErrorText = string.Empty;
        addOn.RenewalText = string.Empty;
    }

    private static string BuildExportSuccessMessage(WinoAccountSyncExportResult result)
    {
        var parts = new Collection<string>();

        if (result.IncludedPreferences)
        {
            parts.Add(Translator.WinoAccount_Management_ExportPreferencesSucceeded);
        }

        if (result.IncludedAccounts)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountsSucceeded, result.ExportedMailboxCount));
        }

        if (result.ExportedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ExportAccountDataSucceeded, result.ExportedAccountDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ExportSucceeded);
        }

        return string.Join(" ", parts);
    }

    private static string BuildImportMessage(WinoAccountSyncImportResult result)
    {
        var parts = new Collection<string>();

        if (result.HadRemotePreferences)
        {
            parts.Add(result.FailedPreferenceCount > 0
                ? string.Format(Translator.WinoAccount_Management_ImportPartial, result.AppliedPreferenceCount, result.FailedPreferenceCount)
                : string.Format(Translator.WinoAccount_Management_ImportPreferencesSucceeded, result.AppliedPreferenceCount));
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountsSucceeded, result.ImportedMailboxCount));
        }

        if (result.SkippedDuplicateMailboxCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportDuplicateAccountsSkipped, result.SkippedDuplicateMailboxCount));
        }

        if (result.AppliedAccountDataCount > 0)
        {
            parts.Add(string.Format(Translator.WinoAccount_Management_ImportAccountDataSucceeded, result.AppliedAccountDataCount));
        }

        if (parts.Count == 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportEmpty);
        }

        if (result.ImportedMailboxCount > 0)
        {
            parts.Add(Translator.WinoAccount_Management_ImportReloginReminder);
        }

        return string.Join(" ", parts);
    }

    private static bool IsAccessTokenExpired(WinoAccount account)
        => string.IsNullOrWhiteSpace(account.AccessToken) || account.AccessTokenExpiresAtUtc <= DateTime.UtcNow;

    private async Task LoadAddOnsAsync(WinoAccount? account)
    {
        ApiEnvelope<BillingStatusResultDto>? response = null;
        var hasIntelligenceAccess = false;
        try
        {
            if (account != null)
            {
                response = await _billingService.GetStatusAsync().ConfigureAwait(false);
            }

            var hasUnlimitedAccounts = await _billingService.HasUnlimitedAccountsAsync().ConfigureAwait(false) ||
                                       response?.Result?.IsUnlimitedAccountsEnabled == true;
            var mailAccountCount = (await _accountService.GetAccountsAsync().ConfigureAwait(false))?.Count ?? 0;

            await ExecuteUIThread(() =>
            {
                _unlimitedAccountsAddOn.IsPurchased = hasUnlimitedAccounts;
                _unlimitedAccountsAddOn.ErrorText = string.Empty;
                _unlimitedAccountsAddOn.IsLoading = false;

                ApplyAccountUsage(mailAccountCount, hasUnlimitedAccounts);

                var aiPack = response?.Result?.AiPack;
                _aiPackAddOn.IsPurchased = aiPack?.HasAccess == true;
                hasIntelligenceAccess = _aiPackAddOn.IsPurchased;
                HasIntelligenceAccess = hasIntelligenceAccess;
                _aiPackAddOn.ErrorText = account != null && (response == null || !response.IsSuccess || response.Result == null)
                    ? Translator.WinoAccount_Management_AddOnLoadFailed
                    : string.Empty;
                _aiPackAddOn.RenewalText = aiPack?.RenewsAtUtc is DateTimeOffset renewalDateUtc
                    ? string.Format(Translator.WinoAccount_Management_AiPackRenews, renewalDateUtc.LocalDateTime)
                    : string.Empty;
                _aiPackAddOn.IsLoading = false;

                ApplyAiPackBillingTexts(aiPack);
                ApplySubtitleTexts();
                PurchaseAddOnCommand.NotifyCanExecuteChanged();
            });

            if (account is not null && response?.IsSuccess == true)
                WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());

            if (account != null)
            {
                await LoadIntelligenceDataAsync().ConfigureAwait(false);
            }
            else
            {
                await ResetIntelligenceDataAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.ErrorText = Translator.WinoAccount_Management_AddOnLoadFailed;
                _unlimitedAccountsAddOn.ErrorText = Translator.WinoAccount_Management_AddOnLoadFailed;
            });
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.IsLoading = false;
                _unlimitedAccountsAddOn.IsLoading = false;
                PurchaseAddOnCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private async Task LoadIntelligenceDataAsync()
    {
        IntelligenceConsentDto? consent = null;
        var consentError = string.Empty;
        try
        {
            consent = await _apiClient.GetIntelligenceConsentAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            consentError = WinoAccountApiErrorTranslator.Translate(exception.Message);
        }

        if (consent != null)
            await ExecuteUIThread(() => ApplyIntelligenceConsent(consent));

        ApiEnvelope<AiUsageStatusDto>? usageResponse = null;
        try
        {
            usageResponse = await _apiClient.GetAiUsageAsync().ConfigureAwait(false);
        }
        catch
        {
            // Mailbox intelligence remains useful when only the period-usage endpoint is unavailable.
        }

        var localAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        var mailboxItems = Array.Empty<IntelligenceMailboxData>();
        var mailboxError = string.Empty;
        try
        {
            var serverMailboxes = await _apiClient.GetSemanticMailboxesAsync().ConfigureAwait(false);
            mailboxItems = await Task.WhenAll(serverMailboxes.Select(mailbox =>
                CreateIntelligenceMailboxItemAsync(mailbox, localAccounts))).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            mailboxError = exception.Message is WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode or WinoAccountApiErrorTranslator.IntelligenceConsentVersionOutdatedCode
                ? string.Empty
                : WinoAccountApiErrorTranslator.Translate(exception.Message);
        }

        var localOnlyItems = localAccounts
            .Where(account => mailboxItems.All(item => item.LocalAccountId != account.Id))
            .Select(CreateLocalIntelligenceMailboxItem)
            .ToArray();
        mailboxItems = [.. mailboxItems, .. localOnlyItems];

        var usage = usageResponse?.IsSuccess == true ? usageResponse.Result : null;
        var totalStorageSize = mailboxItems.Sum(item => item.StorageSizeBytes);
        await ExecuteUIThread(() =>
        {
            IntelligenceMailboxes.Clear();
            foreach (var item in mailboxItems.OrderBy(item => item.Address, StringComparer.OrdinalIgnoreCase))
            {
                IntelligenceMailboxes.Add(item);
            }

            IsIntelligenceUsageAvailable = usage != null;
            IntelligenceUsagePercentage = usage == null ? 0 : (double)usage.UsagePercentage;
            IntelligenceUsageSummary = usage == null
                ? Translator.WinoAccount_Management_IntelligenceUsageUnavailable
                : string.Format(
                    Translator.WinoAccount_Management_IntelligenceUsageSummary,
                    usage.UsagePercentage,
                    usage.RemainingPercentage);
            IntelligenceResetText = usage?.ResetsAtUtc is DateTimeOffset resetsAtUtc
                ? string.Format(Translator.WinoAccount_Management_IntelligenceResets, resetsAtUtc.LocalDateTime)
                : string.Empty;
            IntelligenceStorageSummary = string.IsNullOrEmpty(mailboxError)
                ? string.Format(
                    Translator.WinoAccount_Management_IntelligenceStorageSummary,
                    mailboxItems.Count(item => item.HasServerIntelligence),
                    FormatStorageSize(totalStorageSize))
                : Translator.WinoAccount_Management_IntelligenceStorageUnavailable;
            IntelligenceDataError = mailboxError;
            if (consent != null)
                ApplyIntelligenceConsent(consent);
            else
            {
                IsConsentGranted = false;
                IntelligenceConsentStatusText = Translator.WinoAccount_IntelligenceConsentNotGranted;
            }
            ConsentErrorMessage = consentError;
        });
    }

    private async Task<IntelligenceMailboxData> CreateIntelligenceMailboxItemAsync(
        SemanticMailboxDto mailbox,
        IReadOnlyList<Wino.Core.Domain.Entities.Shared.MailAccount> localAccounts)
    {
        IntelligenceMailboxStatusDto? intelligenceStatus = null;
        try
        {
            intelligenceStatus = await _apiClient.GetIntelligenceStatusAsync(mailbox.MailboxId).ConfigureAwait(false);
        }
        catch
        {
            // The semantic mailbox state is still server-authoritative when intelligence consent is unavailable.
        }

        var localAccount = localAccounts.FirstOrDefault(account =>
            (int)account.ProviderType == mailbox.ProviderType &&
            string.Equals(account.Address?.Trim(), mailbox.Address?.Trim(), StringComparison.OrdinalIgnoreCase));
        var storageSize = intelligenceStatus?.StorageSizeBytes ?? mailbox.IndexState?.StorageSizeBytes ?? 0;

        return new IntelligenceMailboxData
        {
            MailboxId = mailbox.MailboxId,
            Address = mailbox.Address,
            ProviderType = (MailProviderType)mailbox.ProviderType,
            SpecialProvider = localAccount?.SpecialImapProvider ?? SpecialImapProvider.None,
            LocalAccountId = localAccount?.Id,
            Account = localAccount,
            HasServerIntelligence = storageSize > 0,
            IsEnabled = localAccount?.Preferences?.IsSemanticIndexingEnabled == true,
            CanToggle = localAccount != null &&
                        (localAccount.Preferences?.IsSemanticIndexingEnabled == true ||
                         HasIntelligenceAccess && IsConsentGranted),
            StorageSizeBytes = storageSize,
            IntelligenceSummary = string.Format(
                Translator.WinoAccount_Management_IntelligenceMailboxSummary,
                0,
                0,
                FormatStorageSize(storageSize)),
            ManageCommand = ManageIntelligenceMailboxCommand,
            DeleteCommand = DeleteIntelligenceCommand,
            ToggleEnabledCommand = ToggleIntelligenceMailboxCommand,
        };
    }

    private IntelligenceMailboxData CreateLocalIntelligenceMailboxItem(Wino.Core.Domain.Entities.Shared.MailAccount account)
        => new()
        {
            MailboxId = account.Id,
            Address = account.Address,
            ProviderType = account.ProviderType,
            SpecialProvider = account.SpecialImapProvider,
            LocalAccountId = account.Id,
            Account = account,
            HasServerIntelligence = false,
            IsEnabled = account.Preferences?.IsSemanticIndexingEnabled == true,
            CanToggle = account.Preferences?.IsSemanticIndexingEnabled == true ||
                        HasIntelligenceAccess && IsConsentGranted,
            StorageSizeBytes = 0,
            IntelligenceSummary = Translator.WinoAccount_Management_NoIntelligenceData,
            ManageCommand = ManageIntelligenceMailboxCommand,
            DeleteCommand = DeleteIntelligenceCommand,
            ToggleEnabledCommand = ToggleIntelligenceMailboxCommand,
        };

    private Task ResetIntelligenceDataAsync() => ExecuteUIThread(() =>
    {
        HasIntelligenceAccess = false;
        IsIntelligenceUsageAvailable = false;
        IntelligenceUsagePercentage = 0;
        IntelligenceUsageSummary = string.Empty;
        IntelligenceResetText = string.Empty;
        IntelligenceStorageSummary = string.Empty;
        IntelligenceConsentStatusText = string.Empty;
        IsConsentGranted = false;
        IsConsentBusy = false;
        ConsentPolicyUri = null;
        ConsentDataDeletionStatus = IntelligenceDeletionStatuses.NotRequired;
        ConsentErrorMessage = string.Empty;
        IntelligenceDataError = string.Empty;
        IntelligenceMailboxes.Clear();
    });

    private void ApplyIntelligenceConsent(IntelligenceConsentDto consent)
    {
        _intelligencePolicyVersion = consent.CurrentPolicyVersion;
        ConsentPolicyUri = Uri.TryCreate(consent.PrivacyPolicyUrl, UriKind.Absolute, out var uri) ? uri : null;
        IsConsentGranted = IsCurrentIntelligenceConsent(consent);
        ConsentDataDeletionStatus = consent.DataDeletionStatus;
        IntelligenceConsentStatusText = IsConsentGranted
            ? Translator.WinoAccount_IntelligenceConsentGranted
            : Translator.WinoAccount_IntelligenceConsentNotGranted;
    }

    private static bool IsCurrentIntelligenceConsent(IntelligenceConsentDto consent)
        => consent.Status == ConsentStatuses.Active &&
           consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;

    private static string FormatStorageSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return $"{display:0.##} {units[unit]}";
    }

    private sealed class IntelligenceMailboxData : WinoIntelligenceMailboxItemViewModel
    {
        public required long StorageSizeBytes { get; init; }
    }

}
