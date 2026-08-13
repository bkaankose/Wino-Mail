using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.IntelligenceProgressRing;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.ViewManagement;

namespace Wino.Mail.Controls.IntelligenceHeader;

/// <summary>
/// Expandable intelligence surface for a rendered mail. The host owns all data, localization,
/// service calls, formatting and request cancellation; this control owns presentation and
/// correlated feature state only.
/// </summary>
public sealed partial class WinoIntelligenceHeader : Control
{
    private readonly UISettings _uiSettings = new();
    private readonly ObservableCollection<WinoIntelligenceReply> _replies = [];
    private readonly ObservableCollection<WinoIntelligenceSimilarMailItem> _similarItems = [];

    private Guid? _summaryRequestId;
    private Guid? _repliesRequestId;
    private Guid? _similarRequestId;
    private WinoIntelligenceFeature? _openPanel;
    private bool _isSynchronizingCollections;
    private bool _isSynchronizingLanguages;

    private Grid? _layoutRoot;
    private Button? _headerToggleButton;
    private FrameworkElement? _headerContentRoot;
    private TextBlock? _titleTextBlock;
    private TextBlock? _subtitleTextBlock;
    private FrameworkElement? _deadlinePill;
    private TextBlock? _deadlinePillText;
    private FrameworkElement? _needsReplyPill;
    private TextBlock? _needsReplyPillText;
    private Button? _processButton;
    private FrameworkElement? _processingStatusPanel;
    private WinoIntelligenceProgressRing? _processingProgressRing;
    private TextBlock? _processingStatusTextBlock;
    private FontIcon? _chevronIcon;
    private FrameworkElement? _bodyRoot;
    private FrameworkElement? _factsPanel;
    private FrameworkElement? _deadlineFactCard;
    private TextBlock? _deadlineFactTextBlock;
    private Button? _addToCalendarButton;
    private FrameworkElement? _needsReplyFactCard;
    private TextBlock? _needsReplyFactTextBlock;
    private FrameworkElement? _insightsLockedPanel;
    private TextBlock? _insightsLockedTextBlock;

    private FeatureParts? _summaryParts;
    private FeatureParts? _repliesParts;
    private FeatureParts? _translateParts;
    private FeatureParts? _similarParts;
    private FrameworkElement? _panelHost;
    private FrameworkElement? _summaryPanel;
    private FrameworkElement? _repliesPanel;
    private FrameworkElement? _translatePanel;
    private FrameworkElement? _similarPanel;
    private FrameworkElement? _summaryWaitingPanel;
    private FrameworkElement? _repliesWaitingPanel;
    private FrameworkElement? _similarWaitingPanel;
    private WinoIntelligenceProgressRing? _summaryWaitingRing;
    private WinoIntelligenceProgressRing? _repliesWaitingRing;
    private WinoIntelligenceProgressRing? _similarWaitingRing;
    private TextBlock? _summaryResultTextBlock;
    private ListView? _suggestedRepliesList;
    private ListView? _similarMailList;
    private ComboBox? _translationSourceComboBox;
    private ComboBox? _translationTargetComboBox;
    private Button? _translationRunButton;
    private TextBlock? _translationStatusTextBlock;
    private TextBlock? _translationBusyTextBlock;
    private FrameworkElement? _translationAppliedPanel;
    private FrameworkElement? _translationWaitingPanel;
    private WinoIntelligenceProgressRing? _translationWaitingRing;
    private Button? _summaryCopyButton;
    private Button? _summaryRegenerateButton;
    private Button? _repliesRegenerateButton;
    private Button? _similarRegenerateButton;
    private Button? _summaryCloseButton;
    private Button? _repliesCloseButton;
    private Button? _translateCloseButton;
    private Button? _similarCloseButton;

    /// <summary>Gets or sets the <c>ContentKey</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ContentKey { get; set; }

    /// <summary>Gets or sets the <c>ProcessingState</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = WinoIntelligenceProcessingState.NotProcessed)]
    public partial WinoIntelligenceProcessingState ProcessingState { get; set; }

    /// <summary>Gets or sets the <c>IsExpanded</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsExpanded { get; set; }

    /// <summary>Gets or sets the <c>HeaderTitle</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Wino Intelligence")]
    public partial string HeaderTitle { get; set; }

    /// <summary>Gets or sets the <c>ActionHeadingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Ask Wino")]
    public partial string ActionHeadingText { get; set; }

    /// <summary>Gets or sets the <c>DeadlineText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string DeadlineText { get; set; }

    /// <summary>Gets or sets the <c>DeadlineDetailText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string DeadlineDetailText { get; set; }

    /// <summary>Gets or sets the <c>NeedsReply</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool NeedsReply { get; set; }

    /// <summary>Gets or sets the <c>NeedsReplyText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Needs reply")]
    public partial string NeedsReplyText { get; set; }

    /// <summary>Gets or sets the <c>NeedsReplyDetailText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string NeedsReplyDetailText { get; set; }

    /// <summary>Gets or sets the <c>NoSignalSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Summarize, translate, suggest replies")]
    public partial string NoSignalSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>UnprocessedSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Not processed")]
    public partial string UnprocessedSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>QueuedSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Waiting to be processed…")]
    public partial string QueuedSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>ProcessingSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Processing…")]
    public partial string ProcessingSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>ProcessingFailedSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Could not be processed")]
    public partial string ProcessingFailedSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>UnavailableSubtitleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Not available for this message")]
    public partial string UnavailableSubtitleText { get; set; }

    /// <summary>Gets or sets the <c>ProcessButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Process")]
    public partial string ProcessButtonText { get; set; }

    /// <summary>Gets or sets the <c>RetryProcessButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Try again")]
    public partial string RetryProcessButtonText { get; set; }

    /// <summary>Gets or sets the <c>InsightsLockedText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Process this message to get deadlines, reply status and suggested replies.")]
    public partial string InsightsLockedText { get; set; }

    /// <summary>Gets or sets the <c>SummaryText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SummaryText { get; set; }

    /// <summary>Gets or sets the <c>SuggestedReplies</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<WinoIntelligenceReply>? SuggestedReplies { get; set; }

    /// <summary>Gets or sets the <c>SimilarMailItems</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<WinoIntelligenceSimilarMailItem>? SimilarMailItems { get; set; }

    /// <summary>Gets or sets the <c>IsAddToCalendarAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsAddToCalendarAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsProcessingAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsProcessingAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsSummaryAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSummaryAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsSuggestedRepliesAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsSuggestedRepliesAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsTranslateAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsTranslateAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsFindSimilarMailAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsFindSimilarMailAvailable { get; set; }

    /// <summary>Gets or sets the <c>ProcessAutomationText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Process this message with Wino Intelligence")]
    public partial string ProcessAutomationText { get; set; }

