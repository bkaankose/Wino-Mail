#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Messaging.UI;

namespace Wino.Core.ViewModels;

public partial class WinoAccountManagementPageViewModel : CoreBaseViewModel,
    IRecipient<WinoAccountSignedInMessage>,
    IRecipient<WinoAccountSignedOutMessage>
{
    private const string AiPackProductId = "ai-pack-monthly";
    private const string ManageAiPackUrl = "https://example.com/wino-ai-pack/manage";

    private readonly IWinoAccountProfileService _profileService;
    private readonly IMailDialogService _dialogService;
    private readonly INativeAppService _nativeAppService;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyPropertyChangedFor(nameof(CanShowBuyAiPack))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string AccountDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountInitials { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowAiUsage))]
    [NotifyPropertyChangedFor(nameof(CanShowBuyAiPack))]
    public partial bool HasAiPack { get; set; }

    [ObservableProperty]
    public partial string AiPackStateText { get; set; } = Translator.WinoAccount_Management_AiPackInactive;

    [ObservableProperty]
    public partial string AiUsageSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiBillingPeriodSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiPackRenewalText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AiUsageCount { get; set; }

    [ObservableProperty]
    public partial int AiUsageLimit { get; set; } = 1;

    [ObservableProperty]
    public partial double AiUsagePercentage { get; set; }

    [ObservableProperty]
    public partial string AiUsageResetText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuyAiPack))]
    public partial bool IsAiPackCheckoutInProgress { get; set; }

    public bool IsSignedOut => !IsSignedIn;
    public bool CanShowAiUsage => HasAiPack;
    public bool CanShowBuyAiPack => IsSignedIn && !HasAiPack;
    public bool CanBuyAiPack => !IsAiPackCheckoutInProgress;

    public WinoAccountManagementPageViewModel(IWinoAccountProfileService profileService,
                                              IMailDialogService dialogService,
                                              INativeAppService nativeAppService)
    {
        _profileService = profileService;
        _dialogService = dialogService;
        _nativeAppService = nativeAppService;
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _ = LoadAsync();
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
    private async Task BuyAiPackAsync()
    {
        if (IsAiPackCheckoutInProgress)
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

        await ExecuteUIThread(() => IsAiPackCheckoutInProgress = true);

        try
        {
            var checkoutSession = await _profileService.CreateCheckoutSessionAsync(AiPackProductId).ConfigureAwait(false);

            if (!checkoutSession.IsSuccess || string.IsNullOrWhiteSpace(checkoutSession.Result))
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                              Translator.WinoAccount_Management_PurchaseStartFailed,
                                              InfoBarMessageType.Error);
                return;
            }

            var isLaunched = await _nativeAppService.LaunchUriAsync(new Uri(checkoutSession.Result)).ConfigureAwait(false);

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
            await ExecuteUIThread(() => IsAiPackCheckoutInProgress = false);
        }
    }

    [RelayCommand]
    private async Task ManageAiPackAsync() => await _nativeAppService.LaunchUriAsync(new Uri(ManageAiPackUrl));

    [RelayCommand]
    private Task ExportSettingsAsync() => Task.CompletedTask;

    [RelayCommand]
    private Task ImportSettingsAsync() => Task.CompletedTask;

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<WinoAccountSignedInMessage>(this);
        Messenger.Register<WinoAccountSignedOutMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<WinoAccountSignedInMessage>(this);
        Messenger.Unregister<WinoAccountSignedOutMessage>(this);
    }

    public void Receive(WinoAccountSignedInMessage message)
        => _ = LoadAsync();

    public void Receive(WinoAccountSignedOutMessage message)
        => _ = ResetSignedOutStateAsync();

    private async Task LoadAsync()
    {
        await ExecuteUIThread(() => IsBusy = true);

        try
        {
            var account = await _profileService.GetAuthenticatedAccountAsync().ConfigureAwait(false);

            if (account == null)
            {
                await ResetSignedOutStateAsync();
                return;
            }

            var currentUserResponse = await _profileService.GetCurrentUserAsync().ConfigureAwait(false);
            var aiStatusResponse = await _profileService.GetAiStatusAsync().ConfigureAwait(false);

            var resolvedUser = currentUserResponse.IsSuccess && currentUserResponse.Result != null
                ? currentUserResponse.Result
                : new AuthUserDto(account.Id, account.Email, account.AccountStatus, account.HasPassword, account.HasGoogleLogin, account.HasFacebookLogin);

            await ExecuteUIThread(() =>
            {
                IsSignedIn = true;
                AccountEmail = resolvedUser.Email;
                AccountDisplayName = ExtractDisplayName(resolvedUser.Email);
                AccountInitials = ExtractInitials(resolvedUser.Email);
                AccountStatusText = string.Format(Translator.WinoAccount_Management_StatusLabel, resolvedUser.AccountStatus);
            });

            UpdateAiPackState(aiStatusResponse.IsSuccess ? aiStatusResponse.Result : null);

            if (!currentUserResponse.IsSuccess || !aiStatusResponse.IsSuccess)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Warning,
                                              Translator.WinoAccount_Management_LoadFailed,
                                              InfoBarMessageType.Warning);
            }
        }
        catch (Exception)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error,
                                          Translator.WinoAccount_Management_LoadFailed,
                                          InfoBarMessageType.Error);
            await ResetSignedOutStateAsync();
        }
        finally
        {
            await ExecuteUIThread(() => IsBusy = false);
        }
    }

    private async Task ResetSignedOutStateAsync()
    {
        await ExecuteUIThread(() =>
        {
            IsSignedIn = false;
            AccountDisplayName = string.Empty;
            AccountInitials = string.Empty;
            AccountEmail = string.Empty;
            AccountStatusText = string.Empty;
            HasAiPack = false;
            AiPackStateText = Translator.WinoAccount_Management_AiPackInactive;
            AiUsageSummary = string.Empty;
            AiBillingPeriodSummary = string.Empty;
            AiPackRenewalText = string.Empty;
            AiUsageCount = 0;
            AiUsageLimit = 1;
            AiUsagePercentage = 0;
            AiUsageResetText = string.Empty;
            IsAiPackCheckoutInProgress = false;
        });
    }

    private void UpdateAiPackState(AiStatusResultDto? aiStatus)
    {
        var hasAiPack = aiStatus?.HasAiPack == true;
        var usageText = Translator.WinoAccount_Management_AiPackUnknownUsage;
        var billingText = string.Empty;
        var renewalText = string.Empty;
        var usageCount = 0;
        var usageLimit = 1;
        var usagePercentage = 0d;
        var resetText = string.Empty;

        if (hasAiPack && aiStatus?.Used is int used && aiStatus.MonthlyLimit is int limit && aiStatus.Remaining is int remaining)
        {
            usageText = string.Format(Translator.WinoAccount_Management_AiPackUsage, used, limit, remaining);
            usageCount = used;
            usageLimit = limit > 0 ? limit : 1;
            usagePercentage = (double)used / usageLimit * 100;
        }

        if (hasAiPack && aiStatus?.CurrentPeriodStartUtc is DateTimeOffset periodStart && aiStatus.CurrentPeriodEndUtc is DateTimeOffset periodEnd)
        {
            billingText = string.Format(Translator.WinoAccount_Management_AiPackBillingPeriod, periodStart.LocalDateTime, periodEnd.LocalDateTime);
            renewalText = string.Format(Translator.WinoAccount_Management_AiPackRenews, periodEnd.LocalDateTime);
            resetText = string.Format(Translator.WinoAccount_Management_AiPackResets, periodEnd.LocalDateTime);
        }

        _ = ExecuteUIThread(() =>
        {
            HasAiPack = hasAiPack;
            AiPackStateText = hasAiPack
                ? Translator.WinoAccount_Management_AiPackActive
                : Translator.WinoAccount_Management_AiPackInactive;
            AiUsageSummary = hasAiPack ? usageText : string.Empty;
            AiBillingPeriodSummary = hasAiPack ? billingText : string.Empty;
            AiPackRenewalText = hasAiPack ? renewalText : string.Empty;
            AiUsageCount = usageCount;
            AiUsageLimit = usageLimit;
            AiUsagePercentage = usagePercentage;
            AiUsageResetText = hasAiPack ? resetText : string.Empty;
        });
    }

    private static string ExtractDisplayName(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var atIndex = email.IndexOf('@');
        var localPart = atIndex > 0 ? email[..atIndex] : email;

        if (localPart.Length == 0)
            return string.Empty;

        return char.ToUpper(localPart[0], CultureInfo.CurrentCulture) + localPart[1..];
    }

    private static string ExtractInitials(string email)
    {
        var displayName = ExtractDisplayName(email);
        return displayName.Length > 0
            ? displayName[..1].ToUpper(CultureInfo.CurrentCulture)
            : string.Empty;
    }
}
