#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>
/// The mail synchronization period of one account: only longer periods are offered, and Apply downloads the older
/// mail. The owning page forwards its account and registers this object's recipients with its own.
/// </summary>
public sealed partial class MailSynchronizationRangeSettingViewModel : ObservableObject,
    IRecipient<AccountSynchronizationProgressUpdatedMessage>,
    IRecipient<AccountSynchronizationCompleted>,
    IRecipient<MailSynchronizationRangeChangeStateChanged>
{
    private readonly IMessenger _messenger;
    private readonly IMailSynchronizationRangeService _rangeService;
    private readonly Func<Action, Task> _runOnUIThread;
    private Guid? _accountId;
    private InitialSynchronizationRange? _listedFor;

    public MailSynchronizationRangeSettingViewModel(IMessenger messenger, IMailSynchronizationRangeService rangeService, Func<Action, Task> runOnUIThread)
    {
        _messenger = messenger;
        _rangeService = rangeService;
        _runOnUIThread = runOnUIThread;
    }

    public ObservableCollection<InitialSynchronizationRangeOption> Ranges { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial InitialSynchronizationRangeOption? SelectedRange { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial double ProgressPercentage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    public partial string StatusText { get; set; } = string.Empty;

    public bool IsStatusVisible => !string.IsNullOrEmpty(StatusText);

    public void RegisterRecipients()
    {
        _messenger.Register<AccountSynchronizationProgressUpdatedMessage>(this);
        _messenger.Register<AccountSynchronizationCompleted>(this);
        _messenger.Register<MailSynchronizationRangeChangeStateChanged>(this);
    }

    public void UnregisterRecipients()
    {
        _messenger.Unregister<AccountSynchronizationProgressUpdatedMessage>(this);
        _messenger.Unregister<AccountSynchronizationCompleted>(this);
        _messenger.Unregister<MailSynchronizationRangeChangeStateChanged>(this);
    }

    public Task LoadAsync(MailAccount? account)
    {
        _accountId = account?.Id;
        return RefreshAsync();
    }

    public void Receive(AccountSynchronizationProgressUpdatedMessage message)
    {
        var progress = message.Progress;
        if (progress.AccountId != _accountId || progress.Category != SynchronizationProgressCategory.Mail || !IsRunning)
            return;

        _ = _runOnUIThread(() =>
        {
            IsProgressIndeterminate = progress.IsIndeterminate || progress.TotalUnits == 0;
            ProgressPercentage = progress.ProgressPercentage;
        });
    }

    public void Receive(AccountSynchronizationCompleted message)
    {
        if (message.AccountId == _accountId && !IsEnabled)
            _ = RefreshAsync();
    }

    public void Receive(MailSynchronizationRangeChangeStateChanged message)
    {
        if (message.AccountId == _accountId)
            _ = RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_accountId is not { } accountId || SelectedRange is not { } option)
            return;

        IsEnabled = false;
        IsRunning = true;
        StatusText = Translator.AccountMail_SyncRange_Running;

        try
        {
            await _rangeService.ChangeRangeAsync(accountId, option.Range).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private bool CanApply() => IsEnabled && SelectedRange != null;

    private async Task RefreshAsync()
    {
        var accountId = _accountId;
        MailSynchronizationRangeStatus status;

        try
        {
            status = accountId is { } id
                ? await _rangeService.GetStatusAsync(id).ConfigureAwait(false)
                : MailSynchronizationRangeStatus.Unsupported;
        }
        catch (Exception)
        {
            status = MailSynchronizationRangeStatus.Unsupported;
        }

        await _runOnUIThread(() =>
        {
            if (accountId == _accountId)
                Apply(status);
        }).ConfigureAwait(false);
    }

    private void Apply(MailSynchronizationRangeStatus status)
    {
        if (_listedFor != status.CurrentRange)
        {
            _listedFor = status.CurrentRange;
            Ranges.Clear();

            foreach (var option in CreateOptions().Where(option => option.Range.IsWiderThan(status.CurrentRange)))
                Ranges.Add(option);

            SelectedRange = Ranges.FirstOrDefault();
        }

        IsVisible = status.IsSupported && Ranges.Count > 0;
        IsRunning = status.IsChangeRunning;
        IsEnabled = status.IsSupported && status.IsInitialSynchronizationCompleted && !status.IsChangeRunning;
        StatusText = status.IsChangeRunning
            ? Translator.AccountMail_SyncRange_Running
            : status.IsSupported && !status.IsInitialSynchronizationCompleted
                ? Translator.AccountMail_SyncRange_WaitForInitialSync
                : string.Empty;

        if (!status.IsChangeRunning)
        {
            IsProgressIndeterminate = true;
            ProgressPercentage = 0;
        }
    }

    private static InitialSynchronizationRangeOption[] CreateOptions() =>
    [
        new(InitialSynchronizationRange.ThreeMonths, Translator.AccountSetup_Range_3Months),
        new(InitialSynchronizationRange.SixMonths, Translator.AccountSetup_Range_6Months),
        new(InitialSynchronizationRange.NineMonths, Translator.AccountSetup_Range_9Months),
        new(InitialSynchronizationRange.OneYear, Translator.AccountSetup_Range_Year),
        new(InitialSynchronizationRange.Everything, Translator.AccountSetup_Range_Everything)
    ];
}