    /// <summary>Gets or sets the <c>SummarizeButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Summarize")]
    public partial string SummarizeButtonText { get; set; }

    /// <summary>Gets or sets the <c>SummarizingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Summarizing…")]
    public partial string SummarizingText { get; set; }

    /// <summary>Gets or sets the <c>CancelButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Cancel")]
    public partial string CancelButtonText { get; set; }

    /// <summary>Gets or sets the <c>SummaryHeadingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Summary")]
    public partial string SummaryHeadingText { get; set; }

    /// <summary>Gets or sets the <c>SuggestRepliesButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Draft replies")]
    public partial string SuggestRepliesButtonText { get; set; }

    /// <summary>Gets or sets the <c>DraftingRepliesText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Drafting replies…")]
    public partial string DraftingRepliesText { get; set; }

    /// <summary>Gets or sets the <c>SuggestedRepliesHeadingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Suggested replies")]
    public partial string SuggestedRepliesHeadingText { get; set; }

    /// <summary>Gets or sets the <c>SimilarMailHeadingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Similar messages")]
    public partial string SimilarMailHeadingText { get; set; }

    /// <summary>Gets or sets the <c>TranslatePanelHeadingText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Translate message")]
    public partial string TranslatePanelHeadingText { get; set; }

    /// <summary>Gets or sets the <c>TranslateTargetHintFormat</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "to {0}")]
    public partial string TranslateTargetHintFormat { get; set; }

    /// <summary>Gets or sets the <c>CopyText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Copy")]
    public partial string CopyText { get; set; }

    /// <summary>Gets or sets the <c>RegenerateText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Regenerate")]
    public partial string RegenerateText { get; set; }

    /// <summary>Gets or sets the <c>ClosePanelText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Close")]
    public partial string ClosePanelText { get; set; }

    /// <summary>Gets or sets the <c>AddToCalendarButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Add to calendar")]
    public partial string AddToCalendarButtonText { get; set; }

    /// <summary>Gets or sets the <c>TranslateButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Translate")]
    public partial string TranslateButtonText { get; set; }

    /// <summary>Gets or sets the <c>TranslationCancelButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Cancel")]
    public partial string TranslationCancelButtonText { get; set; }

    /// <summary>Gets or sets the <c>ShowOriginalButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Show original")]
    public partial string ShowOriginalButtonText { get; set; }

    /// <summary>Gets or sets the <c>TranslateAgainButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Translate again")]
    public partial string TranslateAgainButtonText { get; set; }

    /// <summary>Gets or sets the <c>SourceLanguageLabel</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Source language")]
    public partial string SourceLanguageLabel { get; set; }

    /// <summary>Gets or sets the <c>TargetLanguageLabel</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Target language")]
    public partial string TargetLanguageLabel { get; set; }

    /// <summary>Gets or sets the <c>TranslationLanguages</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<WinoIntelligenceLanguageOption>? TranslationLanguages { get; set; }

    /// <summary>Gets or sets the <c>SelectedSourceLanguage</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SelectedSourceLanguage { get; set; }

    /// <summary>Gets or sets the <c>SelectedTargetLanguage</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "en-US")]
    public partial string SelectedTargetLanguage { get; set; }

    /// <summary>Gets or sets the <c>IsTranslationBusy</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsTranslationBusy { get; set; }

    /// <summary>Gets or sets the <c>HasTranslationResult</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool HasTranslationResult { get; set; }

    /// <summary>Gets or sets the <c>IsTranslationApplied</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsTranslationApplied { get; set; }

    /// <summary>Gets or sets the <c>TranslationStatusText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string TranslationStatusText { get; set; }

    /// <summary>Gets or sets the <c>FindSimilarButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Find similar")]
    public partial string FindSimilarButtonText { get; set; }

    /// <summary>Gets or sets the <c>ExpandedText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Expanded")]
    public partial string ExpandedText { get; set; }

    /// <summary>Gets or sets the <c>CollapsedText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Collapsed")]
    public partial string CollapsedText { get; set; }

    /// <summary>Initializes a new instance of the <see cref="WinoIntelligenceHeader"/> class.</summary>
    public WinoIntelligenceHeader()
    {
        DefaultStyleKey = typeof(WinoIntelligenceHeader);
    }

    /// <summary>Occurs when the user requests an intelligence feature.</summary>
    public event EventHandler<WinoIntelligenceRequestEventArgs>? FeatureRequested;

    /// <summary>Occurs when the user cancels an active feature request.</summary>
    public event EventHandler<WinoIntelligenceCancelRequestedEventArgs>? FeatureCancelRequested;

    /// <summary>Occurs when the user invokes a host-owned intelligence action.</summary>
    public event EventHandler<WinoIntelligenceActionEventArgs>? ActionInvoked;

    /// <summary>Occurs when the user chooses a suggested reply.</summary>
    public event EventHandler<WinoIntelligenceReplyChosenEventArgs>? SuggestedReplyChosen;

    /// <summary>Occurs when the user chooses a similar mail item.</summary>
    public event EventHandler<WinoIntelligenceSimilarMailChosenEventArgs>? SimilarMailChosen;

    /// <summary>Occurs when the expanded state changes.</summary>
    public event EventHandler<bool>? ExpansionChanged;

    /// <summary>Occurs when the current message should be processed.</summary>
    public event EventHandler? ProcessRequested;

    /// <summary>Gets the current summary request state.</summary>
    public WinoIntelligenceFeatureState SummaryState { get; private set; } = WinoIntelligenceFeatureState.Idle;

    /// <summary>Gets the current suggested-replies request state.</summary>
    public WinoIntelligenceFeatureState SuggestedRepliesState { get; private set; } = WinoIntelligenceFeatureState.Idle;

    /// <summary>Gets the current similar-mail request state.</summary>
    public WinoIntelligenceFeatureState SimilarMailState { get; private set; } = WinoIntelligenceFeatureState.Idle;

    /// <summary>Gets a value indicating whether any intelligence request is active.</summary>
    public bool IsBusy => SummaryState == WinoIntelligenceFeatureState.Busy
                          || SuggestedRepliesState == WinoIntelligenceFeatureState.Busy
                          || SimilarMailState == WinoIntelligenceFeatureState.Busy
                          || IsTranslationBusy;

    private bool HasInsights => IsProcessingAvailable && ProcessingState == WinoIntelligenceProcessingState.Processed;
    private bool CanRequestProcessing => IsProcessingAvailable && ProcessingState is WinoIntelligenceProcessingState.NotProcessed or WinoIntelligenceProcessingState.Failed;
    private bool IsProcessingRunning => IsProcessingAvailable && ProcessingState is WinoIntelligenceProcessingState.Queued or WinoIntelligenceProcessingState.Processing;
    private bool CanExpand => IsSummaryAvailable || IsTranslateAvailable || CanRequestProcessing
        || (HasInsights && (NeedsReply || !string.IsNullOrWhiteSpace(DeadlineText)
            || IsSuggestedRepliesAvailable || IsFindSimilarMailAvailable));

