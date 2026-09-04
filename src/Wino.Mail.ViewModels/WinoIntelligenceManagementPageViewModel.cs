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
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
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
    /// How long to wait before re-reading a mailbox that came back with no folders at all, which
    /// means the page opened while the account's folders were still being written.
    /// </summary>
    private static readonly TimeSpan CoverageReloadRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IMailDialogService _dialogService;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ISemanticIndexCoordinator _coordinator;
    private readonly IIntelligenceMessageContextResolver _messageContextResolver;
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ILocalIntelligenceStore _localStore;
    private readonly ITranslationService _translationService;
    private readonly IWinoAccountProfileService? _profileService;
    private readonly IWinoAccountIntelligenceSnapshotService? _snapshotService;
    private readonly IIntelligenceCoverageHandoff _coverageHandoff;
    private bool _isApplyingProfile;
    private HashSet<string> _selectedRemoteMessageIds = new(StringComparer.Ordinal);
    private HashSet<string> _coveredRemoteMessageIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The account's mail as identity and date, read once per navigation. Every folder count, date
    /// range and latest-N answer on this page is computed from it without further I/O.
    /// </summary>
    private IntelligenceCoverageInventory? _inventory;

    /// <summary>
    /// The account's folders, read alongside the inventory. Kept so the coverage editor can be
    /// opened without a second folder read.
    /// </summary>
    private IReadOnlyCollection<MailItemFolder> _folders = [];

    public WinoIntelligenceManagementPageViewModel(
        IMailDialogService dialogService,
        IAccountService accountService,
        IFolderService folderService,
        ISemanticIndexCoordinator semanticIndexCoordinator,
        IIntelligenceMessageContextResolver messageContextResolver,
        IWinoAccountApiClient apiClient,
        ILocalIntelligenceStore localStore,
        ITranslationService translationService,
        IIntelligenceCoverageHandoff coverageHandoff,
        IWinoAccountProfileService? profileService = null,
        IWinoAccountIntelligenceSnapshotService? snapshotService = null)
    {
        _coverageHandoff = coverageHandoff;
        _dialogService = dialogService;
        _accountService = accountService;
        _folderService = folderService;
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
    public partial bool IsDailyBriefingEnabled { get; set; } = true;

    /// <summary>Ordered, local visibility choices for the current mailbox.</summary>
    public ObservableCollection<IntelligenceIndicatorSettingsItem> IntelligenceIndicatorSettings { get; } = [];

    /// <summary>
    /// One row per folder the user included, each holding that folder's own rule. Populated once
    /// per navigation from the inventory and recomputed in memory afterwards.
    /// </summary>
    public ObservableCollection<IntelligenceFolderCoverageItem> IntelligenceFolderCoverageItems { get; } = [];

    /// <summary>
    /// The rule every included folder follows unless it carries one of its own. Editing this is
    /// the whole job for most mailboxes; per-folder rules are the exception.
    /// </summary>
    [ObservableProperty]
    public partial SemanticIndexFolderCoverageRule DefaultCoverageRule { get; set; }
        = SemanticIndexFolderCoverageRule.Latest(string.Empty, MailAccountPreferences.DefaultLatestMessageCount);

    public bool HasCoverageFolders => IntelligenceFolderCoverageItems.Count > 0;
    public bool IsCoverageEmptyStateVisible => IsPageReady && IntelligenceFolderCoverageItems.Count == 0;

    /// <summary>
    /// The coverage group stays on screen when intelligence is off — hiding it would make the
    /// switch look like it discarded the configuration — but nothing in it can be edited.
    /// </summary>
    public bool IsCoverageEditable => IsPageReady && IsSemanticIndexingEnabled && !IsJobActive && !IsBusy;

    /// <summary>
    /// Features are hidden rather than disabled while intelligence is off: a Daily Briefing toggle
    /// for a mailbox that produces no intelligence is not a setting, it is noise.
    /// </summary>
    public bool IsFeaturesGroupVisible => IsPageReady && IsSemanticIndexingEnabled;

    /// <summary>
    /// Indexed data stays available even when intelligence is off, because that is exactly when
    /// someone wants to delete what it already stored.
    /// </summary>
    public bool IsIndexedDataGroupVisible => IsPageReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCoverageSummary))]
    public partial int TotalAvailableMessageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalCoverageSummary))]
    public partial int TotalSelectedMessageCount { get; set; }

    [ObservableProperty]
    public partial int TotalMissingMessageCount { get; set; }

    [ObservableProperty]
    public partial string AccountName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AccountAddress { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyPropertyChangedFor(nameof(CanChangeIntelligencePreferences))]
    [NotifyPropertyChangedFor(nameof(IsEnabledContentVisible))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteLocalIntelligenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    public partial bool IsPageReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabledContentVisible))]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyPropertyChangedFor(nameof(CanChangeIntelligencePreferences))]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    public partial bool IsSemanticIndexingEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSemanticIndexingState))]
    [NotifyPropertyChangedFor(nameof(CanChangeIntelligencePreferences))]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteLocalIntelligenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelIndexingCommand))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    public partial bool IsCalculatingPlan { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpgradeEmbeddingProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelIndexingCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSemanticIndexCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteLocalIntelligenceCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditMessageRange))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(PlanCardTitle))]
    [NotifyPropertyChangedFor(nameof(PlanCardDescription))]
    [NotifyCanExecuteChangedFor(nameof(TranslateHeadlinesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAvailableIntelligenceCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeIntelligencePreferences))]
    public partial bool IsJobActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIndexingInProgress))]
    [NotifyPropertyChangedFor(nameof(IsStatusInfoBarVisible))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarTitle))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarMessage))]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarType))]
    public partial SemanticIndexJobStatus JobStatus { get; set; } = SemanticIndexJobStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowEverythingWarning))]
    [NotifyPropertyChangedFor(nameof(CanEditMessageRange))]
    public partial bool HasAvailableMessages { get; set; }

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
    [NotifyCanExecuteChangedFor(nameof(DeleteLocalIntelligenceCommand))]
    public partial bool HasLocalIndexData { get; set; }

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
    /// The account-level range that predates per-folder rules. It is no longer edited on this page —
    /// each folder carries its own rule — but the plan calculation still takes it as the fallback
    /// for a mailbox that has no rules stored yet.
    /// </summary>
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
    public bool CanChangeIntelligencePreferences => IsPageReady && !IsBusy && !IsJobActive;
    public bool CanEditMessageRange => HasAvailableMessages && !IsJobActive;
    public bool CanEditIntelligenceFolders => IsPageReady && !IsBusy && !IsJobActive;
    public bool HasSelectedIntelligenceFolders => IntelligenceFolderCoverageItems.Count > 0;
    public string SelectedIntelligenceFoldersDescription => HasSelectedIntelligenceFolders
        ? string.Join(", ", IntelligenceFolderCoverageItems.Select(item => item.DisplayName))
        : Translator.SemanticIndex_FoldersNoneSelected;

    /// <summary>
    /// The hero already carries the busy message and the healthy state, so the status
    /// bar is only raised for what the hero cannot say on its own.
    /// </summary>
    public bool IsIndexingInProgress => JobStatus is
        SemanticIndexJobStatus.Calculating or
        SemanticIndexJobStatus.Queued or
        SemanticIndexJobStatus.Indexing or
        SemanticIndexJobStatus.GeneratingInsights;
    public string StatusInfoBarTitle => IsIndexingInProgress ? Translator.SemanticIndex_IndexingInfoBarTitle : string.Empty;
    public string StatusInfoBarMessage => IsIndexingInProgress ? Translator.SemanticIndex_IndexingInfoBarMessage : StatusMessage;
    public InfoBarMessageType StatusInfoBarType => IsIndexingInProgress ? InfoBarMessageType.Information : StatusType;
    public bool IsStatusInfoBarVisible => IsPageReady && !IsBusy &&
        (IsIndexingInProgress ||
         (!string.IsNullOrWhiteSpace(StatusMessage) && StatusType != InfoBarMessageType.Success));
    /// <summary>
    /// True when every included folder is set to index its whole history. Each folder now carries
    /// its own rule, so this is the sum of those choices rather than one page-level range.
    /// </summary>
    public bool IsEverythingSelected => HasAvailableMessages &&
        IntelligenceFolderCoverageItems.Count > 0 &&
        IntelligenceFolderCoverageItems.All(item =>
            item.Rule.Mode == SemanticIndexCoverageMode.DateRange &&
            item.Rule.DatePreset == SemanticIndexRangePreset.Everything);

    /// <summary>
    /// The warning tells the user that "Everything" is a poor default, so it is only
    /// raised for a whole-mailbox selection that is also large enough to exhaust the quota.
    /// </summary>
    public bool ShouldShowEverythingWarning => IsSemanticIndexingEnabled &&
        IsEverythingSelected &&
        TotalSelectedMessageCount > LargeMailboxMessageThreshold &&
        EstimatedMissingMessageCount > 0;

    /// <summary>What the current rules add up to across every included folder.</summary>
    public string TotalCoverageSummary => string.Format(
        Translator.SemanticIndex_CoverageTotalSummary, TotalSelectedMessageCount, TotalAvailableMessageCount);

    partial void OnAutomaticallyIndexNewMessagesChanged(bool value)
    {
        if (!_isApplyingProfile && Account is not null)
            _ = SaveAutomaticIndexingPreferenceAsync(value);
    }

    partial void OnNewMessageModeIndexChanged(int value)
    {
        AutomaticallyIndexNewMessages = value == 0;
    }

    private async Task SaveAutomaticIndexingPreferenceAsync(bool value)
    {
        Account.Preferences.AutomaticallyIndexNewMessages = value;

        try
        {
            await _accountService.UpdateAccountPreferencesAsync(Account.Preferences).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Account.Preferences.AutomaticallyIndexNewMessages = !value;
            await ExecuteUIThread(() =>
            {
                _isApplyingProfile = true;
                AutomaticallyIndexNewMessages = !value;
                NewMessageModeIndex = AutomaticallyIndexNewMessages ? 0 : 1;
                _isApplyingProfile = false;
            });
            await ShowErrorAsync(exception);
        }
    }

    partial void OnIsPageReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditIntelligenceFolders));
        // The folder rows load before the page is marked ready, so everything gated on readiness
        // has to be re-evaluated here or the groups stay hidden for the life of the page.
        RefreshGroupStates();
        RefreshHeroState();
    }

    /// <summary>
    /// Re-evaluates which groups are shown and which are editable. These are computed from several
    /// flags at once, so every flag that feeds them ends up here rather than carrying its own list.
    /// </summary>
    private void RefreshGroupStates()
    {
        OnPropertyChanged(nameof(HasCoverageFolders));
        OnPropertyChanged(nameof(IsCoverageEmptyStateVisible));
        OnPropertyChanged(nameof(IsCoverageEditable));
        OnPropertyChanged(nameof(IsFeaturesGroupVisible));
        OnPropertyChanged(nameof(IsIndexedDataGroupVisible));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditIntelligenceFolders));
        RefreshGroupStates();
        RefreshHeroState();
    }

    partial void OnIsJobActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditIntelligenceFolders));
        RefreshGroupStates();
        RefreshHeroState();
    }
    partial void OnIsCalculatingPlanChanged(bool value) => RefreshHeroState();
    partial void OnIsSemanticIndexingEnabledChanged(bool value)
    {
        RefreshGroupStates();
        RefreshHeroState();
    }
    partial void OnHasAccountConsentChanged(bool value) => RefreshHeroState();
    partial void OnHasIndexDataChanged(bool value) => RefreshHeroState();
    partial void OnIsUpgradeRecommendedChanged(bool value) => RefreshHeroState();
    partial void OnHasErrorChanged(bool value) => RefreshHeroState();
    partial void OnEstimatedMissingMessageCountChanged(int value) => RefreshHeroState();

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        if (parameters is not Guid accountId)
            return;

        // Returning from the coverage editor. The page is cached, so this instance still holds the
        // inventory the editor worked from — applying its result in memory is both correct and
        // instant, and reloading here would put a progress ring in front of a finished decision.
        if (mode == NavigationMode.Back && IsPageReady &&
            _coverageHandoff.TryTake(out var coverageResult) &&
            coverageResult is not null && coverageResult.AccountId == accountId)
        {
            try
            {
                await ApplyCoverageResultAsync(coverageResult);
                if (IsSemanticIndexingEnabled)
                    await RecalculatePlanAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await ShowErrorAsync(exception);
            }
            return;
        }

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
                IsDailyBriefingEnabled = account.Preferences.IsDailyBriefingEnabled;
                ReplaceIntelligenceIndicatorSettings(account.Preferences.ExcludedIntelligenceIndicatorIds);

                // A job that started before this page opened is still running in the coordinator.
                // Seeding from it is what makes the progress card and its Cancel button appear on
                // arrival, instead of waiting for whatever snapshot the job broadcasts next.
                ApplySnapshot(_coordinator.GetJobSnapshot(accountId));
            });
            // The one mail read this page performs. It is deliberately before the consent check:
            // it is a local query, so the coverage editor is usable the moment consent is granted,
            // and nothing below this line queries mail again.
            await LoadCoverageAsync(account).ConfigureAwait(false);

            var hasSnapshot = await TryApplyCachedAccountSnapshotAsync(account).ConfigureAwait(false);
            if (!hasSnapshot && _snapshotService is not null)
            {
                await RefreshCachedAccountSnapshotAsync(account).ConfigureAwait(false);
                hasSnapshot = await TryApplyCachedAccountSnapshotAsync(account).ConfigureAwait(false);
                if (!hasSnapshot)
                {
                    await RefreshLocalIndexStateAsync().ConfigureAwait(false);
                    await ExecuteUIThread(() => IsPageReady = true);
                    return;
                }
            }

            if (hasSnapshot)
            {
                await RefreshLocalIndexStateAsync().ConfigureAwait(false);
                var currentState = await _coordinator.GetStateAsync(account.Id).ConfigureAwait(false);
                await ExecuteUIThread(() => ApplyState(currentState));
                await ExecuteUIThread(() => IsPageReady = true);
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
            var state = await _coordinator.GetStateAsync(account.Id).ConfigureAwait(false);
            await ExecuteUIThread(() => ApplyState(state));
            await RefreshHeadlineLanguageAsync().ConfigureAwait(false);
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

            Account.Preferences.IsSemanticIndexingEnabled = isEnabled;
            await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
            await ExecuteUIThread(() => IsSemanticIndexingEnabled = isEnabled);
            Messenger.Send(new WinoIntelligenceAccessChanged());
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
            await DownloadAvailableIntelligenceCoreAsync(showSuccess: false).ConfigureAwait(false);
            await RecalculatePlanAsync().ConfigureAwait(false);
        }
        else
            await ExecuteUIThread(() =>
            {
                EstimatedMissingMessageCount = TotalSelectedMessageCount;
                StatusMessage = Translator.SemanticIndex_DisabledCallout;
                StatusType = InfoBarMessageType.Information;
                RefreshHeroState();
            });
        return isEnabled;
    }

    private void ReplaceIntelligenceIndicatorSettings(IEnumerable<string>? excludedIndicatorIds)
    {
        IntelligenceIndicatorSettings.Clear();
        foreach (var item in IntelligenceIndicatorSettingsCatalog.Create(
            excludedIndicatorIds?.ToHashSet(StringComparer.Ordinal)))
        {
            IntelligenceIndicatorSettings.Add(item);
        }
    }

    public async Task<bool> SetDailyBriefingEnabledAsync(bool isEnabled)
    {
        if (!IsPageReady || Account is null || isEnabled == IsDailyBriefingEnabled)
            return IsDailyBriefingEnabled;

        var previous = IsDailyBriefingEnabled;
        try
        {
            Account.Preferences.IsDailyBriefingEnabled = isEnabled;
            await _accountService.UpdateAccountPreferencesAsync(Account.Preferences).ConfigureAwait(false);
            await ExecuteUIThread(() => IsDailyBriefingEnabled = isEnabled);
            return isEnabled;
        }
        catch (Exception exception)
        {
            Account.Preferences.IsDailyBriefingEnabled = previous;
            await ExecuteUIThread(() => IsDailyBriefingEnabled = previous);
            await ShowErrorAsync(exception);
            return previous;
        }
    }

    public async Task<bool> SetIntelligenceIndicatorVisibilityAsync(string indicatorId, bool isVisible)
    {
        if (!IsPageReady || Account is null || string.IsNullOrWhiteSpace(indicatorId))
            return isVisible;

        var previousExcluded = Account.Preferences.ExcludedIntelligenceIndicatorIds?.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var previousVisible = !previousExcluded.Contains(indicatorId);
        if (previousVisible == isVisible)
            return previousVisible;

        var updatedExcluded = previousExcluded.ToHashSet(StringComparer.Ordinal);
        if (isVisible)
            updatedExcluded.Remove(indicatorId);
        else
            updatedExcluded.Add(indicatorId);

        try
        {
            Account.Preferences.ExcludedIntelligenceIndicatorIds = updatedExcluded;
            await _accountService.UpdateAccountPreferencesAsync(Account.Preferences).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                var item = IntelligenceIndicatorSettings.FirstOrDefault(x => x.Identifier == indicatorId);
                if (item is not null)
                    item.IsVisible = isVisible;
            });
            return isVisible;
        }
        catch (Exception exception)
        {
            Account.Preferences.ExcludedIntelligenceIndicatorIds = previousExcluded;
            await ExecuteUIThread(() =>
            {
                var item = IntelligenceIndicatorSettings.FirstOrDefault(x => x.Identifier == indicatorId);
                if (item is not null)
                    item.IsVisible = previousVisible;
            });
            await ShowErrorAsync(exception);
            return previousVisible;
        }
    }

    [RelayCommand]
    private void OpenIntelligenceSettings()
        => Messenger.Send(new SettingsRootNavigationRequested(WinoPage.WinoIntelligencePage));

    [RelayCommand(CanExecute = nameof(CanStartIndexing))]
    private async Task StartIndexingAsync()
    {
        try
        {
            var selectedIds = _selectedRemoteMessageIds.ToArray();
            await _coordinator.StartIndexingAsync(
                Account.Id,
                selectedIds,
                notifyWhenCompleted: true).ConfigureAwait(false);
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
            IsQuotaAvailable = snapshot.Usage is not null;
            QuotaUsagePercentage = snapshot.Usage is null ? 0 : (double)snapshot.Usage.UsagePercentage;
            QuotaSummary = snapshot.Usage is null
                ? Translator.Intelligence_QuotaUnavailable
                : string.Format(
                    Translator.Intelligence_QuotaUsage,
                    snapshot.Usage.UsagePercentage,
                    snapshot.Usage.ResetsAtUtc is { } resetsAtUtc
                        ? resetsAtUtc.LocalDateTime.ToString("d MMMM")
                        : string.Empty);
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
                var currentConsent = result.Snapshot.Consent is { } consent && consent.Status == ConsentStatuses.Active &&
                    consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;
                await ExecuteUIThread(() =>
                {
                    SemanticMailboxId = mailbox?.MailboxId;
                    HasAccountConsent = currentConsent;
                    IsQuotaAvailable = result.Snapshot.Usage is not null;
                    QuotaUsagePercentage = result.Snapshot.Usage is null ? 0 : (double)result.Snapshot.Usage.UsagePercentage;
                    QuotaSummary = result.Snapshot.Usage is null
                        ? Translator.Intelligence_QuotaUnavailable
                        : string.Format(
                            Translator.Intelligence_QuotaUsage,
                            result.Snapshot.Usage.UsagePercentage,
                            result.Snapshot.Usage.ResetsAtUtc is { } resetsAtUtc
                                ? resetsAtUtc.LocalDateTime.ToString("d MMMM")
                                : string.Empty);
                    if (!string.IsNullOrWhiteSpace(result.Error))
                    {
                        RemoteRefreshError = string.Format(
                            Translator.WinoIntelligence_CachedRefreshFailed,
                            result.Snapshot.LastSuccessfulRefreshUtc?.LocalDateTime.ToString("g") ?? Translator.GeneralTitle_Info);
                    }
                });
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

    private bool CanCancelIndexing() => IsJobActive;

    [RelayCommand(CanExecute = nameof(CanDownloadAvailableIntelligence))]
    private async Task DownloadAvailableIntelligenceAsync()
        => await DownloadAvailableIntelligenceCoreAsync(showSuccess: true);

    private async Task DownloadAvailableIntelligenceCoreAsync(bool showSuccess)
    {
        if (Account is null)
            return;

        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationDownloading);
            var result = await _coordinator.DownloadAvailableIntelligenceAsync(Account.Id).ConfigureAwait(false);
            _coveredRemoteMessageIds = result.CoveredRemoteMessageIds.ToHashSet(StringComparer.Ordinal);
            await LoadCoverageAsync(Account).ConfigureAwait(false);
            var state = await _coordinator.GetStateAsync(Account.Id).ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                ApplyState(state);
                if (showSuccess)
                {
                    StatusMessage = string.Format(
                        Translator.SemanticIndex_CloudRestored,
                        result.CoveredRemoteMessageIds.Count);
                    StatusType = InfoBarMessageType.Success;
                }
            });
            await RefreshIndexedMessageCountAsync().ConfigureAwait(false);
            await RefreshLocalIndexStateAsync(updateCoverage: false).ConfigureAwait(false);
            await RefreshHeadlineLanguageAsync().ConfigureAwait(false);
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

    [RelayCommand(CanExecute = nameof(CanDeleteLocalIntelligence))]
    private async Task DeleteLocalIntelligenceAsync()
    {
        if (!await _dialogService.ShowConfirmationDialogAsync(
                Translator.SemanticIndex_DeleteLocalConfirmation,
                Translator.SemanticIndex_DeleteLocalTitle,
                Translator.Buttons_Delete))
            return;

        try
        {
            await SetBusyAsync(true, Translator.SemanticIndex_OperationDeleting);
            await _coordinator.DeleteLocalIndexAsync(Account.Id).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                HasLocalIndexData = false;
                StatusMessage = Translator.SemanticIndex_LocalDeleted;
                StatusType = InfoBarMessageType.Success;
                RefreshHeroState();
            });
            _coveredRemoteMessageIds.Clear();
            await ExecuteUIThread(RecomputeCoverage);
            Messenger.Send(new WinoIntelligenceAccessChanged());
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

    private bool CanDeleteLocalIntelligence()
        => IsPageReady && !IsBusy && !IsJobActive && HasLocalIndexData;

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
            var deletedMailboxId = SemanticMailboxId;
            await _coordinator.DeleteIndexAsync(Account.Id).ConfigureAwait(false);
            if (deletedMailboxId is { } mailboxId)
                await RemoveCachedMailboxHeadAsync(mailboxId).ConfigureAwait(false);
            Account.Preferences.IsSemanticIndexingEnabled = false;
            await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                IsSemanticIndexingEnabled = false;
                SemanticMailboxId = null;
                HasIndexData = false;
                HasLocalIndexData = false;
                _coveredRemoteMessageIds.Clear();
                IndexedMessageCount = 0;
                CoverageDescription = Translator.SemanticIndex_NoIndexedMessages;
                StatusMessage = Translator.SemanticIndex_DisabledCallout;
                RefreshHeroState();
            });
            await ExecuteUIThread(RecomputeCoverage);
            Messenger.Send(new WinoIntelligenceAccessChanged());
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
        if (message.Snapshot.Status is
            SemanticIndexJobStatus.Completed or
            SemanticIndexJobStatus.PausedForQuota or
            SemanticIndexJobStatus.Failed or
            SemanticIndexJobStatus.Cancelled)
        {
            _ = RefreshAfterJobAsync();
        }
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

    private Task RecalculatePlanAsync()
    {
        if (Account is null || !IsSemanticIndexingEnabled || IsJobActive)
            return Task.CompletedTask;

        return ExecuteUIThread(() =>
        {
            RecomputeCoverage();
            PlanSummary = TotalMissingMessageCount == 0
                ? Translator.SemanticIndex_PlanEmpty
                : string.Format(
                    Translator.SemanticIndex_PlanSummary,
                    TotalMissingMessageCount,
                    FormatDuration(TimeSpan.FromSeconds(TotalMissingMessageCount)));
            ProgressSummary = TotalMissingMessageCount == 0
                ? Translator.SemanticIndex_PlanEmpty
                : string.Format(
                    Translator.SemanticIndex_OverallProgress,
                    0,
                    TotalMissingMessageCount,
                    TotalMissingMessageCount);
        });
    }

    /// <param name="rebuildRows">
    /// False when the caller has already built the coverage rows itself, which is what returning
    /// from the coverage editor does — rebuilding them there would throw away its per-folder rules.
    /// </param>
    public async Task SetIntelligenceFolderSelectionAsync(
        IReadOnlyCollection<string> remoteFolderIds, bool rebuildRows = true)
    {
        if (Account is null || !CanEditIntelligenceFolders)
            return;

        var previousInitialized = Account.Preferences.IsIntelligenceFolderSelectionInitialized;
        var previousSelection = Account.Preferences.SelectedIntelligenceFolderIds.ToHashSet(StringComparer.Ordinal);
        var selected = remoteFolderIds.ToHashSet(StringComparer.Ordinal);
        Account.Preferences.IsIntelligenceFolderSelectionInitialized = true;
        Account.Preferences.SelectedIntelligenceFolderIds = selected;
        Account.Preferences.IsIntelligenceCoverageInitialized = true;
        Account.Preferences.IntelligenceFolderCoverageRules = [.. IntelligenceFolderCoverageItems.Select(item => item.Rule)];
        Account.Preferences.IntelligenceDefaultCoverageRule = DefaultCoverageRule;
        Account.Preferences.PrepareForStorage();
        OnPropertyChanged(nameof(HasSelectedIntelligenceFolders));
        OnPropertyChanged(nameof(SelectedIntelligenceFoldersDescription));

        try
        {
            await _accountService.UpdateAccountPreferencesAsync(Account.Preferences).ConfigureAwait(false);
            // The inventory covers the whole account, so a folder that was just included already
            // has its counts in memory. Rebuilding the rows is all that is needed here.
            if (rebuildRows)
                await LoadIntelligenceFolderSelectionsAsync(Account).ConfigureAwait(false);
            if (IsSemanticIndexingEnabled)
                await RecalculatePlanAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // This runs after an await, so it is on a background thread. The rollback puts the
            // checkboxes back, which is bound state, so it has to go through the dispatcher.
            Account.Preferences.IsIntelligenceFolderSelectionInitialized = previousInitialized;
            Account.Preferences.SelectedIntelligenceFolderIds = previousSelection;
            await ExecuteUIThread(() =>
            {
                OnPropertyChanged(nameof(HasSelectedIntelligenceFolders));
                OnPropertyChanged(nameof(SelectedIntelligenceFoldersDescription));
            });
            await ShowErrorAsync(exception);
        }
    }

    /// <summary>
    /// Opens the coverage editor, handing it everything this page has already read so it needs no
    /// I/O of its own.
    /// </summary>
    [RelayCommand]
    private void OpenCoverageEditor()
    {
        if (Account is null || _inventory is null || !CanEditIntelligenceFolders)
            return;

        var args = new IntelligenceCoverageEditorArgs(
            Account.Id,
            _folders,
            _inventory,
            _coveredRemoteMessageIds,
            IncludedFolderIds(),
            (SemanticIndexFolderCoverageRule[])[.. IntelligenceFolderCoverageItems.Select(item => item.Rule)],
            DefaultCoverageRule);

        Messenger.Send(new BreadcrumbNavigationRequested(
            Translator.SemanticIndex_CoverageEditorTitle,
            WinoPage.IntelligenceCoveragePage,
            args));
    }

    /// <summary>
    /// Applies what the coverage editor decided, without re-reading anything.
    /// </summary>
    /// <remarks>
    /// The editor computed its rows from the very inventory this page still holds, so the rows can
    /// be rebuilt in memory. Re-running the load here would put a progress ring and a mail query in
    /// front of a decision the user has already made.
    /// </remarks>
    private async Task ApplyCoverageResultAsync(IntelligenceCoverageResult result)
    {
        var rulesByFolderId = result.Rules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.RemoteFolderId))
            .GroupBy(static rule => rule.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var displayNames = GetIntelligenceFolderDisplayNames(_folders);

        await ExecuteUIThread(() =>
        {
            DefaultCoverageRule = result.DefaultRule with { RemoteFolderId = string.Empty };
            IntelligenceFolderCoverageItems.Clear();
            foreach (var remoteFolderId in result.IncludedRemoteFolderIds)
            {
                IntelligenceFolderCoverageItems.Add(new IntelligenceFolderCoverageItem(
                    remoteFolderId,
                    displayNames.GetValueOrDefault(remoteFolderId, remoteFolderId),
                    rulesByFolderId.TryGetValue(remoteFolderId, out var rule)
                        ? rule
                        : DefaultCoverageRule with { RemoteFolderId = remoteFolderId }));
            }

            RecomputeCoverage();
            OnPropertyChanged(nameof(HasCoverageFolders));
            OnPropertyChanged(nameof(IsCoverageEmptyStateVisible));
        });

        await SetIntelligenceFolderSelectionAsync(result.IncludedRemoteFolderIds, rebuildRows: false).ConfigureAwait(false);
    }

    private bool CanStartIndexing()
        => IsPageReady && IsSemanticIndexingEnabled && !IsBusy && !IsCalculatingPlan && !IsJobActive &&
           _selectedRemoteMessageIds.Count > 0;

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
            var manifest = await _apiClient.GetWinoIntelligenceManifestAsync().ConfigureAwait(false);
            await _apiClient.BeginIntelligenceReindexAsync(
                SemanticMailboxId.Value,
                new BeginIntelligenceReindexRequest(manifest.LatestIntelligenceVersion, Guid.NewGuid())).ConfigureAwait(false);
            await _coordinator.StartIndexingAsync(
                Account.Id,
                _selectedRemoteMessageIds.ToArray(),
                notifyWhenCompleted: true).ConfigureAwait(false);
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
        HasIndexData = state.LocalIndexedMessageCount > 0 || state.ServerHead?.IndexedMessageCount > 0;
        IndexedMessageCount = checked((int)Math.Min(
            Math.Max(state.LocalIndexedMessageCount, state.ServerHead?.IndexedMessageCount ?? 0),
            int.MaxValue));
        IsUpgradeRecommended = state.ServerHead is { IntelligenceVersion: not WinoIntelligenceVersions.V1 };
        _recommendedProfileId = state.ServerHead?.IntelligenceVersion ?? string.Empty;
        ApplyProfile(null);
        StartButtonText = Translator.SemanticIndex_StartButton;
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

    private void ApplyProfile(IntelligenceIndexingProfileDto? profile)
    {
        if (Account is null)
            return;
        _isApplyingProfile = true;
        try
        {
            AutomaticallyIndexNewMessages = Account?.Preferences.AutomaticallyIndexNewMessages ?? true;
            NewMessageModeIndex = AutomaticallyIndexNewMessages ? 0 : 1;
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
        ProgressValue = snapshot.ProcessedMessageCount;
        ProgressMaximum = Math.Max(1, snapshot.SelectedMessageCount);
        ProgressText = string.Format(
            Translator.SemanticIndex_EmbeddingProgress,
            snapshot.SucceededMessageCount,
            snapshot.SelectedMessageCount,
            snapshot.FailedMessageCount);
        MetadataProgressValue = snapshot.SucceededMessageCount;
        MetadataProgressText = string.Format(
            Translator.SemanticIndex_MetadataProgress,
            snapshot.SucceededMessageCount,
            snapshot.SelectedMessageCount,
            snapshot.FailedMessageCount);
        var remainingMessageCount = Math.Max(snapshot.SelectedMessageCount - snapshot.ProcessedMessageCount, 0);
        ProgressSummary = snapshot.SelectedMessageCount == 0
            ? Translator.SemanticIndex_PlanEmpty
            : string.Format(
                Translator.SemanticIndex_OverallProgress,
                snapshot.ProcessedMessageCount,
                snapshot.SelectedMessageCount,
                remainingMessageCount);
        if (snapshot.Status == SemanticIndexJobStatus.PausedForQuota)
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

    /// <remarks>
    /// Deliberately local-only where coverage is concerned. Indexing writes each artifact to the
    /// local store as it goes, so reading that store back is enough to know what is now covered.
    /// This used to reconcile the entire mailbox against the server instead, which walked every
    /// message in the account rather than the ones the job touched — and because a reconcile
    /// publishes its own job snapshots, finishing or cancelling one job immediately looked like a
    /// second, much larger one starting. Cancelling in particular restarted the very work the user
    /// had just stopped.
    /// </remarks>
    private async Task RefreshAfterJobAsync()
    {
        try
        {
            var state = await _coordinator.GetStateAsync(Account.Id).ConfigureAwait(false);
            await ExecuteUIThread(() => ApplyState(state));
            await RefreshLocalIndexStateAsync().ConfigureAwait(false);
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
            _ = mailboxId;
            var state = await _coordinator.GetStateAsync(Account.Id).ConfigureAwait(false);
            await ExecuteUIThread(() => ApplyState(state));
            await RefreshHeadlineLanguageAsync().ConfigureAwait(false);
        }
        catch
        {
            // Enabling or accepting consent already succeeded. A status refresh is
            // best-effort and the next page refresh will retry it.
        }
    }

    private string CreateCoverageDescription(SemanticIndexAccountState state)
    {
        if (state.ServerHead is null || state.ServerHead.IndexedMessageCount == 0)
            return Translator.SemanticIndex_NoIndexedMessages;
        var count = string.Format(Translator.SemanticIndex_IndexedCount, state.ServerHead.IndexedMessageCount);
        var size = string.Format(Translator.SemanticIndex_StorageSize, FormatStorageSize(state.ServerHead.StorageSizeBytes));
        if (state.ServerHead.OldestAnalyzedMessageUtc is null || state.ServerHead.NewestAnalyzedMessageUtc is null)
            return $"{count}\n{size}";
        return string.Format(
            Translator.SemanticIndex_CoverageRangeWithSize,
            count,
            state.ServerHead.OldestAnalyzedMessageUtc.Value.LocalDateTime.ToString("d MMMM yyyy"),
            state.ServerHead.NewestAnalyzedMessageUtc.Value.LocalDateTime.ToString("d MMMM yyyy"),
            size);
    }

    private async Task RefreshHeadlineLanguageAsync()
    {
        if (Account is null || SemanticMailboxId is null) return;
        var language = await _localStore.GetHeadlineLanguageAsync(Account.Id).ConfigureAwait(false) ?? string.Empty;
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
    /// <summary>
    /// Reads the mailbox once and builds every folder row from it.
    /// </summary>
    /// <remarks>
    /// Opening this page during the account's first synchronization can catch the folder table
    /// mid-write and read nothing, which would leave the page claiming the mailbox has no folders
    /// for as long as it stays open. An empty read is never a legitimate steady state for an
    /// account that has mail, so it — and only it — is retried once before giving up.
    /// </remarks>
    private async Task LoadCoverageAsync(MailAccount account)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(CoverageReloadRetryDelay).ConfigureAwait(false);

            _inventory = await _messageContextResolver.GetCoverageInventoryAsync(account.Id).ConfigureAwait(false);

            // The loaded count comes back as a return value rather than being read off the bound
            // collections: those belong to the UI thread, and this method runs on a background one.
            if (await LoadIntelligenceFolderSelectionsAsync(account).ConfigureAwait(false) > 0)
                return;
        }
    }

    /// <returns>How many selectable folders the mailbox reported.</returns>
    private async Task<int> LoadIntelligenceFolderSelectionsAsync(MailAccount account)
    {
        var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);
        _folders = folders;
        var selected = account.Preferences.IsIntelligenceFolderSelectionInitialized
            ? account.Preferences.SelectedIntelligenceFolderIds
            : folders.Where(folder => folder.SpecialFolderType == SpecialFolderType.Inbox)
                .Select(folder => folder.RemoteFolderId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
        var displayNames = GetIntelligenceFolderDisplayNames(folders);
        var inventory = _inventory ?? IntelligenceCoverageInventory.Empty(account.Id);
        var items = folders.Where(IntelligenceFolderFilter.IsSelectable)
            .OrderBy(folder => displayNames[folder.RemoteFolderId], StringComparer.CurrentCultureIgnoreCase)
            .Select(folder => folder.RemoteFolderId)
            .ToArray();

        // Rules survive navigation now, so a returning user sees what they configured rather than
        // the hundred-newest default every time.
        var defaultRule = account.Preferences.IsIntelligenceCoverageInitialized
            ? account.Preferences.IntelligenceDefaultCoverageRule
            : SemanticIndexFolderCoverageRule.Latest(
                string.Empty,
                MailAccountPreferences.DefaultLatestMessageCount);
        var storedRules = account.Preferences.IntelligenceFolderCoverageRules
            .Where(static rule => !string.IsNullOrWhiteSpace(rule.RemoteFolderId))
            .GroupBy(static rule => rule.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var restoredFolders = inventory.KnownRemoteFolderIds
            .Where(folderId => inventory.GetFolderIndices(folderId).Any(index =>
                _coveredRemoteMessageIds.Contains(inventory.RemoteMessageIds[index])))
            .ToHashSet(StringComparer.Ordinal);
        var coverageItems = items
            .Where(remoteFolderId => selected.Contains(remoteFolderId) || restoredFolders.Contains(remoteFolderId))
            .Select(remoteFolderId => new IntelligenceFolderCoverageItem(
                remoteFolderId,
                displayNames[remoteFolderId],
                !selected.Contains(remoteFolderId)
                    ? SemanticIndexFolderCoverageRule.Latest(remoteFolderId, 0)
                    : storedRules.TryGetValue(remoteFolderId, out var storedRule)
                        ? storedRule
                        : defaultRule with { RemoteFolderId = remoteFolderId }))
            .ToArray();

        // Everything above this line is plain data on a background thread. Everything below touches
        // bound state, so all of it — not just the collections — happens on the UI thread.
        await ExecuteUIThread(() =>
        {
            DefaultCoverageRule = defaultRule;
            IntelligenceFolderCoverageItems.Clear();
            foreach (var item in coverageItems) IntelligenceFolderCoverageItems.Add(item);

            RecomputeCoverage();
            OnPropertyChanged(nameof(HasSelectedIntelligenceFolders));
            OnPropertyChanged(nameof(SelectedIntelligenceFoldersDescription));
            OnPropertyChanged(nameof(HasCoverageFolders));
            OnPropertyChanged(nameof(IsCoverageEmptyStateVisible));
        });

        return items.Length;
    }

    private static IReadOnlyDictionary<string, string> GetIntelligenceFolderDisplayNames(
        IReadOnlyCollection<MailItemFolder> folders)
    {
        var foldersByRemoteId = folders.Where(folder => !string.IsNullOrWhiteSpace(folder.RemoteFolderId))
            .GroupBy(folder => folder.RemoteFolderId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var duplicateNames = foldersByRemoteId.Values.GroupBy(folder => folder.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return foldersByRemoteId.Values.ToDictionary(folder => folder.RemoteFolderId,
            folder => duplicateNames.Contains(folder.FolderName)
                ? GetIntelligenceFolderDisplayPath(folder, foldersByRemoteId)
                : folder.FolderName,
            StringComparer.Ordinal);
    }

    private static string GetIntelligenceFolderDisplayPath(MailItemFolder folder,
                                                            IReadOnlyDictionary<string, MailItemFolder> foldersByRemoteId)
    {
        var path = new Stack<string>();
        var visitedRemoteFolderIds = new HashSet<string>(StringComparer.Ordinal);
        MailItemFolder? current = folder;

        while (current is not null && visitedRemoteFolderIds.Add(current.RemoteFolderId))
        {
            path.Push(current.FolderName);
            current = !string.IsNullOrWhiteSpace(current.ParentRemoteFolderId) &&
                foldersByRemoteId.TryGetValue(current.ParentRemoteFolderId, out var parent)
                ? parent
                : null;
        }

        return string.Join(" / ", path);
    }

    #region Coverage

    /// <summary>
    /// Recomputes every number the coverage list shows, from the inventory read once at navigation.
    /// Pure in-memory work, so it runs inline on the UI thread whenever a rule changes.
    /// </summary>
    private void RecomputeCoverage()
    {
        var inventory = _inventory;
        HasAvailableMessages = inventory is { TotalMessageCount: > 0 };

        if (inventory is null)
        {
            TotalAvailableMessageCount = 0;
            TotalSelectedMessageCount = 0;
            TotalMissingMessageCount = 0;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var rules = IntelligenceFolderCoverageItems.Select(item => item.Rule).ToArray();
        var selection = IntelligenceCoverageCalculator.Resolve(inventory, rules, now);

        // Paired by folder id rather than by position: the calculator skips a rule that carries no
        // folder id, so the two lists are not guaranteed to line up, and an index mismatch here
        // would throw inside a dispatcher callback and abandon the whole load.
        var resolvedFolders = new Dictionary<string, IntelligenceFolderSelectionResult>(StringComparer.Ordinal);
        foreach (var folder in selection.Folders)
            resolvedFolders[folder.RemoteFolderId] = folder;

        foreach (var item in IntelligenceFolderCoverageItems)
        {
            var stats = IntelligenceCoverageCalculator.GetFolderStats(inventory, item.RemoteFolderId);
            item.AvailableMessageCount = stats.AvailableMessageCount;

            if (resolvedFolders.TryGetValue(item.RemoteFolderId, out var folder))
            {
                item.SelectedMessageCount = folder.SelectedMessageCount;
                var folderIndices = inventory.GetFolderIndices(item.RemoteFolderId);
                var covered = 0;
                for (var position = folder.SliceStart; position < folder.SliceEnd; position++)
                {
                    if (_coveredRemoteMessageIds.Contains(inventory.RemoteMessageIds[folderIndices[position]]))
                        covered++;
                }

                item.CoveredMessageCount = covered;
            }
            else
            {
                item.SelectedMessageCount = 0;
                item.CoveredMessageCount = 0;
            }
        }

        TotalAvailableMessageCount = IntelligenceCoverageCalculator.CountDistinct(inventory, IncludedFolderIds());
        TotalSelectedMessageCount = selection.DistinctSelectedCount;
        _selectedRemoteMessageIds = selection.ToRemoteMessageIds().ToHashSet(StringComparer.Ordinal);
        var missing = _selectedRemoteMessageIds.Count(id => !_coveredRemoteMessageIds.Contains(id));
        TotalMissingMessageCount = missing;
        EstimatedMissingMessageCount = missing;

        OnPropertyChanged(nameof(IsEverythingSelected));
        OnPropertyChanged(nameof(ShouldShowEverythingWarning));
        StartIndexingCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlySet<string> IncludedFolderIds()
        => IntelligenceFolderCoverageItems.Select(item => item.RemoteFolderId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Stops indexing one folder. Inclusion belongs to the folder picker, so this does exactly what
    /// clearing the folder there does, without making the user reopen the dialog.
    /// </summary>
    [RelayCommand]
    private async Task RemoveCoverageFolderAsync(IntelligenceFolderCoverageItem? item)
    {
        if (item is null || !CanEditIntelligenceFolders)
            return;

        var remaining = IntelligenceFolderCoverageItems
            .Where(row => !string.Equals(row.RemoteFolderId, item.RemoteFolderId, StringComparison.Ordinal))
            .Select(row => row.RemoteFolderId)
            .ToArray();
        await SetIntelligenceFolderSelectionAsync(remaining).ConfigureAwait(false);
    }

    #endregion

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
    /// Reconciles the local and server counts after an operation that changed coverage.
    /// Best effort: the cached mailbox status remains usable without it.
    /// </summary>
    private async Task RefreshIndexedMessageCountAsync()
    {
        if (Account is null)
            return;

        try
        {
            var state = await _coordinator.GetStateAsync(Account.Id).ConfigureAwait(false);
            var serverVectorCount = state?.ServerHead?.IndexedMessageCount ?? 0;
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

    private async Task RefreshLocalIndexStateAsync(bool updateCoverage = true)
    {
        if (Account is null || _inventory is null)
            return;

        try
        {
            var documents = await _localStore.GetCurrentDocumentsAsync(
                Account.Id,
                _inventory.RemoteMessageIds).ConfigureAwait(false);
            var coveredIds = documents.Keys.ToHashSet(StringComparer.Ordinal);

            await ExecuteUIThread(() =>
            {
                HasLocalIndexData = coveredIds.Count > 0;
                if (updateCoverage)
                    _coveredRemoteMessageIds = coveredIds;
                RecomputeCoverage();
            });
        }
        catch
        {
            await ExecuteUIThread(() => HasLocalIndexData = false);
        }
    }

    private async Task RemoveCachedMailboxHeadAsync(Guid mailboxId)
    {
        if (_profileService is null || _snapshotService is null)
            return;

        try
        {
            var winoAccount = await _profileService.GetActiveAccountAsync().ConfigureAwait(false);
            if (winoAccount is null)
                return;

            var snapshot = await _snapshotService.GetCachedAsync(winoAccount.Id).ConfigureAwait(false);
            if (snapshot is null)
                return;

            var statuses = new Dictionary<Guid, IntelligenceMailboxStatusDto>(snapshot.MailboxStatuses);
            statuses.Remove(mailboxId);
            var heads = new Dictionary<Guid, MailboxIntelligenceHeadDto>(snapshot.MailboxHeads);
            heads.Remove(mailboxId);
            var now = DateTimeOffset.UtcNow;
            await _snapshotService.SaveAsync(snapshot with
            {
                MailboxStatuses = statuses,
                MailboxHeads = heads,
                HeadsUpdatedAtUtc = now,
                LastSuccessfulRefreshUtc = now,
            }).ConfigureAwait(false);
        }
        catch
        {
            // The server deletion already succeeded. Cache cleanup is best effort.
        }
    }

    #endregion


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
