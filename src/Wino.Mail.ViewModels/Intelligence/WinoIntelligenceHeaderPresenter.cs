#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Intelligence;

/// <summary>
/// Orchestrates the Wino Intelligence header of the reading pane: the intelligence context of the
/// rendered message, the coordinator snapshot, summary, processing and translation requests.
/// The platform header binds to the state below and forwards user actions to the commands.
/// </summary>
/// <remarks>
/// Every asynchronous result is checked against the current content key, so a late response for
/// a previous message never reaches the header. Call <see cref="Attach"/> while the reader is on
/// screen and <see cref="Detach"/> when it leaves.
/// </remarks>
public sealed partial class WinoIntelligenceHeaderPresenter : ObservableObject, IDisposable,
    IRecipient<MailIntelligenceJobChanged>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IRecipient<WinoIntelligenceEntitlementChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<IntelligenceVisibilityChanged>
{
    private readonly IWinoIntelligenceCoordinator _intelligenceCoordinator;
    private readonly IMailService _mailService;
    private readonly IClipboardService _clipboardService;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly IMailContentProjector _contentProjector;
    private readonly IDispatcher _dispatcher;
    private readonly HashSet<Guid> _liveFeatureRequestIds = [];

    private string _currentRenderedHtml = string.Empty;
    private string _subject = string.Empty;
    private string _sender = string.Empty;
    private DateTime _creationDate;
    private MailContentProjectionResult? _translationProjection;
    private MailContentProjection? _inferenceProjection;
    private IReadOnlyDictionary<string, string>? _translationMap;
    private MailItemViewModel? _currentMailItem;
    private WinoIntelligenceContext? _intelligenceContext;
    private WinoIntelligenceSnapshot? _intelligenceSnapshot;
    private CancellationTokenSource? _intelligenceContextCancellation;
    private Guid? _translationRequestId;
    private Guid? _summaryRequestId;
    private bool _isAttached;
    private bool _isDisposed;

    public WinoIntelligenceHeaderPresenter(IWinoIntelligenceCoordinator intelligenceCoordinator,
                                           IMailService mailService,
                                           IClipboardService clipboardService,
                                           IPreferencesService preferencesService,
                                           IMailDialogService dialogService,
                                           IMailContentProjector contentProjector,
                                           IDispatcher dispatcher)
    {
        _intelligenceCoordinator = intelligenceCoordinator;
        _mailService = mailService;
        _clipboardService = clipboardService;
        _preferencesService = preferencesService;
        _dialogService = dialogService;
        _contentProjector = contentProjector;
        _dispatcher = dispatcher;

        TranslationLanguages = new[]
        {
            new WinoIntelligenceLanguageOption(string.Empty, Translator.WinoIntelligence_DetectLanguage),
        }.Concat(AiActionCatalog.GetTranslateLanguageOptions()
            .Select(x => new WinoIntelligenceLanguageOption(x.Code, x.Label))).ToArray();
        SelectedSourceLanguage = string.Empty;
        SelectedTargetLanguage = _preferencesService.AiDefaultTranslationLanguageCode;
    }

    #region State

    /// <summary>Whether the header is shown. Starts hidden until the access snapshot allows it.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <summary>Identifies the message the header state belongs to. A new key resets feature state.</summary>
    [ObservableProperty]
    public partial string ContentKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestProcessing))]
    public partial WinoIntelligenceProcessingState ProcessingState { get; set; } = WinoIntelligenceProcessingState.NotProcessed;

    [ObservableProperty]
    public partial bool IsSummaryAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsTranslateAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestProcessing))]
    public partial bool IsProcessingAvailable { get; set; }

    /// <summary>Briefing headline of the message, empty when hidden by the indicator settings.</summary>
    [ObservableProperty]
    public partial string BriefingFactText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeadlineText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeadlineDetailText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VerificationCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WinoIntelligenceFeatureState SummaryState { get; set; } = WinoIntelligenceFeatureState.Idle;

    /// <summary>Localized passive metadata tiles of the current message.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<WinoIntelligenceTile>? IntelligenceTiles { get; set; }

    /// <summary>"Detect" followed by the supported translation languages.</summary>
    public IReadOnlyList<WinoIntelligenceLanguageOption> TranslationLanguages { get; }

    /// <summary>Source language code; empty means detect.</summary>
    [ObservableProperty]
    public partial string SelectedSourceLanguage { get; set; }

    [ObservableProperty]
    public partial string SelectedTargetLanguage { get; set; }

    [ObservableProperty]
    public partial bool IsTranslationBusy { get; set; }

    [ObservableProperty]
    public partial bool HasTranslationResult { get; set; }

    [ObservableProperty]
    public partial bool IsTranslationApplied { get; set; }

    [ObservableProperty]
    public partial string TranslationStatusText { get; set; } = string.Empty;

    /// <summary>Whether the reader shows the translated body rather than the original.</summary>
    [ObservableProperty]
    public partial bool IsShowingTranslation { get; set; }

    /// <summary>Mirrors the header's process button: processing can be requested now.</summary>
    public bool CanRequestProcessing => IsProcessingAvailable
        && ProcessingState is WinoIntelligenceProcessingState.NotProcessed or WinoIntelligenceProcessingState.Failed;

    #endregion

    #region Events

    /// <summary>
    /// Raised when the body must be rendered again, for example after a translation is applied or
    /// removed. The host renders <see cref="ResolveActiveHtml"/> and the returned task completes
    /// once the body is shown.
    /// </summary>
    public event Func<Task>? RerenderRequested;

    /// <summary>Raised when the summary request with the given id produced its text.</summary>
    public event Action<Guid, string>? SummaryCompleted;

    /// <summary>Raised when the summary request with the given id failed or was abandoned.</summary>
    public event Action<Guid>? SummaryFailed;

    #endregion

    #region Lifetime

    /// <summary>Starts receiving intelligence messages.</summary>
    public void Attach()
    {
        if (_isAttached || _isDisposed)
            return;

        _isAttached = true;
        WeakReferenceMessenger.Default.Register<MailIntelligenceJobChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
        WeakReferenceMessenger.Default.Register<IntelligenceMetadataChanged>(this);
        WeakReferenceMessenger.Default.Register<IntelligenceVisibilityChanged>(this);
    }

    /// <summary>Stops receiving intelligence messages.</summary>
    public void Detach()
    {
        if (!_isAttached)
            return;

        _isAttached = false;
        WeakReferenceMessenger.Default.Unregister<MailIntelligenceJobChanged>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceEntitlementChanged>(this);
        WeakReferenceMessenger.Default.Unregister<IntelligenceMetadataChanged>(this);
        WeakReferenceMessenger.Default.Unregister<IntelligenceVisibilityChanged>(this);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        Detach();
        ClearContext();
        _currentMailItem = null;
        _isDisposed = true;
    }

    #endregion

    #region Inputs

    /// <summary>
    /// Starts a new message: abandons the previous context and shows the passive state of
    /// <paramref name="mailItem"/> while its access snapshot loads.
    /// </summary>
    public void BeginMailItem(MailItemViewModel? mailItem)
    {
        ClearContext();
        _currentMailItem = mailItem;
        ShowIntelligenceHeaderImmediately(mailItem);
    }

    /// <summary>Forgets the current message without touching the header, for example when the reader goes idle.</summary>
    public void ForgetMailItem() => _currentMailItem = null;

    /// <summary>
    /// Records the HTML that is about to be rendered and prepares its projections. Any previous
    /// translation belongs to other content and is dropped.
    /// </summary>
    public void SetRenderedContent(string? html)
    {
        _currentRenderedHtml = html ?? string.Empty;
        _translationProjection = _contentProjector.Project(_currentRenderedHtml, MailContentProjectionProfile.Translation);
        _inferenceProjection = _contentProjector.Project(_currentRenderedHtml, MailContentProjectionProfile.Inference)?.Projection;
        _translationMap = null;
        IsShowingTranslation = false;
    }

    /// <summary>
    /// Builds the intelligence context of the rendered message and loads its snapshot. Calls
    /// <see cref="SetRenderedContent"/> first when <paramref name="html"/> was not recorded yet.
    /// </summary>
    public Task OnHtmlRendered(string? html, string? subject, string? from, DateTime created)
    {
        if (!string.Equals(html ?? string.Empty, _currentRenderedHtml, StringComparison.Ordinal))
            SetRenderedContent(html);

        _subject = subject ?? string.Empty;
        _sender = from ?? string.Empty;
        _creationDate = created;
        return LoadIntelligenceContextAsync();
    }

    /// <summary>Drops the rendered content and any translation of it.</summary>
    public void ResetContent()
    {
        _currentRenderedHtml = string.Empty;
        _translationProjection = null;
        _inferenceProjection = null;
        _translationMap = null;
        IsShowingTranslation = false;
    }

    /// <summary>Returns the translated body while a translation is shown, otherwise <paramref name="html"/>.</summary>
    public string ResolveActiveHtml(string html)
        => IsShowingTranslation && _translationProjection is not null && _translationMap is not null
            ? _translationProjection.ApplyTranslations(_translationMap)
            : html;

    /// <summary>Loads the coordinator snapshot of the current context again.</summary>
    public async Task RefreshSnapshotAsync()
    {
        var context = _intelligenceContext;
        var cancellation = _intelligenceContextCancellation;
        if (context is null || cancellation is null || cancellation.IsCancellationRequested)
            return;
        try
        {
            var snapshot = await _intelligenceCoordinator.GetSnapshotAsync(context, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(context, _intelligenceContext))
                return;
            _intelligenceSnapshot = snapshot;
            ApplyIntelligenceSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Rebuilds localized tiles and reloads the snapshot after the app language changed.</summary>
    public void OnLanguageChanged()
    {
        _ = _dispatcher.ExecuteOnUIThread(async () =>
        {
            _currentMailItem?.RefreshIntelligenceTiles();
            IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
            await RefreshSnapshotAsync();
        });
    }

    #endregion

    #region Commands

    /// <summary>Requests a summary with a new request id.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RequestSummaryAsync() => RunSummaryAsync(Guid.NewGuid());

    /// <summary>Cancels the active summary request.</summary>
    [RelayCommand]
    public void CancelSummary()
    {
        if (_summaryRequestId is { } requestId)
            CancelSummaryRequest(requestId);
    }

    /// <summary>
    /// Runs the summary request <paramref name="requestId"/>. Hosts whose header correlates its own
    /// request ids call this directly and complete the header from <see cref="SummaryCompleted"/>
    /// and <see cref="SummaryFailed"/>.
    /// </summary>
    public async Task RunSummaryAsync(Guid requestId)
    {
        _summaryRequestId = requestId;
        SummaryState = WinoIntelligenceFeatureState.Busy;
        SummaryText = string.Empty;

        var context = _intelligenceContext;
        if (context is null)
        {
            FailSummaryRequest(requestId);
            return;
        }
        _liveFeatureRequestIds.Add(requestId);
        var result = await _intelligenceCoordinator.SummarizeAsync(context, requestId);

        _liveFeatureRequestIds.Remove(requestId);
        if (result is null || !IsCurrent(result.ContentKey) || result.IsCanceled)
            return;
        if (!result.IsSuccess)
        {
            ReportFeatureFailure(requestId, result.Error);
            return;
        }
        CompleteSummary(requestId, result.Value ?? string.Empty);
    }

    /// <summary>Cancels the summary request <paramref name="requestId"/>; the summary returns to idle.</summary>
    public void CancelSummaryRequest(Guid requestId)
    {
        if (_summaryRequestId == requestId)
        {
            _summaryRequestId = null;
            SummaryState = WinoIntelligenceFeatureState.Idle;
        }
        _liveFeatureRequestIds.Remove(requestId);
        _intelligenceCoordinator.CancelRequest(requestId);
    }

    /// <summary>Queues the current message for processing.</summary>
    [RelayCommand]
    public async Task RequestProcessingAsync()
    {
        var context = _intelligenceContext;
        if (context is null || !CanRequestProcessing)
            return;
        try
        {
            ProcessingState = WinoIntelligenceProcessingState.Processing;
            await _intelligenceCoordinator.RequestProcessingAsync(context);
            if (!IsCurrent(context.ContentKey))
                return;
            ProcessingState = WinoIntelligenceProcessingState.Processed;
            await RefreshSnapshotAsync();
        }
        catch (Exception exception)
        {
            if (!IsCurrent(context.ContentKey))
                return;
            ProcessingState = WinoIntelligenceProcessingState.Failed;
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Translate(exception.Message), InfoBarMessageType.Error);
        }
    }

    /// <summary>
    /// Translates the message into <see cref="SelectedTargetLanguage"/>. With a translation
    /// available this toggles between the original and the translated body instead.
    /// </summary>
    [RelayCommand]
    public async Task TranslateAsync()
    {
        var context = _intelligenceContext;
        if (context is null)
            return;
        try
        {
            await TranslateCurrentMessageAsync(context);
        }
        catch (Exception exception)
        {
            if (IsCurrent(context.ContentKey))
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Translate(exception.Message), InfoBarMessageType.Error);
        }
    }

    /// <summary>Cancels a running translation.</summary>
    [RelayCommand]
    public void CancelTranslation()
    {
        if (_translationRequestId is not { } requestId)
            return;
        _intelligenceCoordinator.CancelRequest(requestId);
        _translationRequestId = null;
        IsTranslationBusy = false;
        TranslationStatusText = Translator.WinoIntelligence_TranslationCanceled;
    }

    /// <summary>Copies the verification code of the message, when there is one.</summary>
    [RelayCommand]
    public async Task CopyVerificationCodeAsync()
    {
        if (!string.IsNullOrWhiteSpace(VerificationCode))
            (await _clipboardService.CopyTextAsync(VerificationCode)).ThrowIfNotSucceeded();
    }

    #endregion

    #region Context

    private async Task LoadIntelligenceContextAsync()
    {
        var mailCopy = _currentMailItem?.MailCopy;
        var account = mailCopy?.AssignedAccount;
        if (mailCopy is null || account is null || mailCopy.IsDraft || string.IsNullOrWhiteSpace(_currentRenderedHtml))
        {
            IsVisible = false;
            return;
        }

        ShowIntelligenceHeaderImmediately(_currentMailItem);

        var contentKey = $"{account.Id:N}:{mailCopy.UniqueId:N}";
        if (_intelligenceContext is null || !string.Equals(_intelligenceContext.ContentKey, contentKey, StringComparison.Ordinal))
        {
            ClearContext();
            ContentKey = contentKey;
            _intelligenceContextCancellation = new CancellationTokenSource();
            _intelligenceContext = new WinoIntelligenceContext(
                contentKey,
                account.Id,
                mailCopy.UniqueId,
                mailCopy.FileId,
                mailCopy.Id,
                account.Address,
                account.ProviderType,
                account.Preferences?.IsSemanticIndexingEnabled == true,
                _subject,
                _sender,
                ToUtc(_creationDate),
                _currentRenderedHtml,
                _inferenceProjection,
                _translationProjection?.Projection,
                mailCopy.IntelligenceMetadata);
        }
        else
        {
            // Metadata can arrive after the reader context was created. Keep the same cancellation
            // scope, but refresh the immutable context so the snapshot sees imported artifacts.
            _intelligenceContext = _intelligenceContext with
            {
                IsSemanticIndexingEnabled = account.Preferences?.IsSemanticIndexingEnabled == true,
                Html = _currentRenderedHtml,
                InferenceProjection = _inferenceProjection,
                TranslationProjection = _translationProjection?.Projection,
                IntelligenceMetadata = mailCopy.IntelligenceMetadata,
            };
        }

        await RefreshSnapshotAsync();
    }

    private void ShowIntelligenceHeaderImmediately(MailItemViewModel? mailItem)
    {
        // Eligibility is resolved asynchronously. Start hidden so ineligible accounts never
        // see an Intelligence header flash while the shared access snapshot is loading.
        IsVisible = false;
        IntelligenceTiles = mailItem?.IntelligenceTiles;
        if (mailItem?.MailCopy is not { IsDraft: false } mailCopy)
            return;

        if (mailCopy.IntelligenceMetadata is { } metadata)
            ApplyPassiveIntelligenceMetadata(metadata);
        else
            ClearPassiveIntelligenceMetadata();

        IsSummaryAvailable = false;
        IsTranslateAvailable = false;
        IsProcessingAvailable = false;
        ProcessingState = WinoIntelligenceProcessingState.NotProcessed;
    }

    private void ApplyIntelligenceSnapshot(WinoIntelligenceSnapshot snapshot)
    {
        IsSummaryAvailable = snapshot.IsSummaryAvailable;
        IsTranslateAvailable = snapshot.IsTranslateAvailable;
        IsProcessingAvailable = snapshot.IsProcessingAvailable;
        ProcessingState = MapProcessingState(snapshot.ProcessingState);
        if (_currentMailItem?.MailCopy.IntelligenceMetadata is { } metadata)
            ApplyPassiveIntelligenceMetadata(metadata);
        else
        {
            BriefingFactText = string.Empty;
            DeadlineText = string.Empty;
            DeadlineDetailText = string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(snapshot.CachedSummary))
            SummaryText = snapshot.CachedSummary;
        IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
        IsVisible = snapshot.IsVisible;
    }

    private void ApplyPassiveIntelligenceMetadata(MailIntelligenceMetadata metadata)
    {
        var excludedIndicators = _currentMailItem?.MailCopy?.AssignedAccount?.Preferences?
            .ExcludedIntelligenceIndicatorIds;
        var showBriefing = IntelligenceVisibilityPolicy.IsVisible(excludedIndicators, IntelligenceFactKind.Briefing);

        BriefingFactText = showBriefing ? metadata.Headline : string.Empty;
        DeadlineText = string.Empty;
        DeadlineDetailText = string.Empty;
        VerificationCode = string.Empty;
    }

    private void ClearPassiveIntelligenceMetadata()
    {
        BriefingFactText = string.Empty;
        DeadlineText = string.Empty;
        DeadlineDetailText = string.Empty;
        VerificationCode = string.Empty;
    }

    /// <summary>Cancels every request of the current context and hides the header.</summary>
    public void ClearContext()
    {
        if (_translationRequestId is { } translationRequestId)
            _intelligenceCoordinator.CancelRequest(translationRequestId);
        _translationRequestId = null;
        IsTranslationBusy = false;
        HasTranslationResult = false;
        IsTranslationApplied = false;
        TranslationStatusText = string.Empty;
        _intelligenceContextCancellation?.Cancel();
        _intelligenceContextCancellation?.Dispose();
        _intelligenceContextCancellation = null;
        if (_intelligenceContext is { } context)
            _intelligenceCoordinator.CancelContext(context.ContentKey);
        foreach (var requestId in _liveFeatureRequestIds.ToArray())
        {
            _intelligenceCoordinator.CancelRequest(requestId);
            FailSummaryRequest(requestId);
        }
        _liveFeatureRequestIds.Clear();
        _intelligenceContext = null;
        _intelligenceSnapshot = null;
        IsVisible = false;
        VerificationCode = string.Empty;
    }

    #endregion

    #region Features

    private async Task TranslateCurrentMessageAsync(WinoIntelligenceContext context)
    {
        if (IsShowingTranslation)
        {
            IsShowingTranslation = false;
            IsTranslationApplied = false;
            await RequestRerenderAsync();
            return;
        }

        if (_translationMap is not null)
        {
            IsShowingTranslation = true;
            IsTranslationApplied = true;
            await RequestRerenderAsync();
            return;
        }

        var requestId = Guid.NewGuid();
        _translationRequestId = requestId;
        IsTranslationBusy = true;
        TranslationStatusText = Translator.WinoIntelligence_Translating;
        var sourceLanguage = string.IsNullOrWhiteSpace(SelectedSourceLanguage)
            ? null
            : SelectedSourceLanguage;
        var targetLanguage = SelectedTargetLanguage;
        _preferencesService.AiDefaultTranslationLanguageCode = targetLanguage;
        WinoIntelligenceOperationResult<MailTranslationResult> result;
        try
        {
            result = await _intelligenceCoordinator.TranslateAsync(
                context,
                requestId,
                sourceLanguage,
                targetLanguage);
        }
        finally
        {
            if (_translationRequestId == requestId)
            {
                _translationRequestId = null;
                IsTranslationBusy = false;
            }
        }
        if (!IsCurrent(result.ContentKey) || result.IsCanceled)
            return;
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Error);
        if (result.Value is null)
            throw new InvalidOperationException("Translation response was empty.");
        _translationMap = result.Value.Translations.ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
        IsShowingTranslation = true;
        HasTranslationResult = true;
        IsTranslationApplied = true;
        TranslationStatusText = $"{result.Value.DetectedSourceLanguage} → {targetLanguage}";
        await RequestRerenderAsync();
    }

    private async Task RequestRerenderAsync()
    {
        if (RerenderRequested is { } rerender)
            await rerender();
    }

    private void CompleteSummary(Guid requestId, string summaryText)
    {
        if (_summaryRequestId == requestId)
        {
            _summaryRequestId = null;
            SummaryText = summaryText;
            SummaryState = string.IsNullOrWhiteSpace(SummaryText) ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        }
        SummaryCompleted?.Invoke(requestId, summaryText);
    }

    /// <returns><see langword="true"/> when <paramref name="requestId"/> was the active summary request.</returns>
    private bool FailSummaryRequest(Guid requestId)
    {
        var wasActive = _summaryRequestId == requestId;
        if (wasActive)
        {
            _summaryRequestId = null;
            SummaryState = WinoIntelligenceFeatureState.Idle;
        }
        SummaryFailed?.Invoke(requestId);
        return wasActive;
    }

    private void ReportFeatureFailure(Guid requestId, string? error)
    {
        if (FailSummaryRequest(requestId))
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, error ?? Translator.WinoIntelligence_ActionFailed, InfoBarMessageType.Error);
    }

    private bool IsCurrent(string contentKey)
        => _intelligenceContext is { } context && string.Equals(context.ContentKey, contentKey, StringComparison.Ordinal);

    private static WinoIntelligenceProcessingState MapProcessingState(MailMessageIntelligenceState state) => state switch
    {
        MailMessageIntelligenceState.NotProcessed => WinoIntelligenceProcessingState.NotProcessed,
        MailMessageIntelligenceState.Queued => WinoIntelligenceProcessingState.Queued,
        MailMessageIntelligenceState.Processing => WinoIntelligenceProcessingState.Processing,
        MailMessageIntelligenceState.Processed => WinoIntelligenceProcessingState.Processed,
        MailMessageIntelligenceState.Failed => WinoIntelligenceProcessingState.Failed,
        _ => WinoIntelligenceProcessingState.Unavailable,
    };

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    // A new content key belongs to another message: the header returns to its initial feature
    // state, like the Windows header control does when its ContentKey changes.
    partial void OnContentKeyChanged(string value)
    {
        _summaryRequestId = null;
        SummaryState = WinoIntelligenceFeatureState.Idle;
        SummaryText = string.Empty;
        IsTranslationBusy = false;
        HasTranslationResult = false;
        IsTranslationApplied = false;
        TranslationStatusText = string.Empty;
    }

    // A summary text set outside a request (a cached summary) is a finished summary.
    partial void OnSummaryTextChanged(string value)
    {
        if (_summaryRequestId is null)
            SummaryState = string.IsNullOrWhiteSpace(value) ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
    }

    #endregion

    #region Messages

    void IRecipient<MailIntelligenceJobChanged>.Receive(MailIntelligenceJobChanged message)
    {
        if (_intelligenceContext?.LocalAccountId != message.AccountId)
            return;

        _ = _dispatcher.ExecuteOnUIThread(async () => await RefreshSnapshotAsync());
    }

    void IRecipient<WinoIntelligenceAccessChanged>.Receive(WinoIntelligenceAccessChanged message)
    {
        _intelligenceCoordinator.InvalidateAccess();
        _ = _dispatcher.ExecuteOnUIThread(async () => await RefreshSnapshotAsync());
    }

    void IRecipient<WinoIntelligenceEntitlementChanged>.Receive(WinoIntelligenceEntitlementChanged message)
    {
        _ = _dispatcher.ExecuteOnUIThread(async () =>
        {
            if (!message.Entitlement.CanAccessSurfaces)
            {
                _intelligenceCoordinator.CancelContext(_intelligenceContext?.ContentKey ?? string.Empty);
                IsVisible = false;
                return;
            }

            await RefreshSnapshotAsync();
        });
    }

    void IRecipient<IntelligenceMetadataChanged>.Receive(IntelligenceMetadataChanged message)
    {
        var mail = _currentMailItem?.MailCopy;
        if (mail?.AssignedAccount is null)
            return;

        var remoteId = RemoteMessageIdentity.TryCreate(mail);
        var matches = message.Scope == IntelligenceMetadataChangeScope.DatabaseReset ||
            (message.LocalAccountId == mail.AssignedAccount.Id &&
             (message.Scope == IntelligenceMetadataChangeScope.MailboxReset ||
              (remoteId is not null && message.RemoteMessageIds.Contains(remoteId))));
        if (matches)
            _ = _dispatcher.ExecuteOnUIThread(async () => await RefreshCurrentIntelligenceMetadataAsync(message.Scope));
    }

    void IRecipient<IntelligenceVisibilityChanged>.Receive(IntelligenceVisibilityChanged message)
    {
        var mailItem = _currentMailItem;
        if (mailItem is null || mailItem.MailCopy?.AssignedAccount?.Id != message.LocalAccountId)
            return;

        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            mailItem.ApplyIntelligenceVisibility(message.ExcludedIndicatorIds);
            if (!ReferenceEquals(_currentMailItem, mailItem))
                return;

            IntelligenceTiles = mailItem.IntelligenceTiles;
            if (mailItem.MailCopy.IntelligenceMetadata is { } metadata)
                ApplyPassiveIntelligenceMetadata(metadata);
        });
    }

    private async Task RefreshCurrentIntelligenceMetadataAsync(IntelligenceMetadataChangeScope scope)
    {
        var mailItem = _currentMailItem;
        if (mailItem?.MailCopy is null)
            return;

        if (scope == IntelligenceMetadataChangeScope.Messages)
            await _mailService.HydrateIntelligenceMetadataAsync(new[] { mailItem.MailCopy });
        else
            mailItem.MailCopy.IntelligenceMetadata = null;

        mailItem.UpdateFrom(mailItem.MailCopy, MailCopyChangeFlags.IntelligenceMetadata);
        ShowIntelligenceHeaderImmediately(mailItem);
        await LoadIntelligenceContextAsync();
    }

    #endregion
}
