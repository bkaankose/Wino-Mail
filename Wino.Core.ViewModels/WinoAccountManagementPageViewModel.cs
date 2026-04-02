#nullable enable
using System;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Entities.Shared;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Api.Contracts.Common;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel : CoreBaseViewModel,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>,
    IRecipient<WinoAccountAddOnPurchasedMessage>
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly IMailDialogService _dialogService;
    private readonly IStoreManagementService _storeManagementService;
    private readonly WinoAddOnItemViewModel _aiPackAddOn;
    private readonly WinoAddOnItemViewModel _unlimitedAccountsAddOn;

    public ObservableCollection<WinoAddOnItemViewModel> AddOns { get; } = [];

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string AccountEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PurchaseAddOnCommand))]
    public partial bool IsCheckoutInProgress { get; set; }

    public bool IsSignedOut => !IsSignedIn;

    public WinoAccountManagementPageViewModel(IWinoAccountProfileService profileService,
                                              IMailDialogService dialogService,
                                              IStoreManagementService storeManagementService)
    {
        _profileService = profileService;
        _dialogService = dialogService;
        _storeManagementService = storeManagementService;

        _aiPackAddOn = CreateAddOnItem(WinoAddOnProductType.AI_PACK);
        _unlimitedAccountsAddOn = CreateAddOnItem(WinoAddOnProductType.UNLIMITED_ACCOUNTS);
        AddOns.Add(_aiPackAddOn);
        AddOns.Add(_unlimitedAccountsAddOn);
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = InitializeAsync();
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

        await ExecuteUIThread(() =>
        {
            IsCheckoutInProgress = true;
            addOn.IsPurchaseInProgress = true;
        });

        try
        {
            var purchaseResult = await _storeManagementService.PurchaseAsync(addOn.ProductType);

            if (purchaseResult == StorePurchaseResult.NotPurchased)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            var syncResult = await _profileService.SyncStoreEntitlementsAsync().ConfigureAwait(false);
            if (!syncResult.IsSuccess && !string.Equals(syncResult.ErrorCode, "MissingAccessToken", StringComparison.Ordinal))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              TranslateStoreSyncError(syncResult.ErrorCode),
                                              InfoBarMessageType.Error);
                return;
            }

            await HandleAddOnPurchasedAsync().ConfigureAwait(false);
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

    [RelayCommand]
    private Task ExportSettingsAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task ImportSettingsAsync() => Task.CompletedTask;

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Register<WinoAccountProfileDeletedMessage>(this);
        Messenger.Register<WinoAccountAddOnPurchasedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<WinoAccountProfileUpdatedMessage>(this);
        Messenger.Unregister<WinoAccountProfileDeletedMessage>(this);
        Messenger.Unregister<WinoAccountAddOnPurchasedMessage>(this);
    }

    public void Receive(WinoAccountProfileUpdatedMessage message)
        => _ = LoadAsync();

    public void Receive(WinoAccountProfileDeletedMessage message)
        => _ = LoadAsync();

    public void Receive(WinoAccountAddOnPurchasedMessage message)
        => _ = HandleAddOnPurchasedAsync();

    private async Task InitializeAsync()
    {
        await LoadAsync().ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        WinoAccount? cachedAccount = null;

        try
        {
            cachedAccount = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);

            if (cachedAccount != null)
            {
                await ApplyAccountStateAsync(cachedAccount).ConfigureAwait(false);
            }

            await ExecuteUIThread(() => IsBusy = true);
            await ResetAddOnStatesAsync().ConfigureAwait(false);
            var loadAiPackTask = LoadAiPackAddOnAsync();
            var loadUnlimitedAccountsTask = LoadUnlimitedAccountsAddOnAsync();

            var resolvedAccount = cachedAccount;

            if (cachedAccount == null || IsAccessTokenExpired(cachedAccount))
            {
                try
                {
                    var account = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);
                    if (account != null)
                    {
                        resolvedAccount = account;

                        var refreshedProfileResult = await _profileService.RefreshProfileAsync().ConfigureAwait(false);
                        if (refreshedProfileResult.IsSuccess && refreshedProfileResult.Account != null)
                        {
                            resolvedAccount = refreshedProfileResult.Account;
                        }
                    }
                }
                catch (Exception)
                {
                    resolvedAccount ??= cachedAccount;
                }
            }

            await ApplyAccountStateAsync(resolvedAccount).ConfigureAwait(false);
            await Task.WhenAll(loadAiPackTask, loadUnlimitedAccountsTask).ConfigureAwait(false);
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
        }
    }

    private async Task ApplyAccountStateAsync(Wino.Core.Domain.Entities.Shared.WinoAccount? account)
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = account != null;
            AccountEmail = account?.Email ?? string.Empty;
            AccountStatusText = account == null
                ? string.Empty
                : string.Format(Translator.WinoAccount_Management_StatusLabel, account.AccountStatus);
        });
    }

    private async Task HandleAddOnPurchasedAsync()
    {
        await LoadAsync().ConfigureAwait(false);

        _dialogService.InfoBarMessage(Translator.Info_PurchaseThankYouTitle,
                                      Translator.Info_PurchaseThankYouMessage,
                                      InfoBarMessageType.Success);
    }

    private async Task ResetStateAsync()
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = false;
            AccountEmail = string.Empty;
            AccountStatusText = string.Empty;
            IsCheckoutInProgress = false;
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });

        await ResetAddOnStatesAsync().ConfigureAwait(false);
    }

    private WinoAddOnItemViewModel CreateAddOnItem(WinoAddOnProductType productType)
    {
        return new WinoAddOnItemViewModel(productType)
        {
            PurchaseCommand = PurchaseAddOnCommand,
            UsageLimit = 1
        };
    }

    private async Task ResetAddOnStatesAsync()
    {
        await ExecuteUIThread(() =>
        {
            ResetAddOnItem(_aiPackAddOn);
            ResetAddOnItem(_unlimitedAccountsAddOn);
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });
    }

    private static void ResetAddOnItem(WinoAddOnItemViewModel addOn)
    {
        addOn.IsLoading = true;
        addOn.IsPurchased = false;
        addOn.IsPurchaseInProgress = false;
        addOn.HasUsageData = false;
        addOn.ErrorText = string.Empty;
        addOn.UsageCount = 0;
        addOn.UsageLimit = 1;
        addOn.UsagePercentage = 0;
        addOn.RenewalText = string.Empty;
        addOn.UsageResetText = string.Empty;
    }

    private static string TranslateStoreSyncError(string? errorCode)
        => errorCode switch
        {
            _ => Translator.WinoAccount_Management_StoreSyncFailed
        };

    private static bool IsAccessTokenExpired(WinoAccount account)
        => string.IsNullOrWhiteSpace(account.AccessToken) || account.AccessTokenExpiresAtUtc <= DateTime.UtcNow;

    private async Task LoadUnlimitedAccountsAddOnAsync()
    {
        try
        {
            var hasUnlimitedAccounts = await _storeManagementService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                _unlimitedAccountsAddOn.IsPurchased = hasUnlimitedAccounts;
                _unlimitedAccountsAddOn.ErrorText = string.Empty;
            });
        }
        catch (Exception)
        {
            await ExecuteUIThread(() =>
            {
                _unlimitedAccountsAddOn.ErrorText = Translator.WinoAccount_Management_AddOnLoadFailed;
            });
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                _unlimitedAccountsAddOn.IsLoading = false;
                PurchaseAddOnCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private async Task LoadAiPackAddOnAsync()
    {
        try
        {
            var hasAiPack = await _storeManagementService.HasProductAsync(WinoAddOnProductType.AI_PACK).ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.IsPurchased = hasAiPack;
                _aiPackAddOn.ErrorText = string.Empty;
            });

            if (!hasAiPack)
            {
                return;
            }

            var aiStatusResponse = await _profileService.GetAiStatusAsync().ConfigureAwait(false);
            if (!aiStatusResponse.IsSuccess || aiStatusResponse.Result == null)
            {
                await ExecuteUIThread(() =>
                {
                    _aiPackAddOn.HasUsageData = false;
                    _aiPackAddOn.ErrorText = Translator.WinoAccount_Management_AiPackUsageLoadFailed;
                });
                return;
            }

            var aiStatus = aiStatusResponse.Result;
            if (aiStatus.MonthlyLimit is not int usageLimit || usageLimit <= 0 || aiStatus.Used is not int usageCount)
            {
                await ExecuteUIThread(() =>
                {
                    _aiPackAddOn.HasUsageData = false;
                    _aiPackAddOn.ErrorText = Translator.WinoAccount_Management_AiPackUsageLoadFailed;
                });
                return;
            }

            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.HasUsageData = true;
                _aiPackAddOn.ErrorText = string.Empty;
                _aiPackAddOn.UsageCount = usageCount;
                _aiPackAddOn.UsageLimit = usageLimit;
                _aiPackAddOn.UsagePercentage = usageLimit > 0 ? (double)usageCount / usageLimit * 100 : 0;
                _aiPackAddOn.RenewalText = aiStatus.CurrentPeriodEndUtc is DateTimeOffset renewalDateUtc
                    ? string.Format(Translator.WinoAccount_Management_AiPackRenews, renewalDateUtc.LocalDateTime)
                    : string.Empty;
                _aiPackAddOn.UsageResetText = aiStatus.CurrentPeriodEndUtc is DateTimeOffset resetDateUtc
                    ? string.Format(Translator.WinoAccount_Management_AiPackResets, resetDateUtc.LocalDateTime)
                    : string.Empty;
            });
        }
        catch (Exception)
        {
            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.HasUsageData = false;
                _aiPackAddOn.ErrorText = Translator.WinoAccount_Management_AddOnLoadFailed;
            });
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                _aiPackAddOn.IsLoading = false;
                PurchaseAddOnCommand.NotifyCanExecuteChanged();
            });
        }
    }
}