    protected override void OnApplyTemplate()
    {
        DetachTemplateHandlers();
        base.OnApplyTemplate();

        _layoutRoot = GetTemplateChild(PartLayoutRootName) as Grid;
        _headerToggleButton = GetTemplateChild(PartHeaderToggleButtonName) as Button;
        _headerContentRoot = GetTemplateChild(PartHeaderContentRootName) as FrameworkElement;
        _titleTextBlock = GetTemplateChild(PartTitleTextBlockName) as TextBlock;
        _subtitleTextBlock = GetTemplateChild(PartSubtitleTextBlockName) as TextBlock;
        _deadlinePill = GetTemplateChild(PartDeadlinePillName) as FrameworkElement;
        _deadlinePillText = GetTemplateChild(PartDeadlinePillTextName) as TextBlock;
        _needsReplyPill = GetTemplateChild(PartNeedsReplyPillName) as FrameworkElement;
        _needsReplyPillText = GetTemplateChild(PartNeedsReplyPillTextName) as TextBlock;
        _processButton = GetTemplateChild(PartProcessButtonName) as Button;
        _processingStatusPanel = GetTemplateChild(PartProcessingStatusPanelName) as FrameworkElement;
        _processingProgressRing = GetTemplateChild(PartProcessingProgressRingName) as WinoIntelligenceProgressRing;
        _processingStatusTextBlock = GetTemplateChild(PartProcessingStatusTextBlockName) as TextBlock;
        _chevronIcon = GetTemplateChild(PartChevronIconName) as FontIcon;
        _bodyRoot = GetTemplateChild(PartBodyRootName) as FrameworkElement;
        _factsPanel = GetTemplateChild(PartFactsPanelName) as FrameworkElement;
        _deadlineFactCard = GetTemplateChild(PartDeadlineFactCardName) as FrameworkElement;
        _deadlineFactTextBlock = GetTemplateChild(PartDeadlineFactTextBlockName) as TextBlock;
        _addToCalendarButton = GetTemplateChild(PartAddToCalendarButtonName) as Button;
        _needsReplyFactCard = GetTemplateChild(PartNeedsReplyFactCardName) as FrameworkElement;
        _needsReplyFactTextBlock = GetTemplateChild(PartNeedsReplyFactTextBlockName) as TextBlock;
        _insightsLockedPanel = GetTemplateChild(PartInsightsLockedPanelName) as FrameworkElement;
        _insightsLockedTextBlock = GetTemplateChild(PartInsightsLockedTextBlockName) as TextBlock;

        _summaryParts = GetFeatureParts(PartSummaryChipButtonName, PartSummaryCancelButtonName, PartSummaryChipLabelName, PartSummaryChipHintName, PartSummaryResultDotName);
        _repliesParts = GetFeatureParts(PartRepliesChipButtonName, PartRepliesCancelButtonName, PartRepliesChipLabelName, PartRepliesChipHintName, PartRepliesResultDotName);
        _translateParts = GetFeatureParts(PartTranslateChipButtonName, PartTranslateCancelButtonName, PartTranslateChipLabelName, PartTranslateChipHintName, PartTranslateResultDotName);
        _similarParts = GetFeatureParts(PartSimilarChipButtonName, PartSimilarCancelButtonName, PartSimilarChipLabelName, PartSimilarChipHintName, PartSimilarResultDotName);
        _panelHost = GetTemplateChild(PartPanelHostName) as FrameworkElement;
        _summaryPanel = GetTemplateChild(PartSummaryPanelName) as FrameworkElement;
        _repliesPanel = GetTemplateChild(PartRepliesPanelName) as FrameworkElement;
        _translatePanel = GetTemplateChild(PartTranslatePanelName) as FrameworkElement;
        _similarPanel = GetTemplateChild(PartSimilarPanelName) as FrameworkElement;
        _summaryWaitingPanel = GetTemplateChild(PartSummaryWaitingPanelName) as FrameworkElement;
        _repliesWaitingPanel = GetTemplateChild(PartRepliesWaitingPanelName) as FrameworkElement;
        _similarWaitingPanel = GetTemplateChild(PartSimilarWaitingPanelName) as FrameworkElement;
        _summaryWaitingRing = GetTemplateChild(PartSummaryWaitingRingName) as WinoIntelligenceProgressRing;
        _repliesWaitingRing = GetTemplateChild(PartRepliesWaitingRingName) as WinoIntelligenceProgressRing;
        _similarWaitingRing = GetTemplateChild(PartSimilarWaitingRingName) as WinoIntelligenceProgressRing;
        _summaryResultTextBlock = GetTemplateChild(PartSummaryResultTextBlockName) as TextBlock;
        _suggestedRepliesList = GetTemplateChild(PartSuggestedRepliesListName) as ListView;
        _similarMailList = GetTemplateChild(PartSimilarMailListName) as ListView;
        _translationSourceComboBox = GetTemplateChild(PartTranslationSourceComboBoxName) as ComboBox;
        _translationTargetComboBox = GetTemplateChild(PartTranslationTargetComboBoxName) as ComboBox;
        _translationRunButton = GetTemplateChild(PartTranslationRunButtonName) as Button;
        _translationStatusTextBlock = GetTemplateChild(PartTranslationStatusTextBlockName) as TextBlock;
        _translationBusyTextBlock = GetTemplateChild(PartTranslationBusyTextBlockName) as TextBlock;
        _translationAppliedPanel = GetTemplateChild(PartTranslationAppliedPanelName) as FrameworkElement;
        _translationWaitingPanel = GetTemplateChild(PartTranslationWaitingPanelName) as FrameworkElement;
        _translationWaitingRing = GetTemplateChild(PartTranslationWaitingRingName) as WinoIntelligenceProgressRing;
        _summaryCopyButton = GetTemplateChild(PartSummaryCopyButtonName) as Button;
        _summaryRegenerateButton = GetTemplateChild(PartSummaryRegenerateButtonName) as Button;
        _repliesRegenerateButton = GetTemplateChild(PartRepliesRegenerateButtonName) as Button;
        _similarRegenerateButton = GetTemplateChild(PartSimilarRegenerateButtonName) as Button;
        _summaryCloseButton = GetTemplateChild(PartSummaryCloseButtonName) as Button;
        _repliesCloseButton = GetTemplateChild(PartRepliesCloseButtonName) as Button;
        _translateCloseButton = GetTemplateChild(PartTranslateCloseButtonName) as Button;
        _similarCloseButton = GetTemplateChild(PartSimilarCloseButtonName) as Button;

        if (_suggestedRepliesList is not null) _suggestedRepliesList.ItemsSource = _replies;
        if (_similarMailList is not null) _similarMailList.ItemsSource = _similarItems;
        AttachTemplateHandlers();
        RefreshCollections();
        SyncAll(animateExpansion: false);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WinoIntelligenceHeaderAutomationPeer(this);

    // The cancel button lives in the feature's detail panel, not on the chip: the panel is where
    // progress is shown, so it is also where the request is called off.
    private FeatureParts GetFeatureParts(string mainButtonName, string cancelButtonName, string labelName, string hintName, string resultDotName) => new(
        GetTemplateChild(mainButtonName) as Button,
        GetTemplateChild(cancelButtonName) as Button,
        GetTemplateChild(labelName) as TextBlock,
        GetTemplateChild(hintName) as TextBlock,
        GetTemplateChild(resultDotName) as FrameworkElement);

    private void AttachTemplateHandlers()
    {
        if (_headerToggleButton is not null) _headerToggleButton.Click += OnHeaderToggleClicked;
        if (_processButton is not null) _processButton.Click += OnProcessClicked;
        AttachFeatureHandlers(_summaryParts, OnSummaryClicked, OnSummaryCancelClicked);
        AttachFeatureHandlers(_repliesParts, OnRepliesClicked, OnRepliesCancelClicked);
        AttachFeatureHandlers(_translateParts, OnTranslateChipClicked, OnTranslateCancelClicked);
        AttachFeatureHandlers(_similarParts, OnSimilarClicked, OnSimilarCancelClicked);
        if (_suggestedRepliesList is not null) _suggestedRepliesList.ItemClick += OnSuggestedReplyItemClick;
        if (_similarMailList is not null) _similarMailList.ItemClick += OnSimilarMailItemClick;
        if (_addToCalendarButton is not null) _addToCalendarButton.Click += OnAddToCalendarClicked;
        if (_translationSourceComboBox is not null) _translationSourceComboBox.SelectionChanged += OnTranslationSourceChanged;
        if (_translationTargetComboBox is not null) _translationTargetComboBox.SelectionChanged += OnTranslationTargetChanged;
        if (_translationRunButton is not null) _translationRunButton.Click += OnTranslationRunClicked;
        if (_summaryCopyButton is not null) _summaryCopyButton.Click += OnSummaryCopyClicked;
        if (_summaryRegenerateButton is not null) _summaryRegenerateButton.Click += OnSummaryRegenerateClicked;
        if (_repliesRegenerateButton is not null) _repliesRegenerateButton.Click += OnRepliesRegenerateClicked;
        if (_similarRegenerateButton is not null) _similarRegenerateButton.Click += OnSimilarRegenerateClicked;
        if (_summaryCloseButton is not null) _summaryCloseButton.Click += OnSummaryCloseClicked;
        if (_repliesCloseButton is not null) _repliesCloseButton.Click += OnRepliesCloseClicked;
        if (_translateCloseButton is not null) _translateCloseButton.Click += OnTranslateCloseClicked;
        if (_similarCloseButton is not null) _similarCloseButton.Click += OnSimilarCloseClicked;
    }

    private void DetachTemplateHandlers()
    {
        if (_headerToggleButton is not null) _headerToggleButton.Click -= OnHeaderToggleClicked;
        if (_processButton is not null) _processButton.Click -= OnProcessClicked;
        DetachFeatureHandlers(_summaryParts, OnSummaryClicked, OnSummaryCancelClicked);
        DetachFeatureHandlers(_repliesParts, OnRepliesClicked, OnRepliesCancelClicked);
        DetachFeatureHandlers(_translateParts, OnTranslateChipClicked, OnTranslateCancelClicked);
        DetachFeatureHandlers(_similarParts, OnSimilarClicked, OnSimilarCancelClicked);
        if (_suggestedRepliesList is not null) _suggestedRepliesList.ItemClick -= OnSuggestedReplyItemClick;
        if (_similarMailList is not null) _similarMailList.ItemClick -= OnSimilarMailItemClick;
        if (_addToCalendarButton is not null) _addToCalendarButton.Click -= OnAddToCalendarClicked;
        if (_translationSourceComboBox is not null) _translationSourceComboBox.SelectionChanged -= OnTranslationSourceChanged;
        if (_translationTargetComboBox is not null) _translationTargetComboBox.SelectionChanged -= OnTranslationTargetChanged;
        if (_translationRunButton is not null) _translationRunButton.Click -= OnTranslationRunClicked;
        if (_summaryCopyButton is not null) _summaryCopyButton.Click -= OnSummaryCopyClicked;
        if (_summaryRegenerateButton is not null) _summaryRegenerateButton.Click -= OnSummaryRegenerateClicked;
        if (_repliesRegenerateButton is not null) _repliesRegenerateButton.Click -= OnRepliesRegenerateClicked;
        if (_similarRegenerateButton is not null) _similarRegenerateButton.Click -= OnSimilarRegenerateClicked;
        if (_summaryCloseButton is not null) _summaryCloseButton.Click -= OnSummaryCloseClicked;
        if (_repliesCloseButton is not null) _repliesCloseButton.Click -= OnRepliesCloseClicked;
        if (_translateCloseButton is not null) _translateCloseButton.Click -= OnTranslateCloseClicked;
        if (_similarCloseButton is not null) _similarCloseButton.Click -= OnSimilarCloseClicked;
    }

    private static void AttachFeatureHandlers(FeatureParts? parts, RoutedEventHandler main, RoutedEventHandler cancel)
    {
        if (parts?.MainButton is not null) parts.MainButton.Click += main;
        if (parts?.CancelButton is not null) parts.CancelButton.Click += cancel;
    }

    private static void DetachFeatureHandlers(FeatureParts? parts, RoutedEventHandler main, RoutedEventHandler cancel)
    {
        if (parts?.MainButton is not null) parts.MainButton.Click -= main;
        if (parts?.CancelButton is not null) parts.CancelButton.Click -= cancel;
    }

    /// <summary>Completes the matching summary request.</summary>
    /// <returns><see langword="true"/> when <paramref name="requestId"/> matches the active request.</returns>
    public bool CompleteSummary(Guid requestId, string summaryText)
    {
        if (_summaryRequestId != requestId) return false;
        _summaryRequestId = null;
        SummaryText = summaryText ?? string.Empty;
        SummaryState = string.IsNullOrWhiteSpace(SummaryText) ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        SyncFeatureVisuals();
        Announce(SummaryHeadingText);
        return true;
    }

    /// <summary>Completes the matching suggested-replies request.</summary>
    /// <returns><see langword="true"/> when <paramref name="requestId"/> matches the active request.</returns>
    public bool CompleteSuggestedReplies(Guid requestId, IEnumerable<WinoIntelligenceReply> replies)
    {
        if (_repliesRequestId != requestId) return false;
        _repliesRequestId = null;
        SuggestedReplies = replies?.ToArray() ?? [];
        SuggestedRepliesState = _replies.Count == 0 ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        SyncFeatureVisuals();
        Announce(SuggestedRepliesHeadingText);
        return true;
    }

    /// <summary>Completes the matching similar-mail request.</summary>
    /// <returns><see langword="true"/> when <paramref name="requestId"/> matches the active request.</returns>
    public bool CompleteSimilarMail(Guid requestId, IEnumerable<WinoIntelligenceSimilarMailItem> items)
    {
        if (_similarRequestId != requestId) return false;
        _similarRequestId = null;
        SimilarMailItems = items?.ToArray() ?? [];
        SimilarMailState = _similarItems.Count == 0 ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        SyncFeatureVisuals();
        Announce(SimilarMailHeadingText);
        return true;
    }

    /// <summary>Marks the matching feature request as failed.</summary>
    /// <returns><see langword="true"/> when <paramref name="requestId"/> matches an active request.</returns>
    public bool FailRequest(Guid requestId)
    {
        if (_summaryRequestId == requestId)
        {
            _summaryRequestId = null;
            SummaryState = WinoIntelligenceFeatureState.Idle;
        }
        else if (_repliesRequestId == requestId)
        {
            _repliesRequestId = null;
            SuggestedRepliesState = WinoIntelligenceFeatureState.Idle;
        }
        else if (_similarRequestId == requestId)
        {
            _similarRequestId = null;
            SimilarMailState = WinoIntelligenceFeatureState.Idle;
        }
        else return false;

        SyncFeatureVisuals();
        Announce(UnavailableSubtitleText);
        return true;
    }

    /// <summary>Clears correlated requests, results, open panels, and expansion state.</summary>
    public void Reset()
    {
        _summaryRequestId = null;
        _repliesRequestId = null;
        _similarRequestId = null;
        _openPanel = null;
        SummaryState = WinoIntelligenceFeatureState.Idle;
        SuggestedRepliesState = WinoIntelligenceFeatureState.Idle;
        SimilarMailState = WinoIntelligenceFeatureState.Idle;
        SummaryText = string.Empty;
        SuggestedReplies = null;
        SimilarMailItems = null;
        IsTranslationBusy = false;
        HasTranslationResult = false;
        IsTranslationApplied = false;
        TranslationStatusText = string.Empty;
        IsExpanded = false;
        SyncAll(animateExpansion: false);
    }

    private void OnContentKeyPropertyChanged(DependencyObject sender, DependencyProperty dp) => Reset();
    private void OnStatePropertyChanged(DependencyObject sender, DependencyProperty dp) => SyncAll(animateExpansion: false);

    private void OnExpansionPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        SyncExpansionVisuals(animate: true);
        SyncHeaderVisuals();
        ExpansionChanged?.Invoke(this, IsExpanded);
    }

