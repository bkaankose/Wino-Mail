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
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class WinoIntelligenceManagementPageViewModel : MailBaseViewModel, IRecipient<SemanticIndexJobChanged>
{
    private const int LargeMailboxMessageThreshold = 2_000;

    /// <summary>
    /// Number of columns in the message volume histogram. A day per column is far
    /// more detail than the control can draw, so days are folded into fixed buckets.
    /// </summary>
    private const int RangeHistogramBucketCount = 72;

    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly ISemanticIndexCoordinator _coordinator;
    private readonly IIntelligenceMessageContextResolver _messageContextResolver;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ILocalIntelligenceStore _localStore;
    private readonly ITranslationService _translationService;
    private readonly IWinoAccountProfileService? _profileService;
    private readonly IWinoAccountIntelligenceSnapshotService? _snapshotService;
    private SemanticIndexPlan _currentPlan;
    private bool _isApplyingProfile;
    private SemanticIndexAvailableRange? _availableRange;
    private IntelligenceMailboxStatusDto? _intelligenceStatus;
    private CancellationTokenSource _rangeRecalculationCancellation;
    public WinoIntelligenceManagementPageViewModel(
        IMailDialogService dialogService,
        IAccountService accountService,
        ISemanticIndexCoordinator semanticIndexCoordinator,
        IIntelligenceMessageContextResolver messageContextResolver,
        IWinoAccountApiClient apiClient,
        ILocalIntelligenceStore localStore,
        ITranslationService translationService,
        IWinoAccountProfileService? profileService = null,
        IWinoAccountIntelligenceSnapshotService? snapshotService = null)
    {
        _dialogService = dialogService;
        _accountService = accountService;
        _coordinator = semanticIndexCoordinator;
        _messageContextResolver = messageContextResolver;
        _apiClient = apiClient;
        _localStore = localStore;
        _translationService = translationService;
        _profileService = profileService;
        _snapshotService = snapshotService;
    }

    [ObservableProperty]
    public partial MailAccount Account { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountAddress { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyPropertyChangedFor(nameof(IsEnabledContentVisible))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    public partial bool IsPageReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabledContentVisible))]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    public partial bool IsSemanticIndexingEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    public partial bool IsCalculatingPlan { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelIndexingCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditMessageRange))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(PlanCardTitle))]
    [NotifyPropertyChangedFor(nameof(PlanCardDescription))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    public partial bool IsJobActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndexingInProgress))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarTitle))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarMessage))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarType))]
    public partial SemanticIndexJobStatus JobStatus { get; set; } = SemanticIndexJobStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEverythingSelected))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    public partial double RangeMaximum { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEverythingSelected))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    public partial double SelectedRangeStartOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEverythingSelected))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    public partial double SelectedRangeEndOffset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEverythingSelected))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    [NotifyPropertyChangedFor(nameof(CanEditMessageRange))]
    public partial bool HasAvailableMessages { get; set; }

    [ObservableProperty]
    public partial string SelectedRangeSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OldestAvailableDateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewestAvailableDateText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    public partial int SelectedRangeMessageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    public partial int EstimatedMissingMessageCount { get; set; }

    [ObservableProperty]
    public partial bool AutomaticallyIndexNewMessages { get; set; } = true;

    [ObservableProperty]
    public partial int NewMessageModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanCardDescription))]
    public partial string PlanSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarType))]
    public partial InfoBarMessageType StatusType { get; set; } = InfoBarMessageType.Information;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsConsentActionVisible { get; set; }

    [ObservableProperty]
    public partial bool HasAccountConsent { get; set; }

    [ObservableProperty]
    public partial Guid? SemanticMailboxId { get; set; }

    [ObservableProperty]
    public partial string StartButtonText { get; set; } = Translator.SemanticIndex_StartButton;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    public partial bool HasIndexData { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    public partial bool IsUpgradeRecommended { get; set; }

    private string _recommendedProfileId = string.Empty;

    [ObservableProperty]
    public partial string CoverageDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRemoteRefreshInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteRefreshError))]
    public partial string RemoteRefreshError { get; set; } = string.Empty;

    public bool HasRemoteRefreshError => !string.IsNullOrWhiteSpace(RemoteRefreshError);

    [ObservableProperty]
    public partial string HeadlineLanguageDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeadlineLanguageMismatchMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    public partial bool IsHeadlineLanguageMismatchVisible { get; set; }

    [ObservableProperty]
    public partial bool DontAskHeadlineLanguageAgain { get; set; }

    [ObservableProperty]
    public partial int ProgressMaximum { get; set; } = 1;

    [ObservableProperty]
    public partial int ProgressValue { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanCardDescription))]
    public partial string ProgressSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MetadataProgressValue { get; set; }

    [ObservableProperty]
    public partial string MetadataProgressText { get; set; } = string.Empty;

    #region Hero summary

    [ObservableProperty]
    public partial string HeroStateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarMessageType HeroStateType { get; set; } = InfoBarMessageType.Information;

    /// <summary>
    /// Shown instead of the state dot while an operation, a plan calculation or a job is running.
    /// </summary>
    [ObservableProperty]
    public partial bool IsHeroProgressVisible { get; set; }

    [ObservableProperty]
    public partial int IndexedMessageCount { get; set; }

    /// <summary>
    /// The only coverage fact the hero carries. Everything else about coverage lives
    /// in the data management card, so the hero does not restate it.
    /// </summary>
    [ObservableProperty]
    public partial string IndexedMessageCountDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsQuotaAvailable { get; set; }

    [ObservableProperty]
    public partial double QuotaUsagePercentage { get; set; }

    [ObservableProperty]
    public partial string QuotaSummary { get; set; } = Translator.Intelligence_QuotaUnavailable;

    #endregion

    /// <summary>
    /// Message volume of the retrieved mail, folded into fixed buckets and painted
    /// behind the range selector so the selection is made against real mail volume.
    /// </summary>
    public ObservableCollection<SemanticIndexRangeBucketViewModel> RangeBuckets { get; } = [];

    [ObservableProperty]
    public partial SemanticIndexRangePreset SelectedRangePreset { get; set; } = SemanticIndexRangePreset.Everything;

    /// <summary>
    /// The indexing card states its title and its one-line summary once, in the card
    /// header, whether it is describing a plan or a running job.
    /// </summary>
    public string PlanCardTitle => IsJobActive
        ? Translator.SemanticIndex_ProgressTitle
        : Translator.SemanticIndex_IndexMissing;

    public string PlanCardDescription => IsJobActive ? ProgressSummary : PlanSummary;

    public bool IsEnabledContentVisible => IsPageReady && IsSemanticIndexingEnabled;
    public bool CanChangeSemanticIndexingState => IsPageReady && !IsBusy && !IsJobActive;
    public bool CanEditMessageRange => HasAvailableMessages && !IsJobActive;

    /// <summary>
    /// The hero already carries the busy message and the healthy state, so the status
    /// bar is only raised for what the hero cannot say on its own.
    /// </summary>
    public bool IsIndexingInProgress => JobStatus is SemanticIndexJobStatus.Queued or SemanticIndexJobStatus.Indexing;
    public string StatusInfoBarTitle => IsIndexingInProgress ? Translator.SemanticIndex_IndexingInfoBarTitle : string.Empty;
    public string StatusInfoBarMessage => IsIndexingInProgress ? Translator.SemanticIndex_IndexingInfoBarMessage : StatusMessage;
    public InfoBarMessageType StatusInfoBarType => IsIndexingInProgress ? InfoBarMessageType.Information : StatusType;
    public bool IsStatusInfoBarVisible => IsPageReady && !IsBusy &&
        (IsIndexingInProgress ||
         (!string.IsNullOrWhiteSpace(StatusMessage) && StatusType != InfoBarMessageType.Success));
    public bool IsEverythingSelected => HasAvailableMessages &&
        SelectedRangeStartOffset < 0.5 &&
        Math.Abs(SelectedRangeEndOffset - RangeMaximum) < 0.5;

    /// <summary>
    /// The warning tells the user that "Everything" is a poor default, so it is only
    /// raised for a whole-mailbox selection that is also large enough to exhaust the quota.
    /// </summary>
    public bool ShouldShowEverythingWarning => IsSemanticIndexingEnabled &&
        IsEverythingSelected &&
        SelectedRangeMessageCount > LargeMailboxMessageThreshold &&
        EstimatedMissingMessageCount > 0;
    partial void OnSelectedRangeStartOffsetChanged(double value)
    {
        UpdateSelectedRangeSummary();
        if (!_isApplyingProfile && IsPageReady && IsSemanticIndexingEnabled)
            SchedulePlanRecalculation();
    }

    partial void OnSelectedRangeEndOffsetChanged(double value)
    {
        UpdateSelectedRangeSummary();
        if (!_isApplyingProfile && IsPageReady && IsSemanticIndexingEnabled)
            SchedulePlanRecalculation();
    }

    partial void OnAutomaticallyIndexNewMessagesChanged(bool value)
    {
        if (!_isApplyingProfile && IsPageReady && IsSemanticIndexingEnabled)
            _ = RecalculatePlanAsync();
    }

    partial void OnNewMessageModeIndexChanged(int value)
    {
        AutomaticallyIndexNewMessages = value == 0;
    }

    partial void OnIsPageReadyChanged(bool value) => RefreshHeroState();
    partial void OnIsBusyChanged(bool value) => RefreshHeroState();
    partial void OnIsJobActiveChanged(bool value) => RefreshHeroState();
    partial void OnIsCalculatingPlanChanged(bool value) => RefreshHeroState();
    partial void OnIsSemanticIndexingEnabledChanged(bool value) => RefreshHeroState();
    partial void OnHasAccountConsentChanged(bool value) => RefreshHeroState();
    partial void OnHasIndexDataChanged(bool value) => RefreshHeroState();
    partial void OnIsUpgradeRecommendedChanged(bool value) => RefreshHeroState();
    partial void OnHasErrorChanged(bool value) => RefreshHeroState();
    partial void OnEstimatedMissingMessageCountChanged(int value) => RefreshHeroState();

    /// <summary>
    /// Applies one of the fixed range presets to the day-offset selection. "Everything"
    /// spans the whole retrieved range, every other preset ends at the newest message.
    /// </summary>
    [RelayCommand]
    private void ApplyRangePreset(string presetId)
    {
        if (_availableRange is null)
            return;

        var days = SemanticIndexRangeSelectionResolver.GetPresetDays(
            SemanticIndexRangePresetExtensions.FromStableId(presetId),
            _availableRange);

        SelectedRangeEndOffset = RangeMaximum;
        SelectedRangeStartOffset = Math.Max(0, _availableRange.DaySpan - days);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        if (parameters is not Guid accountId)
            return;

        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationLoading);
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false)
                ?? throw new InvalidOperationException(Translator.Exception_NullAssignedAccount);
            await ExecuteUIThread(() =>
            {
                Account = account;
                AccountName = account.Name;
                AccountAddress = account.Address;
                IsSemanticIndexingEnabled = account.Preferences.IsSemanticIndexingEnabled;
            });
            // The available range is a local mail query. Loading it before the consent
            // check keeps the range editor usable the moment consent is granted.
            await EnsureAvailableRangeAsync(refreshRemote: false).ConfigureAwait(false);

            if (await TryApplyCachedAccountSnapshotAsync(account).ConfigureAwait(false))
            {
                await ExecuteUIThread(() => IsPageReady = true);
                _ = RefreshCachedAccountSnapshotAsync(account);
                await RefreshIndexedMessageCountAsync().ConfigureAwait(false);
                if (IsSemanticIndexingEnabled)
                    await RecalculatePlanAsync().ConfigureAwait(false);
                return;
            }

            var hasAccountConsent = await LoadAccountConsentAsync().ConfigureAwait(false);
            if (!hasAccountConsent)
            {
                await _coordinator.DeleteLocalIndexAsync(account.Id).ConfigureAwait(false);
                account.Preferences.IsSemanticIndexingEnabled = false;
                await _accountService.UpdateAccountAsync(account).ConfigureAwait(false);
                await ExecuteUIThread(() =>
                {
                    IsSemanticIndexingEnabled = false;
                    ApplyErrorStatus(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode);
                });
                return;
            }
            var status = SemanticMailboxId is { } mailboxId
                ? await _apiClient.GetIntelligenceStatusAsync(mailboxId).ConfigureAwait(false)
                : null;
            await ExecuteUIThread(() => ApplyStatus(status));
            await EnsureAvailableRangeAsync().ConfigureAwait(false);
            await RefreshHeadlineLanguageAsync(status).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await ExecuteUIThread(() => IsPageReady = true);
            await SetBusyAsync(false);
        }

        await RefreshQuotaAsync().ConfigureAwait(false);
        await RefreshIndexedMessageCountAsync().ConfigureAwait(false);

        if (IsSemanticIndexingEnabled)
            await RecalculatePlanAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetSemanticIndexingEnabledAsync(bool isEnabled)
    {
        if (!IsPageReady || Account is null || isEnabled == IsSemanticIndexingEnabled)
            return IsSemanticIndexingEnabled;
        if (isEnabled && !HasAccountConsent)
        {
            await ExecuteUIThread(() => ApplyErrorStatus(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode));
            return false;
        }

        var previous = IsSemanticIndexingEnabled;
        try
        {
            await SetBusyAsync(true, isEnabled ? Translator.SemanticIndex_OperationEnabling : Translator.SemanticIndex_OperationDisabling);

            if (isEnabled)
                await _coordinator.EnsureMailboxAsync(Account.Id).ConfigureAwait(false);
            else
                await _coordinator.DeleteIndexAsync(Account.Id).ConfigureAwait(false);

            Account.Preferences.IsSemanticIndexingEnabled = isEnabled;
            await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
            await ExecuteUIThread(() => IsSemanticIndexingEnabled = isEnabled);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        }
        catch (Exception exception)
        {
            Account.Preferences.IsSemanticIndexingEnabled = previous;

            await ExecuteUIThread(() => IsSemanticIndexingEnabled = previous);
            await ShowErrorAsync(exception);

            return previous;
        }
        finally
        {
            await SetBusyAsync(false);
        }

        if (isEnabled)
        {
            // Enabling can be the first point at which the range is reachable, so the
            // editor gets its data before the plan is calculated against it.
            await RefreshIntelligenceStatusAsync().ConfigureAwait(false);
            await EnsureAvailableRangeAsync().ConfigureAwait(false);
            await RefreshIndexedMessageCountAsync().ConfigureAwait(false);
            await RecalculatePlanAsync().ConfigureAwait(false);
        }
        else
            await ExecuteUIThread(() =>
            {
                _intelligenceStatus = null;
                EstimatedMissingMessageCount = SelectedRangeMessageCount;
                StatusMessage = Translator.SemanticIndex_DisabledCallout;
                StatusType = InfoBarMessageType.Information;
                RefreshHeroState();
            });
        return isEnabled;
    }

    [RelayCommand]
    private void OpenIntelligenceSettings()
        => Messenger.Send(new SettingsRootNavigationRequested(WinoPage.WinoIntelligencePage));

    [RelayCommand(CanExecute = nameof(CanStartIndexing))]
    private async Task StartIndexingAsync()
    {
        try
        {
            var plan = _currentPlan ?? await CalculatePlanAsync().ConfigureAwait(false);
            await _coordinator.StartIndexingAsync(Account.Id, plan, notifyWhenCompleted: true).ConfigureAwait(false);
            await ExecuteUIThread(() => ApplySnapshot(_coordinator.GetJobSnapshot(Account.Id)));
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    private async Task<bool> TryApplyCachedAccountSnapshotAsync(MailAccount account)
    {
        if (_profileService is null || _snapshotService is null) return false;
        var winoAccount = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (winoAccount is null) return false;
        var snapshot = await _snapshotService.GetCachedAsync(winoAccount.Id).ConfigureAwait(false);
        if (snapshot?.HasData != true) return false;
        var mailbox = snapshot.Mailboxes.FirstOrDefault(x => x.ProviderType == (int)account.ProviderType &&
            string.Equals(x.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
        var currentConsent = snapshot.Consent is { } consent && consent.Status == ConsentStatuses.Active &&
            consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;
        await ExecuteUIThread(() =>
        {
            SemanticMailboxId = mailbox?.MailboxId;
            HasAccountConsent = currentConsent;
            if (mailbox is not null) ApplyStatus(snapshot.MailboxStatuses.GetValueOrDefault(mailbox.MailboxId));
        });
        return true;
    }

    private async Task RefreshCachedAccountSnapshotAsync(MailAccount account)
    {
        if (_snapshotService is null) return;
        await ExecuteUIThread(() =>
        {
            IsRemoteRefreshInProgress = true;
            RemoteRefreshError = string.Empty;
        });
        try
        {
            var result = await _snapshotService.RefreshAsync().ConfigureAwait(false);
            if (result is not null)
            {
                var mailbox = result.Snapshot.Mailboxes.FirstOrDefault(x => x.ProviderType == (int)account.ProviderType &&
                    string.Equals(x.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
                await ExecuteUIThread(() =>
                {
                    SemanticMailboxId = mailbox?.MailboxId;
                    if (mailbox is not null) ApplyStatus(result.Snapshot.MailboxStatuses.GetValueOrDefault(mailbox.MailboxId));
                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        RemoteRefreshError = string.Format(
                            Translator.WinoIntelligence_CachedRefreshFailed,
                            result.Snapshot.LastSuccessfulRefreshUtc?.LocalDateTime.ToString("g") ?? Translator.GeneralTitle_Info);
                    }
                });
                if (mailbox is not null) await EnsureAvailableRangeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Cached data remains authoritative for this visit. API operations still validate live access.
            await ExecuteUIThread(() => RemoteRefreshError = string.Format(
                Translator.WinoIntelligence_CachedRefreshFailed,
                Translator.GeneralTitle_Info));
        }
        finally
        {
            await ExecuteUIThread(() => IsRemoteRefreshInProgress = false);
        }
    }

    [RelayCommand]
    private Task RetryRemoteRefreshAsync()
        => Account is null ? Task.CompletedTask : RefreshCachedAccountSnapshotAsync(Account);

    [RelayCommand(CanExecute = nameof(CanCancelIndexing))]
    private async Task CancelIndexingAsync()
    {
        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationCancelling);
            await _coordinator.CancelIndexingAsync(Account.Id).ConfigureAwait(false);
            await RefreshAfterJobAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    private bool CanCancelIndexing() => IsJobActive && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownloadAvailableIntelligence))]
    private async Task DownloadAvailableIntelligenceAsync()
    {
        if (Account is null)
            return;

        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationDownloading);
            var state = await _coordinator.DownloadAvailableIntelligenceAsync(Account.Id).ConfigureAwait(false);
            var status = SemanticMailboxId is { } mailboxId
                ? await _apiClient.GetIntelligenceStatusAsync(mailboxId).ConfigureAwait(false)
                : null;

            await ExecuteUIThread(() =>
            {
                ApplyStatus(status);
                StatusMessage = string.Format(
                    Translator.SemanticIndex_CloudRestored,
                    state.LocalIndexedMessageCount);
                StatusType = InfoBarMessageType.Success;
            });
            await RefreshIndexedMessageCountAsync().ConfigureAwait(false);
            await RefreshHeadlineLanguageAsync(status).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    private bool CanDownloadAvailableIntelligence()
        => IsPageReady && IsSemanticIndexingEnabled && HasIndexData && !IsBusy && !IsJobActive;

    [RelayCommand(CanExecute = nameof(CanTranslateHeadlines))]
    private async Task TranslateHeadlinesAsync()
    {
        if (Account is null) return;
        try
        {
            await SetBusyAsync(true, Translator.Intelligence_HeadlineTranslationInProgress);
            var targetLanguage = _translationService.CurrentLanguageModel.Code;
            var result = await _coordinator.TranslateHeadlinesAsync(Account.Id, targetLanguage).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                IsHeadlineLanguageMismatchVisible = false;
                HeadlineLanguageDescription = string.Format(Translator.Intelligence_HeadlineLanguage, LanguageName(result.HeadlineLanguage));
            });
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    private bool CanTranslateHeadlines() => IsPageReady && !IsBusy && !IsJobActive && IsHeadlineLanguageMismatchVisible;

    [RelayCommand]
    private async Task DismissHeadlineLanguageAsync()
    {
        IsHeadlineLanguageMismatchVisible = false;
        if (Account is not null && DontAskHeadlineLanguageAgain)
            await _localStore.SetHeadlineLanguagePromptSuppressedAsync(Account.Id, true).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSemanticIndex))]
    private async Task DeleteSemanticIndexAsync()
    {
        if (!await _dialogService.ShowConfirmationDialogAsync(
                Translator.SemanticIndex_DeleteConfirmation,
                Translator.SemanticIndex_DeleteTitle,
                Translator.Buttons_Delete))
            return;
        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationDeleting);
            await _coordinator.DeleteIndexAsync(Account.Id).ConfigureAwait(false);
            Account.Preferences.IsSemanticIndexingEnabled = false;
            await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                IsSemanticIndexingEnabled = false;
                SemanticMailboxId = null;
                HasIndexData = false;
                _intelligenceStatus = null;
                IndexedMessageCount = 0;
                CoverageDescription = Translator.SemanticIndex_NoIndexedMessages;
                StatusMessage = Translator.SemanticIndex_DisabledCallout;
                RefreshHeroState();
            });
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await SetBusyAsync(false);
        }
    }

    public void Receive(SemanticIndexJobChanged message)
    {
        if (Account?.Id != message.AccountId)
            return;
        _ = ExecuteUIThread(() => ApplySnapshot(message.Snapshot));
        if (message.Snapshot.Status is SemanticIndexJobStatus.Completed or SemanticIndexJobStatus.PausedForQuota or SemanticIndexJobStatus.Failed)
            _ = RefreshAfterJobAsync();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<SemanticIndexJobChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        Messenger.Unregister<SemanticIndexJobChanged>(this);
        base.UnregisterRecipients();
    }

    private async Task RecalculatePlanAsync()
    {
        if (Account is null || !IsSemanticIndexingEnabled || IsJobActive)
            return;
        try
        {
            await ExecuteUIThread(() =>
            {
                IsCalculatingPlan = true;
                PlanSummary = Translator.SemanticIndex_PlanCalculating;
                HasError = false;
                IsConsentActionVisible = false;
            });
            var plan = await CalculatePlanAsync().ConfigureAwait(false);
            _currentPlan = plan;
            await ExecuteUIThread(() =>
            {
                ProgressSummary = plan.MissingMessageCount == 0
                    ? Translator.SemanticIndex_PlanEmpty
                    : string.Format(Translator.SemanticIndex_OverallProgress, 0, plan.MissingMessageCount, plan.MissingMessageCount);
                PlanSummary = plan.MissingMessageCount == 0
                    ? Translator.SemanticIndex_PlanEmpty
                    : string.Format(Translator.SemanticIndex_PlanSummary, plan.MissingMessageCount, FormatDuration(plan.EstimatedDuration));

                StartIndexingCommand.NotifyCanExecuteChanged();

                if (plan.MissingMessageCount == 0)
                {
                    StatusMessage = Translator.SemanticIndex_UpToDate;
                    StatusType = InfoBarMessageType.Success;
                }
                else if (!HasIndexData)
                {
                    StatusMessage = Translator.SemanticIndex_NotReady;
                    StatusType = InfoBarMessageType.Information;
                }
            });
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception);
        }
        finally
        {
            await ExecuteUIThread(() => IsCalculatingPlan = false);
        }
    }

    private Task<SemanticIndexPlan> CalculatePlanAsync()
    {
        var count = SelectedRangeMessageCount;
        return Task.FromResult(new SemanticIndexPlan(
            Account.Id,
            SemanticIndexRangePreset.Custom,
            GetSelectedCutoffUtc(),
            GetSelectedThroughUtcExclusive(),
            AutomaticallyIndexNewMessages,
            count,
            EstimatedMissingMessageCount,
            TimeSpan.FromSeconds(EstimatedMissingMessageCount * 0.6),
            false));
    }

    private bool CanStartIndexing()
        => IsPageReady && IsSemanticIndexingEnabled && !IsBusy && !IsCalculatingPlan && !IsJobActive &&
           _currentPlan?.MissingMessageCount > 0;

    private bool CanDeleteSemanticIndex()
        => IsPageReady && !IsBusy && !IsJobActive && HasIndexData;

    [RelayCommand(CanExecute = nameof(CanUpgradeEmbeddingProfile))]
    private async Task UpgradeEmbeddingProfileAsync()
    {
        if (Account is null || SemanticMailboxId is null)
            return;
        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_UpgradeInProgress);
            await _apiClient.RebuildIntelligenceEmbeddingsAsync(SemanticMailboxId.Value).ConfigureAwait(false);
            var plan = await CalculatePlanAsync().ConfigureAwait(false);
            _currentPlan = plan;
            await _coordinator.StartIndexingAsync(Account.Id, plan, notifyWhenCompleted: true).ConfigureAwait(false);
            await ExecuteUIThread(() => IsUpgradeRecommended = false);
        }
        catch (Exception exception)
        {
            await ExecuteUIThread(() => ApplyErrorStatus(exception.Message));
        }
        finally
        {
            await SetBusyAsync(false, null);
        }
    }

    private bool CanUpgradeEmbeddingProfile()
        => IsUpgradeRecommended && IsPageReady && IsSemanticIndexingEnabled && !IsBusy && !IsJobActive;

    private void ApplyState(SemanticIndexAccountState state)
    {
        HasError = false;
        IsConsentActionVisible = false;
        IsSemanticIndexingEnabled = state.IsEnabled;
        HasIndexData = state.IntelligenceState?.IndexedMessageCount > 0 || state.LastImportedVersion > 0;
        IsUpgradeRecommended = state.IntelligenceState?.IndexState == IntelligenceIndexStates.UpgradeRecommended;
        _recommendedProfileId = state.IntelligenceState?.RecommendedEmbeddingProfile.Id ?? string.Empty;
        ApplyProfile(state.IntelligenceState?.IndexingProfile);
        StartButtonText = state.IntelligenceState?.IndexingProfile?.BackfillStatus == "in-progress"
            ? Translator.SemanticIndex_ContinueButton
            : Translator.SemanticIndex_StartButton;
        CoverageDescription = CreateCoverageDescription(state);
        StatusMessage = state switch
        {
            { WaitingMessageCount: > 0 } => string.Format(Translator.SemanticIndex_CloudRemaining, state.WaitingMessageCount),
            { IsUpToDate: true } => Translator.SemanticIndex_UpToDate,
            _ => Translator.SemanticIndex_NotReady,
        };
        StatusType = state.IsUpToDate ? InfoBarMessageType.Success : InfoBarMessageType.Information;
        ApplySnapshot(_coordinator.GetJobSnapshot(Account.Id));
    }

    private void ApplyStatus(IntelligenceMailboxStatusDto? status)
    {
        _intelligenceStatus = status;
        HasError = false;
        IsConsentActionVisible = false;
        HasIndexData = status is { StorageSizeBytes: > 0 };
        IsUpgradeRecommended = status?.EmbeddingModelStatus is
            EmbeddingModelStatuses.UpgradeAvailable or EmbeddingModelStatuses.ReindexRequired;
        CoverageDescription = CreateCoverageDescription(status);
        if (_availableRange is not null)
            UpdateSelectedRangeSummary();
        StatusMessage = HasIndexData ? Translator.SemanticIndex_UpToDate : Translator.SemanticIndex_NotReady;
        StatusType = HasIndexData ? InfoBarMessageType.Success : InfoBarMessageType.Information;
        StartButtonText = Translator.SemanticIndex_StartButton;
        ApplySnapshot(_coordinator.GetJobSnapshot(Account.Id));
        RefreshHeroState();
    }

    private void ApplyProfile(IntelligenceIndexingProfileDto profile)
    {
        if (profile is null)
            return;
        _isApplyingProfile = true;
        try
        {
            if (_availableRange is not null)
            {
                var cutoffDate = profile.CutoffUtc?.LocalDateTime is { } cutoff
                    ? DateOnly.FromDateTime(cutoff)
                    : _availableRange.OldestDate;
                SelectedRangeStartOffset = Math.Clamp(
                    cutoffDate.DayNumber - _availableRange.OldestDate.DayNumber,
                    0,
                    _availableRange.DaySpan);
                SelectedRangeEndOffset = RangeMaximum;
                UpdateSelectedRangeSummary();
            }
            AutomaticallyIndexNewMessages = profile.AutomaticallyIndexNewMessages;
            NewMessageModeIndex = profile.AutomaticallyIndexNewMessages ? 0 : 1;
        }
        finally
        {
            _isApplyingProfile = false;
        }
    }

    private void ApplySnapshot(SemanticIndexJobSnapshot snapshot)
    {
        JobStatus = snapshot.Status;
        IsJobActive = snapshot.IsActive;
        ProgressValue = snapshot.EmbeddingProcessedMessageCount;
        ProgressMaximum = Math.Max(1, snapshot.TotalMessageCount);
        ProgressText = string.Format(
            Translator.SemanticIndex_EmbeddingProgress,
            snapshot.EmbeddingProcessedMessageCount,
            snapshot.TotalMessageCount,
            snapshot.EmbeddingFailedMessageCount);
        MetadataProgressValue = snapshot.MetadataProcessedMessageCount;
        MetadataProgressText = string.Format(
            Translator.SemanticIndex_MetadataProgress,
            snapshot.MetadataProcessedMessageCount,
            snapshot.TotalMessageCount,
            snapshot.MetadataFailedMessageCount);
        var completedMessageCount = Math.Min(snapshot.EmbeddingProcessedMessageCount, snapshot.MetadataProcessedMessageCount);
        var remainingMessageCount = Math.Max(snapshot.TotalMessageCount - completedMessageCount, 0);
        ProgressSummary = snapshot.TotalMessageCount == 0
            ? Translator.SemanticIndex_PlanEmpty
            : string.Format(Translator.SemanticIndex_OverallProgress, completedMessageCount, snapshot.TotalMessageCount, remainingMessageCount);
        if (snapshot.Status == SemanticIndexJobStatus.PausedForSynchronization)
            StatusMessage = Translator.SemanticIndex_PausedForSync;
        else if (snapshot.Status == SemanticIndexJobStatus.PausedForQuota)
        {
            StatusMessage = Translator.SemanticIndex_PausedForQuota;
            StatusType = InfoBarMessageType.Warning;
        }
        else if (snapshot.Status == SemanticIndexJobStatus.Failed)
        {
            ApplyErrorStatus(snapshot.ErrorCode ?? Translator.SemanticIndex_NotReady);
        }

        RefreshHeroState();
    }

    private async Task RefreshAfterJobAsync()
    {
        try
        {
            var status = SemanticMailboxId is { } mailboxId
                ? await _apiClient.GetIntelligenceStatusAsync(mailboxId).ConfigureAwait(false)
                : null;
            await ExecuteUIThread(() => ApplyStatus(status));
            await RefreshIndexedMessageCountAsync().ConfigureAwait(false);
            await RefreshQuotaAsync().ConfigureAwait(false);
            if (!IsJobActive)
                await RecalculatePlanAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task RefreshIntelligenceStatusAsync()
    {
        if (SemanticMailboxId is not { } mailboxId)
            return;
        try
        {
            var status = await _apiClient.GetIntelligenceStatusAsync(mailboxId).ConfigureAwait(false);
            await ExecuteUIThread(() => ApplyStatus(status));
            await RefreshHeadlineLanguageAsync(status).ConfigureAwait(false);
        }
        catch
        {
            // Enabling or accepting consent already succeeded. A status refresh is
            // best-effort and the next page refresh will retry it.
        }
    }

    private string CreateCoverageDescription(SemanticIndexAccountState state)
    {
        if (state.IntelligenceState is null || state.IntelligenceState.IndexedMessageCount == 0)
            return Translator.SemanticIndex_NoIndexedMessages;
        var count = string.Format(Translator.SemanticIndex_IndexedCount, state.IntelligenceState.IndexedMessageCount);
        var size = string.Format(Translator.SemanticIndex_StorageSize, FormatStorageSize(state.IntelligenceState.StorageSizeBytes));
        if (state.IntelligenceState.OldestIndexedAtUtc is null || state.IntelligenceState.NewestIndexedAtUtc is null)
            return $"{count}\n{size}";
        return string.Format(
            Translator.SemanticIndex_CoverageRangeWithSize,
            count,
            state.IntelligenceState.OldestIndexedAtUtc.Value.LocalDateTime.ToString("d MMMM yyyy"),
            state.IntelligenceState.NewestIndexedAtUtc.Value.LocalDateTime.ToString("d MMMM yyyy"),
            size);
    }

    private string CreateCoverageDescription(IntelligenceMailboxStatusDto? status)
    {

        if (status is null || status.StorageSizeBytes == 0)
            return Translator.SemanticIndex_NoIndexedMessages;
        var size = string.Format(Translator.SemanticIndex_StorageSize, FormatStorageSize(status.StorageSizeBytes));
        if (status.OldestReceivedAtUtc is null || status.NewestReceivedAtUtc is null)
            return size;

        if (string.IsNullOrEmpty(status.HeadlineLanguage))
        {
            return $"{status.OldestReceivedAtUtc.Value.LocalDateTime:d MMMM yyyy} – " +
               $"{status.NewestReceivedAtUtc.Value.LocalDateTime:d MMMM yyyy}\n{size}";
        }
        else
        {
            return $"{status.OldestReceivedAtUtc.Value.LocalDateTime:d MMMM yyyy} – " +
          $"{status.NewestReceivedAtUtc.Value.LocalDateTime:d MMMM yyyy}\n{size}\n{status.HeadlineLanguage}";
        }
    }

    private async Task RefreshHeadlineLanguageAsync(IntelligenceMailboxStatusDto? status)
    {
        if (Account is null || SemanticMailboxId is not { } mailboxId) return;
        var serverLanguage = status?.HeadlineLanguage;
        if (!string.IsNullOrWhiteSpace(serverLanguage))
            await _localStore.SetHeadlineLanguageAsync(Account.Id, mailboxId, serverLanguage, CancellationToken.None).ConfigureAwait(false);
        var language = serverLanguage ?? await _localStore.GetHeadlineLanguageAsync(Account.Id).ConfigureAwait(false) ?? string.Empty;
        var current = _translationService.CurrentLanguageModel.Code;
        var suppressed = await _localStore.GetHeadlineLanguagePromptSuppressedAsync(Account.Id).ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            HeadlineLanguageDescription = string.IsNullOrWhiteSpace(language)
                ? string.Empty
                : string.Format(Translator.Intelligence_HeadlineLanguage, LanguageName(language));
            HeadlineLanguageMismatchMessage = string.Format(
                Translator.Intelligence_HeadlineLanguageMismatch,
                LanguageName(language),
                LanguageName(current));
            DontAskHeadlineLanguageAgain = false;
            IsHeadlineLanguageMismatchVisible = !string.IsNullOrWhiteSpace(language)
                && !string.Equals(language, current, StringComparison.OrdinalIgnoreCase)
                && !suppressed;
            TranslateHeadlinesCommand.NotifyCanExecuteChanged();
        });
    }

    private string LanguageName(string code)
        => _translationService.GetAvailableLanguages().FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? code;

    /// <summary>
    /// Loads the retrieved message range once. It is a local query that does not depend
    /// on consent or on intelligence being enabled, so every entry point can ask for it.
    /// </summary>
    private async Task EnsureAvailableRangeAsync(bool refreshRemote = true)
    {
        if (Account is null)
            return;

        var localRange = await _coordinator.GetAvailableRangeAsync(Account.Id).ConfigureAwait(false);
        var candidates = await _messageContextResolver.GetCandidatesAsync(Account.Id).ConfigureAwait(false);
        var oldestDate = localRange?.OldestDate;
        var newestDate = localRange?.NewestDate;
        if (_intelligenceStatus?.OldestReceivedAtUtc is { } cloudOldest)
            oldestDate = Min(oldestDate, DateOnly.FromDateTime(cloudOldest.UtcDateTime));
        if (_intelligenceStatus?.NewestReceivedAtUtc is { } cloudNewest)
            newestDate = Max(newestDate, DateOnly.FromDateTime(cloudNewest.UtcDateTime));

        if (oldestDate is null || newestDate is null)
        {
            await ExecuteUIThread(() => ApplyAvailableRange(null));
            return;
        }

        var localCounts = localRange?.MessageCountsByDate ?? new Dictionary<DateOnly, int>();
        var unionCounts = Enumerable.Range(0, newestDate.Value.DayNumber - oldestDate.Value.DayNumber + 1)
            .ToDictionary(offset => oldestDate.Value.AddDays(offset), offset =>
                localCounts.GetValueOrDefault(oldestDate.Value.AddDays(offset)));
        var availableRange = new SemanticIndexAvailableRange(oldestDate.Value, newestDate.Value, unionCounts);
        await ExecuteUIThread(() => ApplyAvailableRange(availableRange));

        if (!refreshRemote || SemanticMailboxId is not Guid mailboxId)
            return;

        var fromUtc = new DateTimeOffset(oldestDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(newestDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var timeline = await _apiClient.GetIntelligenceCoverageTimelineAsync(
            mailboxId, fromUtc, toUtc, RangeHistogramBucketCount).ConfigureAwait(false);
        var missingIds = (await _apiClient.ResolveIntelligenceDeltaAsync(
            mailboxId, candidates.Select(x => x.RemoteMessageId).ToArray()).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        await ExecuteUIThread(() => BuildCoverageBuckets(timeline, candidates, missingIds));
    }

    private void ApplyAvailableRange(SemanticIndexAvailableRange availableRange)
    {
        _availableRange = availableRange;
        HasAvailableMessages = availableRange is not null;
        RangeMaximum = availableRange?.DaySpan ?? 0;
        OldestAvailableDateText = availableRange?.OldestDate.ToString("d MMM yyyy") ?? string.Empty;
        NewestAvailableDateText = availableRange?.NewestDate.ToString("d MMM yyyy") ?? string.Empty;
        BuildRangeBuckets(availableRange);
        RestoreSelectedRange(availableRange);
        UpdateSelectedRangeSummary();
    }

    /// <summary>
    /// Puts the selection back where the user left it. A mailbox that has never had a
    /// range chosen starts at one month rather than at the whole mailbox.
    /// </summary>
    private void RestoreSelectedRange(SemanticIndexAvailableRange? availableRange)
    {
        if (availableRange is null)
        {
            SelectedRangeStartOffset = 0;
            SelectedRangeEndOffset = 0;
            return;
        }

        // Restoring is not a user edit, so it must not schedule a plan recalculation
        // or write the value straight back to the account.
        _isApplyingProfile = true;
        try
        {
            var preferences = Account?.Preferences;
            var selection = SemanticIndexRangeSelectionResolver.Resolve(
                availableRange,
                preferences?.SemanticIndexRangePresetId,
                preferences?.SemanticIndexRangeCutoffUtc,
                preferences?.SemanticIndexRangeThroughUtc);

            SelectedRangeEndOffset = selection.EndOffset;
            SelectedRangeStartOffset = selection.StartOffset;
        }
        finally
        {
            _isApplyingProfile = false;
        }
    }

    /// <summary>
    /// Stores the selection on the account so returning to the page shows it again.
    /// </summary>
    private async Task PersistSelectedRangeAsync()
    {
        if (Account?.Preferences is not { } preferences || _availableRange is null)
            return;

        var startOffset = Math.Clamp((int)Math.Round(SelectedRangeStartOffset), 0, _availableRange.DaySpan);
        var endOffset = Math.Clamp((int)Math.Round(SelectedRangeEndOffset), startOffset, _availableRange.DaySpan);
        var presetId = SelectedRangePreset.ToStableId();
        var cutoffUtc = SelectedRangePreset == SemanticIndexRangePreset.Custom
            ? _availableRange.OldestDate.AddDays(startOffset).ToDateTime(TimeOnly.MinValue)
            : (DateTime?)null;
        var throughUtc = SelectedRangePreset == SemanticIndexRangePreset.Custom
            ? _availableRange.OldestDate.AddDays(endOffset).ToDateTime(TimeOnly.MinValue)
            : (DateTime?)null;

        if (preferences.SemanticIndexRangePresetId == presetId &&
            preferences.SemanticIndexRangeCutoffUtc == cutoffUtc &&
            preferences.SemanticIndexRangeThroughUtc == throughUtc)
            return;

        preferences.SemanticIndexRangePresetId = presetId;
        preferences.SemanticIndexRangeCutoffUtc = cutoffUtc;
        preferences.SemanticIndexRangeThroughUtc = throughUtc;

        try
        {
            await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
        }
        catch
        {
            // Remembering the range is a convenience. A failure must not break indexing.
        }
    }


    private void BuildRangeBuckets(SemanticIndexAvailableRange? availableRange)
    {
        RangeBuckets.Clear();
        if (availableRange is null)
            return;

        var daySpan = availableRange.DaySpan;
        var daysPerBucket = Math.Max(1, (int)Math.Ceiling((daySpan + 1) / (double)RangeHistogramBucketCount));
        var pending = new List<(int StartOffset, int EndOffset, int MessageCount)>();

        for (var startOffset = 0; startOffset <= daySpan; startOffset += daysPerBucket)
        {
            var endOffset = Math.Min(daySpan, startOffset + daysPerBucket - 1);
            var startDate = availableRange.OldestDate.AddDays(startOffset);
            var endDate = availableRange.OldestDate.AddDays(endOffset);
            var messageCount = availableRange.MessageCountsByDate
                .Where(pair => pair.Key >= startDate && pair.Key <= endDate)
                .Sum(pair => pair.Value);
            pending.Add((startOffset, endOffset, messageCount));
        }

        var busiestBucketCount = pending.Count == 0 ? 0 : pending.Max(bucket => bucket.MessageCount);
        var barWidth = pending.Count == 0
            ? 0
            : SemanticIndexRangeBucketViewModel.HistogramWidth / pending.Count;

        foreach (var bucket in pending)
        {
            RangeBuckets.Add(new SemanticIndexRangeBucketViewModel
            {
                StartOffset = bucket.StartOffset,
                EndOffset = bucket.EndOffset,
                StartDate = availableRange.OldestDate.AddDays(bucket.StartOffset),
                EndDate = availableRange.OldestDate.AddDays(bucket.EndOffset),
                MessageCount = bucket.MessageCount,
                LocalAndCloudCount = 0,
                LocalOnlyCount = bucket.MessageCount,
                CloudOnlyCount = 0,
                BarHeight = SemanticIndexRangeBucketViewModel.CalculateBarHeight(bucket.MessageCount, busiestBucketCount),
                BarWidth = barWidth,
            });
        }
    }

    /// <summary>
    /// Repaints the histogram for the current selection. Only the coverage of each
    /// existing bucket changes, so dragging the selector does not rebuild the bars.
    /// </summary>
    private void UpdateBucketCoverage(int startOffset, int endOffset)
    {
        if (RangeBuckets.Count == 0)
            return;

        foreach (var bucket in RangeBuckets)
        {
            bucket.IsSelected = HasAvailableMessages && bucket.EndOffset >= startOffset && bucket.StartOffset <= endOffset;
        }
    }

    private void BuildCoverageBuckets(
        IntelligenceCoverageTimelineDto timeline,
        IReadOnlyList<Wino.Core.Domain.Models.Intelligence.IntelligenceMessageCandidate> candidates,
        IReadOnlySet<string> missingIds)
    {
        if (_availableRange is null || timeline.Buckets.Count == 0)
            return;

        var counts = timeline.Buckets.Select(bucket =>
        {
            var local = candidates.Where(candidate => candidate.ReceivedAt >= bucket.StartUtc && candidate.ReceivedAt < bucket.EndUtc).ToArray();
            var localAndCloud = local.Count(candidate => !missingIds.Contains(candidate.RemoteMessageId));
            var localOnly = local.Length - localAndCloud;
            var cloudOnly = Math.Max(0, (int)Math.Min(int.MaxValue, bucket.IndexedMessageCount) - localAndCloud);
            return (Bucket: bucket, LocalAndCloud: localAndCloud, LocalOnly: localOnly, CloudOnly: cloudOnly,
                Total: localAndCloud + localOnly + cloudOnly);
        }).ToArray();
        var busiest = counts.Max(x => x.Total);
        var barWidth = SemanticIndexRangeBucketViewModel.HistogramWidth / counts.Length;
        RangeBuckets.Clear();
        foreach (var item in counts)
        {
            var startDate = DateOnly.FromDateTime(item.Bucket.StartUtc.UtcDateTime);
            var endDate = DateOnly.FromDateTime(item.Bucket.EndUtc.AddTicks(-1).UtcDateTime);
            RangeBuckets.Add(new SemanticIndexRangeBucketViewModel
            {
                StartOffset = startDate.DayNumber - _availableRange.OldestDate.DayNumber,
                EndOffset = endDate.DayNumber - _availableRange.OldestDate.DayNumber,
                StartDate = startDate,
                EndDate = endDate,
                MessageCount = item.Total,
                LocalAndCloudCount = item.LocalAndCloud,
                LocalOnlyCount = item.LocalOnly,
                CloudOnlyCount = item.CloudOnly,
                BarHeight = SemanticIndexRangeBucketViewModel.CalculateBarHeight(item.Total, busiest),
                BarWidth = barWidth,
            });
        }
        UpdateBucketCoverage((int)Math.Round(SelectedRangeStartOffset), (int)Math.Round(SelectedRangeEndOffset));
    }

    private static DateOnly Min(DateOnly? left, DateOnly right) => left is null || right < left ? right : left.Value;
    private static DateOnly Max(DateOnly? left, DateOnly right) => left is null || right > left ? right : left.Value;

    /// <summary>
    /// Maps the current selection back to a preset so the preset row can show which
    /// one is active. Anything that does not line up stays <see cref="SemanticIndexRangePreset.Custom"/>.
    /// </summary>
    private void UpdateSelectedRangePreset(int startOffset, int endOffset)
    {
        if (_availableRange is null || endOffset != _availableRange.DaySpan)
        {
            SelectedRangePreset = SemanticIndexRangePreset.Custom;
            return;
        }

        var selectedDays = _availableRange.DaySpan - startOffset;
        SelectedRangePreset = selectedDays switch
        {
            0 => SemanticIndexRangePreset.OnlyNew,
            7 => SemanticIndexRangePreset.OneWeek,
            30 => SemanticIndexRangePreset.OneMonth,
            91 => SemanticIndexRangePreset.ThreeMonths,
            182 => SemanticIndexRangePreset.SixMonths,
            365 => SemanticIndexRangePreset.OneYear,
            // A preset that reaches past the oldest message covers the whole mailbox.
            _ when startOffset == 0 => SemanticIndexRangePreset.Everything,
            _ => SemanticIndexRangePreset.Custom,
        };
    }

    #region Hero summary

    /// <summary>
    /// Recomputes the single headline state and the one action that goes with it.
    /// Every operation that changes status ends up here so the hero can never
    /// disagree with the cards below it.
    /// </summary>
    private void RefreshHeroState()
    {
        var (text, type, isProgressVisible) = CalculateHeroState();
        HeroStateText = text;
        HeroStateType = type;
        IsHeroProgressVisible = isProgressVisible;

        RefreshIndexStateSummary();
    }

    private (string Text, InfoBarMessageType Type, bool IsProgressVisible) CalculateHeroState()
    {
        if (!IsPageReady)
            return (Translator.SemanticIndex_OperationLoading, InfoBarMessageType.Information, true);

        if (IsBusy)
            return (StatusMessage, InfoBarMessageType.Information, true);

        if (!HasAccountConsent)
            return (Translator.SemanticIndex_HeroStateAttention, InfoBarMessageType.Error, false);

        if (!IsSemanticIndexingEnabled)
            return (Translator.SemanticIndex_HeroStateOff, InfoBarMessageType.Information, false);

        if (HasError)
            return (Translator.SemanticIndex_HeroStateAttention, InfoBarMessageType.Error, false);

        if (JobStatus == SemanticIndexJobStatus.PausedForQuota)
            return (Translator.SemanticIndex_HeroStateAttention, InfoBarMessageType.Warning, false);

        if (IsJobActive)
            return (Translator.SemanticIndex_HeroStateIndexing, InfoBarMessageType.Information, false);

        if (IsCalculatingPlan)
            return (Translator.SemanticIndex_PlanCalculating, InfoBarMessageType.Information, true);

        if (!HasAvailableMessages)
            return (Translator.SemanticIndex_DeviceEmptyTitle, InfoBarMessageType.Information, false);

        return EstimatedMissingMessageCount > 0
            ? (string.Format(Translator.SemanticIndex_HeroStatePending, EstimatedMissingMessageCount), InfoBarMessageType.Warning, false)
            : (Translator.SemanticIndex_HeroStateUpToDate, InfoBarMessageType.Success, false);
    }

    private void RefreshIndexStateSummary()
        => IndexedMessageCountDetail = IsJobActive
            ? string.Empty
            : IndexedMessageCount > 0
                ? string.Format(Translator.SemanticIndex_IndexedCount, IndexedMessageCount)
                : Translator.SemanticIndex_NoIndexedMessages;

    /// <summary>
    /// The period usage endpoint is account wide and unrelated to this mailbox's index,
    /// so a failure only hides the quota tile instead of failing the page.
    /// </summary>
    private async Task RefreshQuotaAsync()
    {
        try
        {
            var response = await _apiClient.GetAiUsageAsync().ConfigureAwait(false);
            var usage = response?.IsSuccess == true ? response.Result : null;
            await ExecuteUIThread(() =>
            {
                IsQuotaAvailable = usage is not null;
                QuotaUsagePercentage = usage is null ? 0 : (double)usage.UsagePercentage;
                QuotaSummary = usage is null
                    ? Translator.Intelligence_QuotaUnavailable
                    : string.Format(
                        Translator.Intelligence_QuotaUsage,
                        usage.UsagePercentage,
                        usage.ResetsAtUtc is { } resetsAtUtc ? resetsAtUtc.LocalDateTime.ToString("d MMMM") : string.Empty);
            });
        }
        catch
        {
            await ExecuteUIThread(() =>
            {
                IsQuotaAvailable = false;
                QuotaSummary = Translator.Intelligence_QuotaUnavailable;
            });
        }
    }

    /// <summary>
    /// Reads the indexed message count, which the mailbox status DTO does not carry.
    /// Best effort: the rest of the page works without it.
    /// </summary>
    private async Task RefreshIndexedMessageCountAsync()
    {
        if (Account is null)
            return;

        try
        {
            var state = await _coordinator.GetStateAsync(Account.Id).ConfigureAwait(false);
            var serverVectorCount = state?.ServerIndex?.VectorCount ?? 0;
            var indexedMessageCount = (int)Math.Max(
                state?.LocalIndexedMessageCount ?? 0,
                Math.Min(serverVectorCount, int.MaxValue));
            await ExecuteUIThread(() =>
            {
                IndexedMessageCount = indexedMessageCount;
                RefreshIndexStateSummary();
            });
        }
        catch
        {
            // Leaves the tile on its placeholder.
        }
    }

    #endregion

    private void UpdateSelectedRangeSummary()
    {
        if (_availableRange is null)
        {
            SelectedRangeMessageCount = 0;
            EstimatedMissingMessageCount = 0;
            SelectedRangeSummary = Translator.SemanticIndex_NoAvailableMessages;
            return;
        }

        var startOffset = Math.Clamp((int)Math.Round(SelectedRangeStartOffset), 0, _availableRange.DaySpan);
        var endOffset = Math.Clamp((int)Math.Round(SelectedRangeEndOffset), startOffset, _availableRange.DaySpan);
        var selectedStart = _availableRange.OldestDate.AddDays(startOffset);
        var selectedEnd = _availableRange.OldestDate.AddDays(endOffset);
        SelectedRangeMessageCount = _availableRange.MessageCountsByDate
            .Where(pair => pair.Key >= selectedStart && pair.Key <= selectedEnd)
            .Sum(pair => pair.Value);
        EstimatedMissingMessageCount = CalculateEstimatedMissingMessageCount(selectedStart, selectedEnd);
        SelectedRangeSummary = string.Format(
            Translator.SemanticIndex_SelectedRangeSummary,
            selectedStart.ToString("d MMMM yyyy"),
            selectedEnd.ToString("d MMMM yyyy"),
            SelectedRangeMessageCount);
        UpdateBucketCoverage(startOffset, endOffset);
        UpdateSelectedRangePreset(startOffset, endOffset);
    }

    private int CalculateEstimatedMissingMessageCount(DateOnly selectedStart, DateOnly selectedEnd)
    {
        if (_availableRange is null || SelectedRangeMessageCount == 0 || _intelligenceStatus is null ||
            _intelligenceStatus.OldestReceivedAtUtc is null || _intelligenceStatus.NewestReceivedAtUtc is null)
            return SelectedRangeMessageCount;

        var indexedStart = DateOnly.FromDateTime(_intelligenceStatus.OldestReceivedAtUtc.Value.UtcDateTime);
        var indexedEnd = DateOnly.FromDateTime(_intelligenceStatus.NewestReceivedAtUtc.Value.UtcDateTime);
        if (indexedStart > indexedEnd)
            return SelectedRangeMessageCount;

        return _availableRange.MessageCountsByDate
            .Where(pair => pair.Key >= selectedStart && pair.Key <= selectedEnd)
            .Where(pair => pair.Key < indexedStart || pair.Key > indexedEnd)
            .Sum(pair => pair.Value);
    }

    private DateTimeOffset GetSelectedCutoffUtc()
    {
        if (_availableRange is null)
            return DateTimeOffset.UtcNow;

        var offset = Math.Clamp((int)Math.Round(SelectedRangeStartOffset), 0, _availableRange.DaySpan);
        var localStart = DateTime.SpecifyKind(
            _availableRange.OldestDate.AddDays(offset).ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Local);
        return new DateTimeOffset(localStart).ToUniversalTime();
    }

    private DateTimeOffset GetSelectedThroughUtcExclusive()
    {
        if (_availableRange is null)
            return DateTimeOffset.UtcNow;

        var startOffset = Math.Clamp((int)Math.Round(SelectedRangeStartOffset), 0, _availableRange.DaySpan);
        var endOffset = Math.Clamp((int)Math.Round(SelectedRangeEndOffset), startOffset, _availableRange.DaySpan);
        var localEndExclusive = DateTime.SpecifyKind(
            _availableRange.OldestDate.AddDays(endOffset + 1).ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Local);
        return new DateTimeOffset(localEndExclusive).ToUniversalTime();
    }

    private void SchedulePlanRecalculation()
    {
        _rangeRecalculationCancellation?.Cancel();
        _rangeRecalculationCancellation?.Dispose();
        _rangeRecalculationCancellation = new CancellationTokenSource();
        _ = RecalculatePlanAfterDelayAsync(_rangeRecalculationCancellation.Token);
    }

    private async Task RecalculatePlanAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            // Only a settled user edit reaches this point, so this is where the choice
            // is worth remembering.
            await PersistSelectedRangeAsync().ConfigureAwait(false);
            await RecalculatePlanAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task SetBusyAsync(bool value, string message = "") => ExecuteUIThread(() =>
    {
        IsBusy = value;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
            StatusType = InfoBarMessageType.Information;
        }
        if (value)
        {
            HasError = false;
            IsConsentActionVisible = false;
        }
        RefreshHeroState();
    });

    private Task ShowErrorAsync(Exception exception) => ExecuteUIThread(() => ApplyErrorStatus(exception.Message));

    private void ApplyErrorStatus(string? error)
    {
        var isConsentRequired = error is WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode or WinoAccountApiErrorTranslator.IntelligenceConsentVersionOutdatedCode ||
                                string.Equals(error, Translator.WinoAccount_IntelligenceConsentNotGranted, StringComparison.Ordinal);
        HasError = true;
        IsConsentActionVisible = isConsentRequired;
        StatusMessage = string.IsNullOrWhiteSpace(error)
            ? Translator.SemanticIndex_NotReady
            : WinoAccountApiErrorTranslator.Translate(error);
        StatusType = InfoBarMessageType.Error;
        RefreshHeroState();
    }

    private async Task<bool> LoadAccountConsentAsync()
    {
        var consentTask = _apiClient.GetIntelligenceConsentAsync();
        var mailboxesTask = _apiClient.GetSemanticMailboxesAsync();
        await Task.WhenAll(consentTask, mailboxesTask).ConfigureAwait(false);
        var consent = await consentTask.ConfigureAwait(false);
        var mailbox = (await mailboxesTask.ConfigureAwait(false)).FirstOrDefault(x =>
            x.ProviderType == (int)Account.ProviderType &&
            string.Equals(x.Address.Trim(), Account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
        var current = consent.Status == ConsentStatuses.Active && consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;
        await ExecuteUIThread(() =>
        {
            SemanticMailboxId = mailbox?.MailboxId;
            HasAccountConsent = current;
        });
        return current;
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? string.Format(Translator.SemanticIndex_DurationMinutes, Math.Ceiling(duration.TotalMinutes))
            : string.Format(Translator.SemanticIndex_DurationSeconds, Math.Ceiling(duration.TotalSeconds));

    private static string FormatStorageSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}
