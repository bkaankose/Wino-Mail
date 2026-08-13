using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Extensions;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class StoragePageViewModel(
    IAccountService accountService,
    IMimeStorageService mimeStorageService,
    IMailDialogService dialogService) : MailBaseViewModel
{
    private readonly ILogger _logger = Log.ForContext<StoragePageViewModel>();
    private readonly IAccountService _accountService = accountService;
    private readonly IMimeStorageService _mimeStorageService = mimeStorageService;
    private readonly IMailDialogService _dialogService = dialogService;

    public ObservableCollection<AccountStorageItemViewModel> AccountStorageItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsCleaning { get; set; }

    [ObservableProperty]
    public partial string MimeRootPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "";

    public bool IsBusy => IsLoading || IsCleaning;

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        await ExecuteUIThread(() => { SummaryText = Translator.SettingsStorage_NoLocalMimeDataFound; });

        await RefreshStorageAsync();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        UpdateAccountBusyState();
    }

    partial void OnIsCleaningChanged(bool value)
    {
        UpdateAccountBusyState();
    }

    private void UpdateAccountBusyState()
    {
        Dispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var item in AccountStorageItems)
            {
                item.IsBusy = IsBusy;
            }
        });
    }

    [RelayCommand]
    private async Task RefreshStorageAsync()
    {
        if (IsBusy) return;

        await ExecuteUIThread(() => { IsLoading = true; });

        try
        {
            var mimeRootPath = await _mimeStorageService.GetMimeRootPathAsync().ConfigureAwait(false);
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var sizeMap = await _mimeStorageService.GetAccountsMimeStorageSizesAsync(accounts.Select(a => a.Id)).ConfigureAwait(false);

            var storageItems = accounts
                .Select(account =>
                {
                    sizeMap.TryGetValue(account.Id, out var accountSize);
                    var viewModel = new AccountStorageItemViewModel(account, accountSize, DeleteAllCommand, DeleteOlderThanOneMonthCommand, DeleteOlderThanThreeMonthsCommand, DeleteOlderThanSixMonthsCommand, DeleteOlderThanOneYearCommand);
                    viewModel.SizeDescription = string.Format(Translator.SettingsStorage_AccountUsageDescription, viewModel.SizeText);
                    return viewModel;
                })
                .OrderByDescending(a => a.SizeBytes)
                .ToList();

            await ExecuteUIThread(() =>
            {
                MimeRootPath = mimeRootPath;
                AccountStorageItems.Clear();

                foreach (var item in storageItems)
                {
                    AccountStorageItems.Add(item);
                }

                var total = storageItems.Sum(a => a.SizeBytes);
                SummaryText = storageItems.Count == 0
                    ? Translator.SettingsStorage_NoAccountsFound
                    : string.Format(Translator.SettingsStorage_TotalUsage, total.GetBytesReadable());
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to refresh storage data.");
            await ExecuteUIThread(() =>
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, Core.Domain.Enums.InfoBarMessageType.Error);
            });
        }
        finally
        {
            await ExecuteUIThread(() => { IsLoading = false; });
        }
    }

    [RelayCommand]
    private async Task DeleteAllAsync(AccountStorageItemViewModel accountItem)
    {
        if (accountItem == null || IsBusy) return;

        bool approved = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.SettingsStorage_DeleteAll_Confirm_Message, accountItem.AccountName),
            Translator.SettingsStorage_DeleteAll_Confirm_Title,
            Translator.Buttons_Delete);

        if (!approved) return;

        await ExecuteUIThread(() => { IsCleaning = true; });

        try
        {
            await _mimeStorageService.DeleteAccountMimeStorageAsync(accountItem.Account.Id).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsStorage_DeleteAll_Success, Core.Domain.Enums.InfoBarMessageType.Success);
            });
            await RefreshStorageAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete all MIME content for account {AccountId}", accountItem.Account.Id);
            await ExecuteUIThread(() =>
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, Core.Domain.Enums.InfoBarMessageType.Error);
            });
        }
        finally
        {
            await ExecuteUIThread(() => { IsCleaning = false; });
        }
    }

    [RelayCommand]
    private Task DeleteOlderThanOneMonthAsync(AccountStorageItemViewModel accountItem)
        => DeleteOlderThanAsync(accountItem, 1);

    [RelayCommand]
    private Task DeleteOlderThanThreeMonthsAsync(AccountStorageItemViewModel accountItem)
        => DeleteOlderThanAsync(accountItem, 3);

    [RelayCommand]
    private Task DeleteOlderThanSixMonthsAsync(AccountStorageItemViewModel accountItem)
        => DeleteOlderThanAsync(accountItem, 6);

    [RelayCommand]
    private Task DeleteOlderThanOneYearAsync(AccountStorageItemViewModel accountItem)
        => DeleteOlderThanAsync(accountItem, 12);

    private async Task DeleteOlderThanAsync(AccountStorageItemViewModel accountItem, int months)
    {
        if (accountItem == null || IsBusy) return;

        string rangeText = GetRangeText(months);

        bool approved = await _dialogService.ShowConfirmationDialogAsync(
            string.Format(Translator.SettingsStorage_DeleteOld_Confirm_Message, rangeText, accountItem.AccountName),
            Translator.SettingsStorage_DeleteOld_Confirm_Title,
            Translator.Buttons_Delete);

        if (!approved) return;

        await ExecuteUIThread(() => { IsCleaning = true; });

        try
        {
            var cutoffDateUtc = DateTime.UtcNow.AddMonths(-months);
            var deletedDirectoryCount = await _mimeStorageService
                .DeleteAccountMimeStorageOlderThanAsync(accountItem.Account.Id, cutoffDateUtc)
                .ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                _dialogService.InfoBarMessage(
                    Translator.GeneralTitle_Info,
                    string.Format(Translator.SettingsStorage_DeleteOld_Success, deletedDirectoryCount, rangeText),
                    Core.Domain.Enums.InfoBarMessageType.Success);
            });

            await RefreshStorageAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete MIME content by cutoff for account {AccountId}", accountItem.Account.Id);
            await ExecuteUIThread(() =>
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, Core.Domain.Enums.InfoBarMessageType.Error);
            });
        }
        finally
        {
            await ExecuteUIThread(() => { IsCleaning = false; });
        }
    }

    private static string GetRangeText(int months)
    {
        return months switch
        {
            1 => Translator.SettingsStorage_1Month,
            3 => Translator.SettingsStorage_3Months,
            6 => Translator.SettingsStorage_6Months,
            12 => Translator.SettingsStorage_1Year,
            _ => string.Format(Translator.SettingsStorage_Months, months)
        };
    }
}
