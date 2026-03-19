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
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel : CoreBaseViewModel,
    IRecipient<WinoAccountProfileUpdatedMessage>,
    IRecipient<WinoAccountProfileDeletedMessage>,
    IRecipient<WinoAccountAddOnPurchasedMessage>
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoAddOnService _addOnService;
    private readonly IMailDialogService _dialogService;
    private readonly INativeAppService _nativeAppService;

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
                                              IWinoAddOnService addOnService,
                                              IMailDialogService dialogService,
                                              INativeAppService nativeAppService)
    {
        _profileService = profileService;
        _addOnService = addOnService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
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

    [RelayCommand(CanExecute = nameof(CanPurchaseAddOn))]
    private async Task PurchaseAddOnAsync(WinoAddOnItemViewModel? addOn)
    {
        if (addOn == null)
        {
            return;
        }

        var account = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);
        if (account == null)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                          Translator.WinoAccount_Management_PurchaseRequiresSignIn,
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
            var checkoutSession = await _profileService.CreateCheckoutSessionAsync(addOn.ProductType).ConfigureAwait(false);

            if (!checkoutSession.IsSuccess || string.IsNullOrWhiteSpace(checkoutSession.Result?.Url))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            var isLaunched = await _nativeAppService.LaunchUriAsync(new Uri(checkoutSession.Result.Url)).ConfigureAwait(false);
            if (!isLaunched)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
            }
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
        => addOn != null && !addOn.IsPurchased && !IsCheckoutInProgress;

    [RelayCommand]
    private async Task ManageAiPackAsync()
    {
        try
        {
            var portalSession = await _profileService.CreateCustomerPortalSessionAsync().ConfigureAwait(false);
            if (!portalSession.IsSuccess || string.IsNullOrWhiteSpace(portalSession.Result?.Url))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            var isLaunched = await _nativeAppService.LaunchUriAsync(new Uri(portalSession.Result.Url)).ConfigureAwait(false);
            if (!isLaunched)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
            }
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
    }

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
        await ExecuteUIThread(() => IsBusy = true);

        try
        {
            var account = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);
            var addOns = await _addOnService.GetAvailableAddOnsAsync().ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                IsSignedIn = account != null;
                AccountEmail = account?.Email ?? string.Empty;
                AccountStatusText = account == null
                    ? string.Empty
                    : string.Format(Translator.WinoAccount_Management_StatusLabel, account.AccountStatus);
            });

            await UpdateAddOnsAsync(addOns).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_LoadFailed,
                                          InfoBarMessageType.Error);
            await ResetStateAsync().ConfigureAwait(false);
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
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
            AddOns.Clear();
            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task UpdateAddOnsAsync(IReadOnlyList<WinoAddOnInfo> addOns)
    {
        var items = addOns.Select(CreateAddOnItem).ToList();

        await ExecuteUIThread(() =>
        {
            AddOns.Clear();

            foreach (var item in items)
            {
                AddOns.Add(item);
            }

            PurchaseAddOnCommand.NotifyCanExecuteChanged();
        });
    }

    private WinoAddOnItemViewModel CreateAddOnItem(WinoAddOnInfo addOn)
    {
        var item = new WinoAddOnItemViewModel(addOn.ProductType)
        {
            IsPurchased = addOn.IsPurchased,
            PurchaseCommand = PurchaseAddOnCommand,
            ManageCommand = ManageAiPackCommand,
            UsageCount = addOn.UsageCount ?? 0,
            UsageLimit = addOn.UsageLimit is > 0 ? addOn.UsageLimit.Value : 1,
            UsagePercentage = addOn.UsagePercentage
        };

        if (addOn.RenewalDateUtc is DateTimeOffset renewalDateUtc)
        {
            item.RenewalText = string.Format(Translator.WinoAccount_Management_AiPackRenews, renewalDateUtc.LocalDateTime);
            item.UsageResetText = string.Format(Translator.WinoAccount_Management_AiPackResets, renewalDateUtc.LocalDateTime);
        }

        return item;
    }
}