    private void OnResultsPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        RefreshCollections();
        if (dp == SummaryTextProperty && _summaryRequestId is null)
            SummaryState = string.IsNullOrWhiteSpace(SummaryText) ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        if (dp == SuggestedRepliesProperty && _repliesRequestId is null)
            SuggestedRepliesState = _replies.Count == 0 ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        if (dp == SimilarMailItemsProperty && _similarRequestId is null)
            SimilarMailState = _similarItems.Count == 0 ? WinoIntelligenceFeatureState.Idle : WinoIntelligenceFeatureState.Done;
        SyncFeatureVisuals();
    }

    private void OnTranslationPropertyChanged(DependencyObject sender, DependencyProperty dp) => SyncTranslationVisuals();

    private void RefreshCollections()
    {
        if (_isSynchronizingCollections) return;
        _isSynchronizingCollections = true;
        try
        {
            Replace(_replies, SuggestedReplies);
            Replace(_similarItems, SimilarMailItems);
        }
        finally { _isSynchronizingCollections = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T>? source)
    {
        target.Clear();
        if (source is null) return;
        foreach (var item in source) if (item is not null) target.Add(item);
    }

    private void SyncAll(bool animateExpansion)
    {
        if (!CanExpand && IsExpanded) IsExpanded = false;
        SyncHeaderVisuals();
        SyncExpansionVisuals(animateExpansion);
        SyncFactVisuals();
        SyncFeatureVisuals();
        SyncTranslationVisuals();
    }

    private void SyncHeaderVisuals()
    {
        var hasDeadline = HasInsights && !string.IsNullOrWhiteSpace(DeadlineText);
        var hasNeedsReply = HasInsights && NeedsReply;
        if (_titleTextBlock is not null) _titleTextBlock.Text = HeaderTitle;
        if (_deadlinePillText is not null) _deadlinePillText.Text = DeadlineText;
        if (_needsReplyPillText is not null) _needsReplyPillText.Text = NeedsReplyText;
        if (_deadlinePill is not null) _deadlinePill.Visibility = ToVisibility(hasDeadline);
        if (_needsReplyPill is not null) _needsReplyPill.Visibility = ToVisibility(hasNeedsReply);
        if (_subtitleTextBlock is not null)
        {
            _subtitleTextBlock.Text = ResolveSubtitleText();
            _subtitleTextBlock.Visibility = ToVisibility(!hasDeadline && !hasNeedsReply && !IsProcessingRunning);
        }
        if (_processButton is not null)
        {
            _processButton.Content = ProcessingState == WinoIntelligenceProcessingState.Failed ? RetryProcessButtonText : ProcessButtonText;
            _processButton.Visibility = ToVisibility(CanRequestProcessing);
            AutomationProperties.SetName(_processButton, ProcessAutomationText);
        }
        if (_processingStatusTextBlock is not null) _processingStatusTextBlock.Text = ResolveSubtitleText();
        if (_processingStatusPanel is not null) _processingStatusPanel.Visibility = ToVisibility(IsProcessingRunning);
        if (_processingProgressRing is not null) _processingProgressRing.IsActive = IsProcessingRunning;
        if (_headerContentRoot is not null) _headerContentRoot.Opacity = CanExpand ? 1 : 0.6;
        if (_headerToggleButton is not null) _headerToggleButton.IsEnabled = CanExpand;
        if (_chevronIcon is not null) _chevronIcon.Visibility = ToVisibility(CanExpand);
        UpdateHeaderAutomationName(hasDeadline, hasNeedsReply);
    }

    private string ResolveSubtitleText() => ProcessingState switch
    {
        WinoIntelligenceProcessingState.Queued => QueuedSubtitleText,
        WinoIntelligenceProcessingState.Processing => ProcessingSubtitleText,
        WinoIntelligenceProcessingState.Failed => ProcessingFailedSubtitleText,
        WinoIntelligenceProcessingState.Unavailable => UnavailableSubtitleText,
        WinoIntelligenceProcessingState.NotProcessed => UnprocessedSubtitleText,
        _ => NoSignalSubtitleText,
    };

    private void UpdateHeaderAutomationName(bool hasDeadline, bool hasNeedsReply)
    {
        if (_headerToggleButton is null) return;
        var parts = new List<string> { HeaderTitle };
        if (hasDeadline) parts.Add(DeadlineText);
        if (hasNeedsReply) parts.Add(NeedsReplyText);
        if (!hasDeadline && !hasNeedsReply) parts.Add(ResolveSubtitleText());
        if (CanExpand) parts.Add(IsExpanded ? ExpandedText : CollapsedText);
        AutomationProperties.SetName(_headerToggleButton, string.Join(", ", parts));
    }

    private void SyncExpansionVisuals(bool animate)
    {
        var expanded = IsExpanded && CanExpand;
        if (_bodyRoot is not null) _bodyRoot.Visibility = ToVisibility(expanded);
        if (_chevronIcon is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(_chevronIcon);
        visual.CenterPoint = new Vector3((float)(_chevronIcon.ActualWidth / 2), (float)(_chevronIcon.ActualHeight / 2), 0);
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = expanded ? 180 : 0;
        }
        else
        {
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1, expanded ? 180 : 0);
            animation.Duration = TimeSpan.FromMilliseconds(167);
            visual.StartAnimation("RotationAngleInDegrees", animation);
        }
    }

    private void SyncFactVisuals()
    {
        var hasDeadline = HasInsights && !string.IsNullOrWhiteSpace(DeadlineText);
        var hasNeedsReply = HasInsights && NeedsReply;
        if (_deadlineFactTextBlock is not null) _deadlineFactTextBlock.Text = string.IsNullOrWhiteSpace(DeadlineDetailText) ? DeadlineText : DeadlineDetailText;
        if (_needsReplyFactTextBlock is not null) _needsReplyFactTextBlock.Text = string.IsNullOrWhiteSpace(NeedsReplyDetailText) ? NeedsReplyText : NeedsReplyDetailText;
        if (_deadlineFactCard is not null) _deadlineFactCard.Visibility = ToVisibility(hasDeadline);
        if (_needsReplyFactCard is not null) _needsReplyFactCard.Visibility = ToVisibility(hasNeedsReply);
        if (_addToCalendarButton is not null) _addToCalendarButton.Visibility = ToVisibility(IsAddToCalendarAvailable);
        if (_factsPanel is not null) _factsPanel.Visibility = ToVisibility(hasDeadline || hasNeedsReply);
        if (_insightsLockedTextBlock is not null) _insightsLockedTextBlock.Text = InsightsLockedText;
        if (_insightsLockedPanel is not null) _insightsLockedPanel.Visibility = ToVisibility(CanRequestProcessing);
    }

    private void SyncFeatureVisuals()
    {
        if (_summaryResultTextBlock is not null) _summaryResultTextBlock.Text = SummaryText;
        SyncFeature(_summaryParts, SummaryState, IsSummaryAvailable,
            SummarizeButtonText, string.Empty, SummarizingText, CancelButtonText);
        SyncFeature(_repliesParts, SuggestedRepliesState, IsSuggestedRepliesAvailable && HasInsights,
            SuggestRepliesButtonText,
            SuggestedRepliesState == WinoIntelligenceFeatureState.Done ? _replies.Count.ToString() : string.Empty,
            DraftingRepliesText, CancelButtonText);
        SyncFeature(_similarParts, SimilarMailState, IsFindSimilarMailAvailable && HasInsights,
            FindSimilarButtonText,
            SimilarMailState == WinoIntelligenceFeatureState.Done ? _similarItems.Count.ToString() : string.Empty,
            FindSimilarButtonText, CancelButtonText);
        SyncPanels();
    }

    /// <summary>
    /// Paints one chip. The chip keeps a fixed icon in every state — progress and cancel both live in
    /// the detail panel, so the rail stays a stable set of entry points instead of animating in place.
    /// </summary>
    private static void SyncFeature(
        FeatureParts? parts,
        WinoIntelligenceFeatureState state,
        bool visible,
        string label,
        string hint,
        string busyName,
        string cancelText)
    {
        if (parts is null) return;

        var busy = visible && state == WinoIntelligenceFeatureState.Busy;

        if (parts.Label is not null) parts.Label.Text = label;
        if (parts.MainButton is not null)
        {
            parts.MainButton.Visibility = ToVisibility(visible);
            AutomationProperties.SetName(
                parts.MainButton,
                busy ? busyName : string.IsNullOrWhiteSpace(hint) ? label : $"{label}, {hint}");
        }

        // The cancel button sits inside the waiting row, which is only ever shown while this feature
        // is busy and its panel is open, so its own visibility does not need driving here.
        if (parts.CancelButton is not null)
        {
            parts.CancelButton.Content = cancelText;
            AutomationProperties.SetName(parts.CancelButton, cancelText);
        }

        if (parts.Hint is not null)
        {
            parts.Hint.Text = hint;
            parts.Hint.Visibility = ToVisibility(!busy && !string.IsNullOrWhiteSpace(hint));
        }
        if (parts.ResultDot is not null)
        {
            parts.ResultDot.Visibility = ToVisibility(visible && state == WinoIntelligenceFeatureState.Done);
        }
    }

    private void SyncTranslationVisuals()
    {
        var target = TranslationLanguages?.FirstOrDefault(x => string.Equals(x.Code, SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase));
        var hint = target is null ? string.Empty : string.Format(TranslateTargetHintFormat, target.Label);
        SyncFeature(
            _translateParts,
            IsTranslationBusy ? WinoIntelligenceFeatureState.Busy
                : HasTranslationResult ? WinoIntelligenceFeatureState.Done
                : WinoIntelligenceFeatureState.Idle,
            IsTranslateAvailable,
            TranslateButtonText,
            hint,
            TranslationStatusText,
            TranslationCancelButtonText);

        var languages = TranslationLanguages?.ToArray() ?? [];
        _isSynchronizingLanguages = true;
        try
        {
            if (_translationSourceComboBox is not null)
            {
                _translationSourceComboBox.ItemsSource = languages;
                _translationSourceComboBox.SelectedItem = languages.FirstOrDefault(x => x.Code == SelectedSourceLanguage);
                _translationSourceComboBox.IsEnabled = !IsTranslationBusy;
                AutomationProperties.SetName(_translationSourceComboBox, SourceLanguageLabel);
            }
            if (_translationTargetComboBox is not null)
            {
                var targets = languages.Where(x => !string.IsNullOrWhiteSpace(x.Code)).ToArray();
                _translationTargetComboBox.ItemsSource = targets;
                _translationTargetComboBox.SelectedItem = targets.FirstOrDefault(x => x.Code == SelectedTargetLanguage);
                _translationTargetComboBox.IsEnabled = !IsTranslationBusy;
                AutomationProperties.SetName(_translationTargetComboBox, TargetLanguageLabel);
            }
        }
        finally { _isSynchronizingLanguages = false; }

        if (_translationRunButton is not null)
        {
            _translationRunButton.Content = IsTranslationApplied ? ShowOriginalButtonText : HasTranslationResult ? TranslateAgainButtonText : TranslateButtonText;
            _translationRunButton.IsEnabled = !IsTranslationBusy && (string.IsNullOrWhiteSpace(SelectedSourceLanguage)
                || !string.Equals(SelectedSourceLanguage, SelectedTargetLanguage, StringComparison.OrdinalIgnoreCase));
        }
        if (_translationStatusTextBlock is not null) _translationStatusTextBlock.Text = TranslationStatusText;
        if (_translationBusyTextBlock is not null) _translationBusyTextBlock.Text = TranslationStatusText;

        // Panel visibility, including the applied bar and the busy row, is owned by SyncPanels so it
        // stays gated on which panel is open.
        SyncPanels();
    }

    /// <summary>
    /// Exactly one feature's detail is on screen at a time.
    /// </summary>
    /// <remarks>
    /// Every detail row is gated on <c>_openPanel</c> as well as on its own feature state, not on the
    /// state alone. Two features can legitimately be in flight at once — the rail keeps showing each
    /// chip's own ring and cancel — but the panel area belongs to whichever feature the user last
    /// opened, so a background summarize can never render underneath the translate panel.
    /// Rings for closed panels are switched off too, or their composition animations keep running.
    /// </remarks>
    private void SyncPanels()
    {
        var summaryOpen = _openPanel == WinoIntelligenceFeature.Summary;
        var repliesOpen = _openPanel == WinoIntelligenceFeature.SuggestedReplies;
        var translateOpen = _openPanel == TranslationFeature;
        var similarOpen = _openPanel == WinoIntelligenceFeature.FindSimilarMail;

        if (_panelHost is not null) _panelHost.Visibility = ToVisibility(_openPanel is not null);
        SetPanel(_summaryPanel, summaryOpen);
        SetPanel(_repliesPanel, repliesOpen);
        SetPanel(_translatePanel, translateOpen);
        SetPanel(_similarPanel, similarOpen);

        var summaryBusy = summaryOpen && SummaryState == WinoIntelligenceFeatureState.Busy;
        if (_summaryWaitingPanel is not null) _summaryWaitingPanel.Visibility = ToVisibility(summaryBusy);
        if (_summaryWaitingRing is not null) _summaryWaitingRing.IsActive = summaryBusy;
        if (_summaryResultTextBlock is not null)
        {
            _summaryResultTextBlock.Visibility = ToVisibility(summaryOpen && SummaryState == WinoIntelligenceFeatureState.Done);
        }

        var repliesBusy = repliesOpen && SuggestedRepliesState == WinoIntelligenceFeatureState.Busy;
        if (_repliesWaitingPanel is not null) _repliesWaitingPanel.Visibility = ToVisibility(repliesBusy);
        if (_repliesWaitingRing is not null) _repliesWaitingRing.IsActive = repliesBusy;
        if (_suggestedRepliesList is not null)
        {
            _suggestedRepliesList.Visibility = ToVisibility(repliesOpen && SuggestedRepliesState == WinoIntelligenceFeatureState.Done);
        }

        var similarBusy = similarOpen && SimilarMailState == WinoIntelligenceFeatureState.Busy;
        if (_similarWaitingPanel is not null) _similarWaitingPanel.Visibility = ToVisibility(similarBusy);
        if (_similarWaitingRing is not null) _similarWaitingRing.IsActive = similarBusy;
        if (_similarMailList is not null)
        {
            _similarMailList.Visibility = ToVisibility(similarOpen && SimilarMailState == WinoIntelligenceFeatureState.Done);
        }

        var translationBusy = translateOpen && IsTranslationBusy;
        if (_translationWaitingPanel is not null) _translationWaitingPanel.Visibility = ToVisibility(translationBusy);
        if (_translationWaitingRing is not null) _translationWaitingRing.IsActive = translationBusy;
        if (_translationAppliedPanel is not null)
        {
            _translationAppliedPanel.Visibility = ToVisibility(translateOpen && HasTranslationResult && !IsTranslationBusy);
        }
    }

    private static void SetPanel(FrameworkElement? panel, bool visible)
    {
        if (panel is not null) panel.Visibility = ToVisibility(visible);
    }

    private void OpenPanel(WinoIntelligenceFeature feature, Control? focusTarget = null)
    {
        _openPanel = feature;
        SyncPanels();
        if (focusTarget is not null) DispatcherQueue.TryEnqueue(() => focusTarget.Focus(FocusState.Programmatic));
    }

    private void ClosePanel(WinoIntelligenceFeature feature, Button? returnFocus)
    {
        if (_openPanel == feature) _openPanel = null;
        SyncPanels();
        DispatcherQueue.TryEnqueue(() => returnFocus?.Focus(FocusState.Programmatic));
    }

    private void StartFeature(WinoIntelligenceFeature feature)
    {
        var requestId = Guid.NewGuid();
        switch (feature)
        {
            case WinoIntelligenceFeature.Summary:
                _summaryRequestId = requestId;
                SummaryState = WinoIntelligenceFeatureState.Busy;
                SummaryText = string.Empty;
                OpenPanel(feature, _summaryCloseButton);
                break;
            case WinoIntelligenceFeature.SuggestedReplies:
                _repliesRequestId = requestId;
                SuggestedRepliesState = WinoIntelligenceFeatureState.Busy;
                SuggestedReplies = null;
                OpenPanel(feature, _repliesCloseButton);
                break;
            case WinoIntelligenceFeature.FindSimilarMail:
                _similarRequestId = requestId;
                SimilarMailState = WinoIntelligenceFeatureState.Busy;
                SimilarMailItems = null;
                OpenPanel(feature, _similarCloseButton);
                break;
        }
        SyncFeatureVisuals();
        Announce(feature switch
        {
            WinoIntelligenceFeature.Summary => SummarizingText,
            WinoIntelligenceFeature.SuggestedReplies => DraftingRepliesText,
            WinoIntelligenceFeature.FindSimilarMail => FindSimilarButtonText,
            _ => HeaderTitle,
        });
        FeatureRequested?.Invoke(this, new WinoIntelligenceRequestEventArgs(requestId, feature));
    }

    private void CancelFeature(WinoIntelligenceFeature feature)
    {
        Guid? requestId = feature switch
        {
            WinoIntelligenceFeature.Summary => _summaryRequestId,
            WinoIntelligenceFeature.SuggestedReplies => _repliesRequestId,
            WinoIntelligenceFeature.FindSimilarMail => _similarRequestId,
            _ => null,
        };
        if (requestId is null) return;
        if (feature == WinoIntelligenceFeature.Summary) { _summaryRequestId = null; SummaryState = WinoIntelligenceFeatureState.Idle; }
        if (feature == WinoIntelligenceFeature.SuggestedReplies) { _repliesRequestId = null; SuggestedRepliesState = WinoIntelligenceFeatureState.Idle; }
        if (feature == WinoIntelligenceFeature.FindSimilarMail) { _similarRequestId = null; SimilarMailState = WinoIntelligenceFeatureState.Idle; }
        if (_openPanel == feature) _openPanel = null;
        SyncFeatureVisuals();
        Announce(CancelButtonText);
        FeatureCancelRequested?.Invoke(this, new WinoIntelligenceCancelRequestedEventArgs(requestId.Value, feature));
    }

    private void Announce(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var peer = FrameworkElementAutomationPeer.FromElement(this) ?? OnCreateAutomationPeer();
        peer.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            AutomationNotificationProcessing.MostRecent,
            text,
            "WinoIntelligenceHeaderStatus");
    }

    private void OnHeaderToggleClicked(object sender, RoutedEventArgs e) { if (CanExpand) IsExpanded = !IsExpanded; }
    private void OnProcessClicked(object sender, RoutedEventArgs e) { if (CanRequestProcessing) ProcessRequested?.Invoke(this, EventArgs.Empty); }

    private void OnSummaryClicked(object sender, RoutedEventArgs e)
    {
        if (!IsSummaryAvailable) return;
        if (SummaryState == WinoIntelligenceFeatureState.Idle) StartFeature(WinoIntelligenceFeature.Summary);
        else OpenPanel(WinoIntelligenceFeature.Summary, _summaryCloseButton);
    }

    private void OnRepliesClicked(object sender, RoutedEventArgs e)
    {
        if (!IsSuggestedRepliesAvailable || !HasInsights) return;
        if (SuggestedRepliesState == WinoIntelligenceFeatureState.Idle) StartFeature(WinoIntelligenceFeature.SuggestedReplies);
        else OpenPanel(WinoIntelligenceFeature.SuggestedReplies, _repliesCloseButton);
    }

    private void OnSimilarClicked(object sender, RoutedEventArgs e)
    {
        if (!IsFindSimilarMailAvailable || !HasInsights) return;
        if (SimilarMailState == WinoIntelligenceFeatureState.Idle) StartFeature(WinoIntelligenceFeature.FindSimilarMail);
        else OpenPanel(WinoIntelligenceFeature.FindSimilarMail, _similarCloseButton);
    }

    private void OnTranslateChipClicked(object sender, RoutedEventArgs e) => OpenPanel(TranslationFeature, _translateCloseButton);
    private void OnSummaryCancelClicked(object sender, RoutedEventArgs e) => CancelFeature(WinoIntelligenceFeature.Summary);
    private void OnRepliesCancelClicked(object sender, RoutedEventArgs e) => CancelFeature(WinoIntelligenceFeature.SuggestedReplies);
    private void OnSimilarCancelClicked(object sender, RoutedEventArgs e) => CancelFeature(WinoIntelligenceFeature.FindSimilarMail);
    private void OnTranslateCancelClicked(object sender, RoutedEventArgs e)
    {
        if (!IsTranslationBusy) return;
        ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.CancelTranslation));
        ClosePanel(TranslationFeature, _translateParts?.MainButton);
    }

    private void OnSummaryRegenerateClicked(object sender, RoutedEventArgs e) => StartFeature(WinoIntelligenceFeature.Summary);
    private void OnRepliesRegenerateClicked(object sender, RoutedEventArgs e) => StartFeature(WinoIntelligenceFeature.SuggestedReplies);
    private void OnSimilarRegenerateClicked(object sender, RoutedEventArgs e) => StartFeature(WinoIntelligenceFeature.FindSimilarMail);
    private void OnSummaryCloseClicked(object sender, RoutedEventArgs e) => ClosePanel(WinoIntelligenceFeature.Summary, _summaryParts?.MainButton);
    private void OnRepliesCloseClicked(object sender, RoutedEventArgs e) => ClosePanel(WinoIntelligenceFeature.SuggestedReplies, _repliesParts?.MainButton);
    private void OnTranslateCloseClicked(object sender, RoutedEventArgs e) => ClosePanel(TranslationFeature, _translateParts?.MainButton);
    private void OnSimilarCloseClicked(object sender, RoutedEventArgs e) => ClosePanel(WinoIntelligenceFeature.FindSimilarMail, _similarParts?.MainButton);

    private void OnSummaryCopyClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SummaryText)) return;
        var package = new DataPackage();
        package.SetText(SummaryText);
        Clipboard.SetContent(package);
    }

    private void OnSuggestedReplyItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WinoIntelligenceReply reply)
            SuggestedReplyChosen?.Invoke(this, new WinoIntelligenceReplyChosenEventArgs(reply, _replies.IndexOf(reply)));
    }

    private void OnSimilarMailItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WinoIntelligenceSimilarMailItem item)
            SimilarMailChosen?.Invoke(this, new WinoIntelligenceSimilarMailChosenEventArgs(item, _similarItems.IndexOf(item)));
    }

    private void OnAddToCalendarClicked(object sender, RoutedEventArgs e)
        => ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.AddDeadlineToCalendar));

    private void OnTranslationRunClicked(object sender, RoutedEventArgs e)
        => ActionInvoked?.Invoke(this, new WinoIntelligenceActionEventArgs(WinoIntelligenceAction.Translate));

    private void OnTranslationSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingLanguages && _translationSourceComboBox?.SelectedItem is WinoIntelligenceLanguageOption language)
            SelectedSourceLanguage = language.Code;
    }

    private void OnTranslationTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingLanguages && _translationTargetComboBox?.SelectedItem is WinoIntelligenceLanguageOption language)
            SelectedTargetLanguage = language.Code;
    }

    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static WinoIntelligenceFeature TranslationFeature => (WinoIntelligenceFeature)(-1);

    private sealed record FeatureParts(
        Button? MainButton,
        Button? CancelButton,
        TextBlock? Label,
        TextBlock? Hint,
        FrameworkElement? ResultDot);
}
