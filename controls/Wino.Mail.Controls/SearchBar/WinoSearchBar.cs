using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.AnimatedVisuals;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.Controls.IntelligenceProgressRing;

namespace Wino.Mail.Controls.SearchBar;

[TemplatePart(Name = PartLayoutRootName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartFieldBorderName, Type = typeof(Border))]
[TemplatePart(Name = PartAutoSuggestBoxName, Type = typeof(AutoSuggestBox))]
[TemplatePart(Name = PartQueryButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartMeaningToggleButtonName, Type = typeof(ToggleButton))]
[TemplatePart(Name = PartMeaningToggleLabelName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartSemanticBusyRingName, Type = typeof(WinoIntelligenceProgressRing))]
[TemplatePart(Name = PartMeaningSparkleName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartSearchOptionsButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartSearchOptionsChevronIconName, Type = typeof(FontIcon))]
[TemplatePart(Name = PartSearchPopupName, Type = typeof(Popup))]
[TemplatePart(Name = PartSearchPopupRootName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartHistoryPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartOptionsPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartEmptyHistoryTextName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartHistoryHeadingTextName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartHistoryListName, Type = typeof(ListView))]
[TemplatePart(Name = PartClearHistoryButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartMailOptionsPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartSemanticExplanationPanelName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartSemanticExplanationTextBlockName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartScopeComboBoxName, Type = typeof(ComboBox))]
[TemplatePart(Name = PartReachComboBoxName, Type = typeof(ComboBox))]
[TemplatePart(Name = PartDateComboBoxName, Type = typeof(ComboBox))]
[TemplatePart(Name = PartSenderSuggestBoxName, Type = typeof(AutoSuggestBox))]
[TemplatePart(Name = PartSenderTokenName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartSenderTokenInitialsName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartSenderTokenNameName, Type = typeof(TextBlock))]
[TemplatePart(Name = PartSenderTokenRemoveButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartAttachmentsButtonName, Type = typeof(ToggleButton))]
[TemplatePart(Name = PartUnreadButtonName, Type = typeof(ToggleButton))]
[TemplatePart(Name = PartFlaggedButtonName, Type = typeof(ToggleButton))]
[TemplatePart(Name = PartResetButtonName, Type = typeof(Button))]
[TemplatePart(Name = PartPanelSearchButtonName, Type = typeof(Button))]
/// <summary>
/// Provides search suggestions, recent searches, semantic-search state, and mail filters.
/// </summary>
public sealed partial class WinoSearchBar : Control
{
    private const string PartLayoutRootName = "PART_LayoutRoot";
    private const string PartFieldBorderName = "PART_FieldBorder";
    private const string PartAutoSuggestBoxName = "PART_AutoSuggestBox";
    private const string PartQueryButtonName = "PART_QueryButton";
    private const string PartMeaningToggleButtonName = "PART_MeaningToggleButton";
    private const string PartMeaningToggleLabelName = "PART_MeaningToggleLabel";
    private const string PartSemanticBusyRingName = "PART_SemanticBusyRing";
    private const string PartMeaningSparkleName = "PART_MeaningSparkle";
    private const string PartSearchOptionsButtonName = "PART_SearchOptionsButton";
    private const string PartSearchOptionsChevronIconName = "PART_SearchOptionsChevronIcon";
    private const string PartSearchPopupName = "PART_SearchPopup";
    private const string PartSearchPopupRootName = "PART_SearchPopupRoot";
    private const string PartHistoryPanelName = "PART_HistoryPanel";
    private const string PartOptionsPanelName = "PART_OptionsPanel";
    private const string PartEmptyHistoryTextName = "PART_EmptyHistoryText";
    private const string PartHistoryHeadingTextName = "PART_HistoryHeadingText";
    private const string PartHistoryListName = "PART_HistoryList";
    private const string PartClearHistoryButtonName = "PART_ClearHistoryButton";
    private const string PartMailOptionsPanelName = "PART_MailOptionsPanel";
    private const string PartSemanticExplanationPanelName = "PART_SemanticExplanationPanel";
    private const string PartSemanticExplanationTextBlockName = "PART_SemanticExplanationTextBlock";
    private const string PartScopeComboBoxName = "PART_ScopeComboBox";
    private const string PartReachComboBoxName = "PART_ReachComboBox";
    private const string PartDateComboBoxName = "PART_DateComboBox";
    private const string PartSenderSuggestBoxName = "PART_SenderSuggestBox";
    private const string PartSenderTokenName = "PART_SenderToken";
    private const string PartSenderTokenInitialsName = "PART_SenderTokenInitials";
    private const string PartSenderTokenNameName = "PART_SenderTokenName";
    private const string PartSenderTokenRemoveButtonName = "PART_SenderTokenRemoveButton";
    private const string PartAttachmentsButtonName = "PART_AttachmentsButton";
    private const string PartUnreadButtonName = "PART_UnreadButton";
    private const string PartFlaggedButtonName = "PART_FlaggedButton";
    private const string PartResetButtonName = "PART_ResetButton";
    private const string PartPanelSearchButtonName = "PART_PanelSearchButton";
    private static readonly SearchBarOptionItem[] DefaultScopeOptions =
    [
        new((int)SearchBarScope.CurrentFolder, "Current folder"),
        new((int)SearchBarScope.CurrentAccount, "Current account"),
        new((int)SearchBarScope.AllAccounts, "All accounts"),
    ];

    private static readonly SearchBarOptionItem[] DefaultReachOptions =
    [
        new((int)SearchBarReach.DownloadedOnly, "Downloaded only"),
        new((int)SearchBarReach.IncludeServer, "Include mail server"),
    ];

    private static readonly SearchBarOptionItem[] DefaultDateOptions =
    [
        new((int)SearchBarDateRange.AnyTime, "Any time"),
        new((int)SearchBarDateRange.Today, "Today"),
        new((int)SearchBarDateRange.LastSevenDays, "Last 7 days"),
        new((int)SearchBarDateRange.LastThirtyDays, "Last 30 days"),
    ];

    private readonly ObservableCollection<string> _historySuggestions = [];
    private readonly DispatcherTimer _senderDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly UISettings _uiSettings = new();
    private WeakHistoryCollectionChangedSubscription? _historySubscription;
    private bool _isSynchronizingOptions;
    private string _pendingSenderQuery = string.Empty;

    private FrameworkElement? _layoutRoot;
    private UIElement? _xamlRootContent;
    private Border? _fieldBorder;
    private SolidColorBrush? _semanticFieldBrush;
    private Windows.UI.Color? _semanticFieldBrushSource;
    private bool _isFieldFocused;
    private AutoSuggestBox? _autoSuggestBox;
    private Button? _queryButton;
    private Button? _clearTextButton;
    private ToggleButton? _meaningToggle;
    private TextBlock? _meaningToggleLabel;
    private WinoIntelligenceProgressRing? _semanticBusyRing;
    private FrameworkElement? _meaningSparkle;
    private Button? _searchOptionsButton;
    private FontIcon? _searchOptionsChevronIcon;
    private Popup? _searchPopup;
    private FrameworkElement? _searchPopupRoot;
    private FrameworkElement? _historyPanel;
    private FrameworkElement? _optionsPanel;
    private TextBlock? _emptyHistoryText;
    private TextBlock? _historyHeadingText;
    private ListView? _historyList;
    private Button? _clearHistoryButton;
    private FrameworkElement? _mailOptionsPanel;
    private FrameworkElement? _semanticExplanationPanel;
    private TextBlock? _semanticExplanationTextBlock;
    private ComboBox? _scopeComboBox;
    private ComboBox? _reachComboBox;
    private ComboBox? _dateComboBox;
    private AutoSuggestBox? _senderSuggestBox;
    private FrameworkElement? _senderToken;
    private TextBlock? _senderTokenInitials;
    private TextBlock? _senderTokenName;
    private Button? _senderTokenRemoveButton;
    private ToggleButton? _attachmentsButton;
    private ToggleButton? _unreadButton;
    private ToggleButton? _flaggedButton;
    private Button? _resetButton;
    private Button? _panelSearchButton;

    /// <summary>Gets or sets the <c>Mode</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = SearchBarMode.Mail)]
    public partial SearchBarMode Mode { get; set; }

    /// <summary>Gets or sets the <c>Text</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Text { get; set; }

    /// <summary>Gets or sets the <c>PlaceholderText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string PlaceholderText { get; set; }

    /// <summary>Gets or sets the <c>ItemsSource</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial object? ItemsSource { get; set; }

    /// <summary>Gets or sets the <c>ItemTemplate</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial DataTemplate? ItemTemplate { get; set; }

    /// <summary>Gets or sets the <c>QueryIcon</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IconElement? QueryIcon { get; set; }

    /// <summary>Gets or sets the <c>SearchHistoryItemsSource</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<string>? SearchHistoryItemsSource { get; set; }

    /// <summary>Gets or sets the <c>MaxHistorySuggestionCount</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = 8)]
    public partial int MaxHistorySuggestionCount { get; set; }

    /// <summary>Gets or sets the <c>IsSemanticSearchAvailable</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSemanticSearchAvailable { get; set; }

    /// <summary>Gets or sets the <c>IsSemanticSearchEnabled</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSemanticSearchEnabled { get; set; }

    /// <summary>Gets or sets the <c>IsSemanticSearchBusy</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSemanticSearchBusy { get; set; }

    /// <summary>Gets or sets the <c>SearchScope</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = SearchBarScope.CurrentFolder)]
    public partial SearchBarScope SearchScope { get; set; }

    /// <summary>Gets or sets the <c>SearchReach</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = SearchBarReach.DownloadedOnly)]
    public partial SearchBarReach SearchReach { get; set; }

    /// <summary>Gets or sets the <c>SenderFilter</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SenderFilter { get; set; }

    /// <summary>Gets or sets the <c>DateRange</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = SearchBarDateRange.AnyTime)]
    public partial SearchBarDateRange DateRange { get; set; }

    /// <summary>Gets or sets the <c>HasAttachments</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool HasAttachments { get; set; }

    /// <summary>Gets or sets the <c>IsUnread</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsUnread { get; set; }

    /// <summary>Gets or sets the <c>IsFlagged</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsFlagged { get; set; }

    /// <summary>Gets or sets the <c>MeaningToggleText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Meaning")]
    public partial string MeaningToggleText { get; set; }

    /// <summary>Gets or sets the <c>SemanticPlaceholderText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SemanticPlaceholderText { get; set; }

    /// <summary>Gets or sets the <c>SemanticSearchExplanationText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SemanticSearchExplanationText { get; set; }

    /// <summary>Gets or sets the <c>SemanticUnavailableReasonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SemanticUnavailableReasonText { get; set; }

    /// <summary>Gets or sets the <c>SenderSuggestions</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<SearchBarContactSuggestion>? SenderSuggestions { get; set; }

    /// <summary>Gets or sets the <c>SelectedSenderContact</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial SearchBarContactSuggestion? SelectedSenderContact { get; set; }

    /// <summary>Gets or sets the <c>SenderSuggestionItemTemplate</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial DataTemplate? SenderSuggestionItemTemplate { get; set; }

    /// <summary>Gets or sets the <c>SenderPlaceholderText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Name or email address")]
    public partial string SenderPlaceholderText { get; set; }

    /// <summary>Gets or sets the <c>ScopeOptionsSource</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<SearchBarOptionItem>? ScopeOptionsSource { get; set; }

    /// <summary>Gets or sets the <c>ReachOptionsSource</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<SearchBarOptionItem>? ReachOptionsSource { get; set; }

    /// <summary>Gets or sets the <c>DateOptionsSource</c> value.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<SearchBarOptionItem>? DateOptionsSource { get; set; }

    /// <summary>Gets or sets the <c>SearchButtonText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Search")]
    public partial string SearchButtonText { get; set; }

    /// <summary>Gets or sets the <c>SearchOptionsText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Search options")]
    public partial string SearchOptionsText { get; set; }

    /// <summary>Gets or sets the <c>RecentSearchesText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Recent searches")]
    public partial string RecentSearchesText { get; set; }

    /// <summary>Gets or sets the <c>ClearHistoryText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Clear")]
    public partial string ClearHistoryText { get; set; }

    /// <summary>Gets or sets the <c>ClearText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Clear search")]
    public partial string ClearText { get; set; }

    /// <summary>Gets or sets the <c>EmptyHistoryText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "No recent searches yet.")]
    public partial string EmptyHistoryText { get; set; }

    /// <summary>Gets or sets the <c>ScopeLabelText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Search in")]
    public partial string ScopeLabelText { get; set; }

    /// <summary>Gets or sets the <c>ReachLabelText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Search reach")]
    public partial string ReachLabelText { get; set; }

    /// <summary>Gets or sets the <c>SenderLabelText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "From")]
    public partial string SenderLabelText { get; set; }

    /// <summary>Gets or sets the <c>DateLabelText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Date")]
    public partial string DateLabelText { get; set; }

    /// <summary>Gets or sets the <c>AttachmentsFilterText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Attachments")]
    public partial string AttachmentsFilterText { get; set; }

    /// <summary>Gets or sets the <c>UnreadFilterText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Unread")]
    public partial string UnreadFilterText { get; set; }

    /// <summary>Gets or sets the <c>FlaggedFilterText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Flagged")]
    public partial string FlaggedFilterText { get; set; }

    /// <summary>Gets or sets the <c>ResetText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Reset")]
    public partial string ResetText { get; set; }

    /// <summary>Gets or sets the <c>RemoveSenderText</c> value.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Remove sender")]
    public partial string RemoveSenderText { get; set; }

    /// <summary>Initializes a new instance of the <see cref="WinoSearchBar"/> class.</summary>
    public WinoSearchBar()
    {
        DefaultStyleKey = typeof(WinoSearchBar);
        QueryIcon = new AnimatedIcon() { Source = new AnimatedFindVisualSource() }; // new SymbolIcon(Symbol.Find);
    }

    /// <summary>Occurs when the user submits a non-empty search.</summary>
    public event EventHandler<SearchBarSubmittedEventArgs>? SearchSubmitted;

    /// <summary>Occurs when the search text changes.</summary>
    public event EventHandler<SearchBarTextChangedEventArgs>? SearchTextChanged;

    /// <summary>Occurs when the user requests that recent searches be cleared.</summary>
    public event EventHandler? ClearSearchHistoryRequested;

    /// <summary>Occurs when a mail-search option changes.</summary>
    public event EventHandler<SearchBarFilterSnapshot>? SearchOptionsChanged;

    /// <summary>Occurs when sender text is ready for suggestion lookup.</summary>
    public event EventHandler<SearchBarSenderQueryEventArgs>? SenderSuggestionsRequested;

    protected override void OnApplyTemplate()
    {
        DetachTemplateHandlers();
        base.OnApplyTemplate();
        _layoutRoot = GetTemplateChild(PartLayoutRootName) as FrameworkElement;
        _fieldBorder = GetTemplateChild(PartFieldBorderName) as Border;
        _autoSuggestBox = GetTemplateChild(PartAutoSuggestBoxName) as AutoSuggestBox;
        _autoSuggestBox?.ApplyTemplate();
        FindVisualDescendant<TextBox>(_autoSuggestBox)?.ApplyTemplate();
        _queryButton = GetTemplateChild(PartQueryButtonName) as Button ?? FindVisualDescendant<Button>(_autoSuggestBox, "WinoSearchBarQueryButton");
        _clearTextButton = FindVisualDescendant<Button>(_autoSuggestBox, "DeleteButton");
        _meaningToggle = GetTemplateChild(PartMeaningToggleButtonName) as ToggleButton ?? FindVisualDescendant<ToggleButton>(_autoSuggestBox, "WinoSearchBarMeaningToggle");
        _meaningToggleLabel = GetTemplateChild(PartMeaningToggleLabelName) as TextBlock;
        _semanticBusyRing = GetTemplateChild(PartSemanticBusyRingName) as WinoIntelligenceProgressRing ?? FindVisualDescendant<WinoIntelligenceProgressRing>(_autoSuggestBox, "WinoSearchBarSemanticBusyRing");
        _meaningSparkle = GetTemplateChild(PartMeaningSparkleName) as FrameworkElement ?? FindVisualDescendant<FrameworkElement>(_autoSuggestBox, "WinoSearchBarMeaningSparkle");
        _searchOptionsButton = GetTemplateChild(PartSearchOptionsButtonName) as Button;
        _searchOptionsChevronIcon = GetTemplateChild(PartSearchOptionsChevronIconName) as FontIcon;
        _searchPopup = GetTemplateChild(PartSearchPopupName) as Popup;
        _searchPopupRoot = GetTemplateChild(PartSearchPopupRootName) as FrameworkElement;
        _historyPanel = GetTemplateChild(PartHistoryPanelName) as FrameworkElement;
        _optionsPanel = GetTemplateChild(PartOptionsPanelName) as FrameworkElement;
        _emptyHistoryText = GetTemplateChild(PartEmptyHistoryTextName) as TextBlock;
        _historyHeadingText = GetTemplateChild(PartHistoryHeadingTextName) as TextBlock;
        _historyList = GetTemplateChild(PartHistoryListName) as ListView;
        _clearHistoryButton = GetTemplateChild(PartClearHistoryButtonName) as Button;
        _mailOptionsPanel = GetTemplateChild(PartMailOptionsPanelName) as FrameworkElement;
        _semanticExplanationPanel = GetTemplateChild(PartSemanticExplanationPanelName) as FrameworkElement;
        _semanticExplanationTextBlock = GetTemplateChild(PartSemanticExplanationTextBlockName) as TextBlock;
        _scopeComboBox = GetTemplateChild(PartScopeComboBoxName) as ComboBox;
        _reachComboBox = GetTemplateChild(PartReachComboBoxName) as ComboBox;
        _dateComboBox = GetTemplateChild(PartDateComboBoxName) as ComboBox;
        _senderSuggestBox = GetTemplateChild(PartSenderSuggestBoxName) as AutoSuggestBox;
        _senderToken = GetTemplateChild(PartSenderTokenName) as FrameworkElement;
        _senderTokenInitials = GetTemplateChild(PartSenderTokenInitialsName) as TextBlock;
        _senderTokenName = GetTemplateChild(PartSenderTokenNameName) as TextBlock;
        _senderTokenRemoveButton = GetTemplateChild(PartSenderTokenRemoveButtonName) as Button;
        _attachmentsButton = GetTemplateChild(PartAttachmentsButtonName) as ToggleButton;
        _unreadButton = GetTemplateChild(PartUnreadButtonName) as ToggleButton;
        _flaggedButton = GetTemplateChild(PartFlaggedButtonName) as ToggleButton;
        _resetButton = GetTemplateChild(PartResetButtonName) as Button;
        _panelSearchButton = GetTemplateChild(PartPanelSearchButtonName) as Button;
        AttachTemplateHandlers();
        RefreshHistoryItems();
        SynchronizeAll();
    }

    private void AttachTemplateHandlers()
    {
        if (_autoSuggestBox is not null)
        {
            _autoSuggestBox.QuerySubmitted += OnNativeQuerySubmitted;
            _autoSuggestBox.TextChanged += OnSearchTextChanged;
            _autoSuggestBox.GotFocus += OnSearchGotFocus;
            _autoSuggestBox.LostFocus += OnSearchLostFocus;
            _autoSuggestBox.KeyDown += OnSearchKeyDown;
        }
        if (_queryButton is not null) _queryButton.Click += OnQueryButtonClicked;
        if (_meaningToggle is not null) _meaningToggle.Click += OnMeaningToggleClicked;
        if (_clearHistoryButton is not null) _clearHistoryButton.Click += OnClearHistoryClicked;
        if (_searchPopup is not null) { _searchPopup.Opened += OnSearchPopupOpened; _searchPopup.Closed += OnSearchPopupClosed; }
        if (_scopeComboBox is not null) _scopeComboBox.SelectionChanged += OnScopeChanged;
        if (_reachComboBox is not null) _reachComboBox.SelectionChanged += OnReachChanged;
        if (_dateComboBox is not null) _dateComboBox.SelectionChanged += OnDateChanged;
        if (_senderSuggestBox is not null)
        {
            _senderSuggestBox.TextChanged += OnSenderTextChanged;
            _senderSuggestBox.SuggestionChosen += OnSenderSuggestionChosen;
            _senderSuggestBox.QuerySubmitted += OnSenderQuerySubmitted;
            _senderSuggestBox.KeyDown += OnSenderKeyDown;
        }
        if (_senderTokenRemoveButton is not null) _senderTokenRemoveButton.Click += OnSenderTokenRemoveClicked;
        if (_attachmentsButton is not null) _attachmentsButton.Click += OnFilterButtonClicked;
        if (_unreadButton is not null) _unreadButton.Click += OnFilterButtonClicked;
        if (_flaggedButton is not null) _flaggedButton.Click += OnFilterButtonClicked;
        if (_resetButton is not null) _resetButton.Click += OnResetClicked;
    }

    private void DetachTemplateHandlers()
    {
        if (_autoSuggestBox is not null)
        {
            _autoSuggestBox.QuerySubmitted -= OnNativeQuerySubmitted;
            _autoSuggestBox.TextChanged -= OnSearchTextChanged;
            _autoSuggestBox.GotFocus -= OnSearchGotFocus;
            _autoSuggestBox.LostFocus -= OnSearchLostFocus;
            _autoSuggestBox.KeyDown -= OnSearchKeyDown;
        }
        if (_queryButton is not null) _queryButton.Click -= OnQueryButtonClicked;
        if (_meaningToggle is not null) _meaningToggle.Click -= OnMeaningToggleClicked;
        if (_clearHistoryButton is not null) _clearHistoryButton.Click -= OnClearHistoryClicked;
        if (_searchPopup is not null) { _searchPopup.Opened -= OnSearchPopupOpened; _searchPopup.Closed -= OnSearchPopupClosed; }
        if (_scopeComboBox is not null) _scopeComboBox.SelectionChanged -= OnScopeChanged;
        if (_reachComboBox is not null) _reachComboBox.SelectionChanged -= OnReachChanged;
        if (_dateComboBox is not null) _dateComboBox.SelectionChanged -= OnDateChanged;
        if (_senderSuggestBox is not null)
        {
            _senderSuggestBox.TextChanged -= OnSenderTextChanged;
            _senderSuggestBox.SuggestionChosen -= OnSenderSuggestionChosen;
            _senderSuggestBox.QuerySubmitted -= OnSenderQuerySubmitted;
            _senderSuggestBox.KeyDown -= OnSenderKeyDown;
        }
        if (_senderTokenRemoveButton is not null) _senderTokenRemoveButton.Click -= OnSenderTokenRemoveClicked;
        if (_attachmentsButton is not null) _attachmentsButton.Click -= OnFilterButtonClicked;
        if (_unreadButton is not null) _unreadButton.Click -= OnFilterButtonClicked;
        if (_flaggedButton is not null) _flaggedButton.Click -= OnFilterButtonClicked;
        if (_resetButton is not null) _resetButton.Click -= OnResetClicked;
        if (_xamlRootContent is not null)
        {
            _xamlRootContent.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased));
            _xamlRootContent = null;
        }
        StopSenderDebounce();
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        _isFieldFocused = true;
        SynchronizeFieldVisuals();
        ShowHistorySuggestions();
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e)
    {
        _isFieldFocused = false;
        SynchronizeFieldVisuals();
    }

    /// <summary>
    /// The field is a plain <see cref="Border"/> wrapping a chrome-less <see cref="AutoSuggestBox"/>,
    /// so focus and the semantic mode have to be painted here rather than by the text box's own
    /// visual states. Meaning wins over focus: it is the state the user most needs to see.
    /// </summary>
    private void SynchronizeFieldVisuals()
    {
        if (_fieldBorder is null) return;

        var semantic = IsSemanticSearchAvailable && IsSemanticSearchEnabled;

        _fieldBorder.BorderBrush = semantic
            ? ResolveBrush("AccentFillColorDefaultBrush", "TextControlBorderBrushFocused")
            : _isFieldFocused
                ? ResolveBrush("TextControlBorderBrushFocused", "TextControlBorderBrush")
                : ResolveBrush("TextControlBorderBrush", "CardStrokeColorDefaultBrush");

        // A tint, not a fill. The obvious accent background resources (selected-text, accent-default)
        // are fully saturated and turn the field into a solid block that the placeholder cannot be
        // read against, so the accent colour is used at low opacity instead.
        _fieldBorder.Background = semantic
            ? SemanticFieldBrush ?? ResolveBrush("TextControlBackgroundFocused", "TextControlBackground")
            : _isFieldFocused
                ? ResolveBrush("TextControlBackgroundFocused", "TextControlBackground")
                : ResolveBrush("TextControlBackground", "ControlFillColorDefaultBrush");

        // A focused Fluent text field thickens its bottom edge. Reproduce that here so the control
        // still reads as a text field, and keep the semantic state on the full outline.
        _fieldBorder.BorderThickness = semantic
            ? new Thickness(1)
            : _isFieldFocused ? new Thickness(1, 1, 1, 2) : new Thickness(1);
    }

    /// <summary>
    /// Accent at 10% — enough to read as "this query is different", light enough to keep the
    /// placeholder legible in both themes. Rebuilt when the accent changes rather than cached across
    /// theme switches.
    /// </summary>
    private SolidColorBrush? SemanticFieldBrush
    {
        get
        {
            if (ResolveBrush("AccentFillColorDefaultBrush", "SystemAccentColorLight2") is not SolidColorBrush accent) return null;
            if (_semanticFieldBrush is null || _semanticFieldBrushSource != accent.Color)
            {
                _semanticFieldBrushSource = accent.Color;
                _semanticFieldBrush = new SolidColorBrush(accent.Color) { Opacity = 0.10 };
            }
            return _semanticFieldBrush;
        }
    }

    private Brush? ResolveBrush(string key, string fallbackKey)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush) return brush;
        if (Application.Current.Resources.TryGetValue(fallbackKey, out var fallback) && fallback is Brush fallbackBrush) return fallbackBrush;
        return null;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!string.Equals(Text, sender.Text, StringComparison.Ordinal)) Text = sender.Text;
        UpdateSuggestions(sender.Text.Length == 0 && sender.FocusState != FocusState.Unfocused);
        SearchTextChanged?.Invoke(this, new SearchBarTextChangedEventArgs(sender.Text, args.Reason == AutoSuggestionBoxTextChangeReason.UserInput));
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _autoSuggestBox is not null)
        {
            _autoSuggestBox.IsSuggestionListOpen = false;
            e.Handled = true;
        }
    }

    private void OnNativeQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var origin = args.ChosenSuggestion is string
            ? SearchBarSubmissionOrigin.History
            : args.ChosenSuggestion is null
                ? SearchBarSubmissionOrigin.KeyboardOrQueryIcon
                : SearchBarSubmissionOrigin.Suggestion;
        RaiseSearchSubmitted(origin, args.QueryText, args.ChosenSuggestion is string ? null : args.ChosenSuggestion);
        sender.IsSuggestionListOpen = false;
    }

    private void OnQueryButtonClicked(object sender, RoutedEventArgs e)
    {
        RaiseSearchSubmitted(SearchBarSubmissionOrigin.KeyboardOrQueryIcon, Text, null);
        _autoSuggestBox?.Focus(FocusState.Programmatic);
    }

    private void OnMeaningToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingOptions || _meaningToggle is null) return;
        IsSemanticSearchEnabled = _meaningToggle.IsChecked == true && IsSemanticSearchAvailable;
    }

    private void RaiseSearchSubmitted(SearchBarSubmissionOrigin origin, string? queryText, object? chosenSuggestion)
    {
        var normalized = queryText?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return;
        SearchSubmitted?.Invoke(this, new SearchBarSubmittedEventArgs(normalized, chosenSuggestion, origin, Mode,
            IsSemanticSearchAvailable && IsSemanticSearchEnabled, CreateFilterSnapshot()));
    }

    private SearchBarFilterSnapshot CreateFilterSnapshot()
        => new(SearchScope, SearchReach, (SelectedSenderContact?.Address ?? SenderFilter).Trim(), DateRange, HasAttachments, IsUnread, IsFlagged);

    private void ShowHistorySuggestions()
    {
        if (_autoSuggestBox is null || Mode == SearchBarMode.Settings || _autoSuggestBox.Text.Length != 0)
        {
            return;
        }

        UpdateSuggestions(useHistory: true);
        _autoSuggestBox.IsSuggestionListOpen = _historySuggestions.Count > 0;
    }

    private void UpdateSuggestions(bool useHistory)
    {
        if (_autoSuggestBox is null)
        {
            return;
        }

        _autoSuggestBox.ItemsSource = useHistory ? _historySuggestions : ItemsSource;
        _autoSuggestBox.ItemTemplate = useHistory ? null : ItemTemplate;
    }

    private void ShowHistoryPopup()
    {
        if (Mode == SearchBarMode.Settings || _searchPopup is null || !string.IsNullOrEmpty(_autoSuggestBox?.Text ?? Text)) return;

        // An empty recent-search list has nothing to offer, so focusing the field must not throw an
        // empty flyout over the content. If the options popup is already open it stays open.
        if (_historySuggestions.Count == 0)
        {
            if (_optionsPanel?.Visibility != Visibility.Visible) CloseSearchPopup();
            return;
        }

        SetPopupView(false);
        OpenSearchPopup(false);
    }

    private void ShowOptionsPopup()
    {
        if (Mode == SearchBarMode.Settings || _searchPopup is null) return;
        if (Mode != SearchBarMode.Mail) { ShowHistoryPopup(); return; }
        SynchronizeOptionControls();
        SetPopupView(true);
        OpenSearchPopup(true);
    }

    private void SetPopupView(bool showOptions)
    {
        if (_historyPanel is not null) _historyPanel.Visibility = ToVisibility(!showOptions);
        if (_optionsPanel is not null) _optionsPanel.Visibility = ToVisibility(showOptions);
        if (_searchPopup?.IsOpen == true) UpdateChevronRotation(showOptions);
    }

    private void OpenSearchPopup(bool options)
    {
        if (_searchPopup is null || _searchPopupRoot is null) return;
        UpdatePopupSize(options);
        PositionSearchPopup();
        _searchPopup.IsOpen = true;
        DispatcherQueue.TryEnqueue(PositionSearchPopup);
    }

    /// <summary>
    /// Recent searches is a continuation of the field, so it is centred under the whole bar. The mail
    /// filter panel is anchored to its right edge instead, so it hangs under the chevron that opened
    /// it rather than drifting to the opposite side of the control.
    /// </summary>
    private bool IsCenteredPopupView => _optionsPanel?.Visibility != Visibility.Visible;

    private void UpdatePopupSize(bool options)
    {
        if (_searchPopupRoot is null) return;
        var rootWidth = XamlRoot?.Size.Width ?? 640;
        var availableWidth = Math.Max(0, rootWidth - 16);
        var desiredWidth = options ? Math.Max(ActualWidth, 560) : Math.Max(ActualWidth, 320);
        _searchPopupRoot.Width = Math.Min(desiredWidth, Math.Min(640, availableWidth));
        var controlTop = _layoutRoot?.TransformToVisual(null).TransformPoint(new Point()).Y ?? 0;
        _searchPopupRoot.MaxHeight = Math.Max(180, (XamlRoot?.Size.Height ?? 720) - controlTop - ActualHeight - 12);
    }

    private void PositionSearchPopup()
    {
        if (_searchPopup is null || _layoutRoot is null || _searchPopupRoot is null) return;

        var overhang = _layoutRoot.ActualWidth - _searchPopupRoot.Width;
        var desired = IsCenteredPopupView ? overhang / 2 : overhang;

        // Keep the popup inside the window. When it is wider than the window min > max, and an
        // unordered Math.Clamp throws, so the bounds are ordered before clamping.
        var controlLeft = _layoutRoot.TransformToVisual(null).TransformPoint(new Point()).X;
        var rootWidth = XamlRoot?.Size.Width ?? _layoutRoot.ActualWidth;
        var min = -controlLeft + 8;
        var max = rootWidth - controlLeft - _searchPopupRoot.Width - 8;

        _searchPopup.HorizontalOffset = Math.Clamp(desired, Math.Min(min, max), Math.Max(min, max));
        _searchPopup.VerticalOffset = _layoutRoot.ActualHeight + 4;
    }

    private void CloseSearchPopup() { if (_searchPopup is not null) _searchPopup.IsOpen = false; }
    private void OnSearchPopupOpened(object? sender, object e)
    {
        EnsureRootPointerHandler();
        PositionSearchPopup();
        UpdateChevronRotation(_optionsPanel?.Visibility == Visibility.Visible);
    }

    private void OnSearchPopupClosed(object? sender, object e)
    {
        DetachRootPointerHandler();
        UpdateChevronRotation(false);
    }

    private void UpdateChevronRotation(bool expanded)
    {
        if (_searchOptionsChevronIcon is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(_searchOptionsChevronIcon);
        visual.CenterPoint = new Vector3((float)(_searchOptionsChevronIcon.ActualWidth / 2), (float)(_searchOptionsChevronIcon.ActualHeight / 2), 0);
        if (!_uiSettings.AnimationsEnabled)
        {
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = expanded ? 180 : 0;
            return;
        }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, expanded ? 180 : 0);
        animation.Duration = TimeSpan.FromMilliseconds(167);
        visual.StartAnimation("RotationAngleInDegrees", animation);
    }

    private void EnsureRootPointerHandler()
    {
        var rootContent = XamlRoot?.Content as UIElement;
        if (ReferenceEquals(rootContent, _xamlRootContent)) return;
        if (_xamlRootContent is not null) _xamlRootContent.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased));
        _xamlRootContent = rootContent;
        _xamlRootContent?.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), true);
    }

    private void DetachRootPointerHandler()
    {
        if (_xamlRootContent is null) return;
        _xamlRootContent.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased));
        _xamlRootContent = null;
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_searchPopup?.IsOpen != true || e.OriginalSource is not DependencyObject source) return;
        if (!IsWithin(source, this) && !IsWithin(source, _searchPopupRoot)) DispatcherQueue.TryEnqueue(CloseSearchPopup);
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingOptions && _scopeComboBox?.SelectedItem is SearchBarOptionItem option)
            SearchScope = (SearchBarScope)option.Value;
    }

    private void OnReachChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingOptions && _reachComboBox?.SelectedItem is SearchBarOptionItem option)
            SearchReach = (SearchBarReach)option.Value;
    }

    private void OnDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSynchronizingOptions && _dateComboBox?.SelectedItem is SearchBarOptionItem option)
            DateRange = (SearchBarDateRange)option.Value;
    }

    private void OnSenderTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_isSynchronizingOptions || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        SelectedSenderContact = null;
        SenderFilter = sender.Text.Trim();
        StopSenderDebounce();
        _pendingSenderQuery = SenderFilter;
        if (_pendingSenderQuery.Length >= 2)
        {
            _senderDebounceTimer.Tick += OnSenderDebounceTimerTick;
            _senderDebounceTimer.Start();
        }
    }

    private void OnSenderDebounceTimerTick(object? sender, object e)
    {
        StopSenderDebounce();
        if (_pendingSenderQuery.Length >= 2)
            SenderSuggestionsRequested?.Invoke(this, new SearchBarSenderQueryEventArgs(_pendingSenderQuery));
    }

    private void StopSenderDebounce()
    {
        _senderDebounceTimer.Stop();
        _senderDebounceTimer.Tick -= OnSenderDebounceTimerTick;
    }

    private void OnSenderSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchBarContactSuggestion suggestion) SelectSender(suggestion);
    }

    private void OnSenderQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchBarContactSuggestion suggestion) SelectSender(suggestion);
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
            SelectSender(new SearchBarContactSuggestion { DisplayName = args.QueryText.Trim(), Address = args.QueryText.Trim() });
    }

    private void OnSenderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { if (_senderSuggestBox is not null) _senderSuggestBox.IsSuggestionListOpen = false; e.Handled = true; }
        else if (e.Key == VirtualKey.Back && string.IsNullOrEmpty(_senderSuggestBox?.Text) && SelectedSenderContact is not null)
        { ClearSender(); e.Handled = true; }
    }

    private void SelectSender(SearchBarContactSuggestion suggestion)
    {
        SelectedSenderContact = suggestion;
        SenderFilter = suggestion.Address;
        if (_senderSuggestBox is not null) { _senderSuggestBox.Text = string.Empty; _senderSuggestBox.IsSuggestionListOpen = false; }
        SynchronizeSenderVisuals();
    }

    private void OnSenderTokenRemoveClicked(object sender, RoutedEventArgs e) => ClearSender();

    private void ClearSender()
    {
        SelectedSenderContact = null;
        SenderFilter = string.Empty;
        if (_senderSuggestBox is not null) _senderSuggestBox.Text = string.Empty;
        SynchronizeSenderVisuals();
        _senderSuggestBox?.Focus(FocusState.Programmatic);
    }

    private void OnFilterButtonClicked(object sender, RoutedEventArgs e) => ApplyOptionControlValues();

    private void ApplyOptionControlValues()
    {
        if (_isSynchronizingOptions) return;
        if (_scopeComboBox?.SelectedItem is SearchBarOptionItem scope) SearchScope = (SearchBarScope)scope.Value;
        if (_reachComboBox?.SelectedItem is SearchBarOptionItem reach) SearchReach = (SearchBarReach)reach.Value;
        if (_dateComboBox?.SelectedItem is SearchBarOptionItem date) DateRange = (SearchBarDateRange)date.Value;
        HasAttachments = _attachmentsButton?.IsChecked == true;
        IsUnread = _unreadButton?.IsChecked == true;
        IsFlagged = _flaggedButton?.IsChecked == true;
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        IsSemanticSearchEnabled = false;
        SearchScope = SearchBarScope.CurrentFolder;
        SearchReach = SearchBarReach.DownloadedOnly;
        DateRange = SearchBarDateRange.AnyTime;
        HasAttachments = IsUnread = IsFlagged = false;
        ClearSender();
        SynchronizeOptionControls();
    }

    private void OnClearHistoryClicked(object sender, RoutedEventArgs e)
    {
        ClearSearchHistoryRequested?.Invoke(this, EventArgs.Empty);
        RefreshHistoryItems();
    }

    private void OnHistorySourcePropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (dp == SearchHistoryItemsSourceProperty)
        {
            _historySubscription?.Dispose();
            _historySubscription = SearchHistoryItemsSource is INotifyCollectionChanged observable
                ? new WeakHistoryCollectionChangedSubscription(observable, this)
                : null;
        }
        RefreshHistoryItems();
    }

    private void UpdateHistorySource()
        => OnHistorySourcePropertyChanged(this, SearchHistoryItemsSourceProperty);

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshHistoryItems();

    private sealed partial class WeakHistoryCollectionChangedSubscription : IDisposable
    {
        private readonly INotifyCollectionChanged _source;
        private readonly WeakReference<WinoSearchBar> _owner;

        public WeakHistoryCollectionChangedSubscription(INotifyCollectionChanged source, WinoSearchBar owner)
        {
            _source = source;
            _owner = new(owner);
            _source.CollectionChanged += OnCollectionChanged;
        }

        public void Dispose() => _source.CollectionChanged -= OnCollectionChanged;

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_owner.TryGetTarget(out var owner)) owner.OnHistoryCollectionChanged(sender, e);
            else Dispose();
        }
    }

    private void RefreshHistoryItems()
    {
        Replace(_historySuggestions, SearchHistoryItemsSource?.Where(x => !string.IsNullOrWhiteSpace(x)).Take(Math.Max(0, MaxHistorySuggestionCount)) ?? []);
        if (_historyList is not null) { _historyList.ItemsSource = _historySuggestions; _historyList.Visibility = ToVisibility(_historySuggestions.Count > 0); }
        if (_clearHistoryButton is not null) _clearHistoryButton.Visibility = ToVisibility(_historySuggestions.Count > 0);
        if (_emptyHistoryText is not null) _emptyHistoryText.Visibility = ToVisibility(_historySuggestions.Count == 0);
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> source)
    {
        collection.Clear();
        foreach (var item in source) collection.Add(item);
    }

    private void SynchronizeAll()
    {
        SynchronizeInputProperties();
        SynchronizeOptionControls();
        UpdateModeVisuals();
        SynchronizeLocalizedText();
        SynchronizeFieldVisuals();
    }

    private void UpdatePlaceholder()
    {
        if (_autoSuggestBox is not null)
        {
            _autoSuggestBox.PlaceholderText = IsSemanticSearchEnabled && !string.IsNullOrWhiteSpace(SemanticPlaceholderText)
                ? SemanticPlaceholderText
                : PlaceholderText;
        }
    }

    private void UpdateQueryButton()
    {
        if (_queryButton is null)
        {
            return;
        }

        _queryButton.Content = QueryIcon;
        _queryButton.Visibility = ToVisibility(QueryIcon is not null);
    }

    private void UpdateSemanticState()
    {
        if (!IsSemanticSearchAvailable && IsSemanticSearchEnabled)
        {
            IsSemanticSearchEnabled = false;
            return;
        }

        if (_meaningToggle is not null)
        {
            _meaningToggle.IsChecked = IsSemanticSearchEnabled;
        }

        UpdatePlaceholder();
        UpdateModeVisuals();
        SynchronizeFieldVisuals();
    }

    private void UpdateAccessibleText() => SynchronizeLocalizedText();

    private void SynchronizeInputProperties()
    {
        if (_autoSuggestBox is null) return;
        if (!string.Equals(_autoSuggestBox.Text, Text, StringComparison.Ordinal)) _autoSuggestBox.Text = Text;
        _autoSuggestBox.PlaceholderText = IsSemanticSearchEnabled && !string.IsNullOrWhiteSpace(SemanticPlaceholderText) ? SemanticPlaceholderText : PlaceholderText;
        UpdateSuggestions(_autoSuggestBox.Text.Length == 0 && _autoSuggestBox.FocusState != FocusState.Unfocused);
        if (_queryButton is not null) { _queryButton.Content = QueryIcon; _queryButton.Visibility = ToVisibility(QueryIcon is not null); }
    }

    private void SynchronizeOptionControls()
    {
        _isSynchronizingOptions = true;
        try
        {
            if (_meaningToggle is not null) _meaningToggle.IsChecked = IsSemanticSearchEnabled;
            SetOptions(_scopeComboBox, ScopeOptionsSource ?? DefaultScopeOptions, (int)SearchScope);
            SetOptions(_reachComboBox, ReachOptionsSource ?? DefaultReachOptions, (int)SearchReach);
            SetOptions(_dateComboBox, DateOptionsSource ?? DefaultDateOptions, (int)DateRange);
            if (_reachComboBox is not null) _reachComboBox.IsEnabled = !IsSemanticSearchEnabled;
            if (_attachmentsButton is not null) _attachmentsButton.IsChecked = HasAttachments;
            if (_unreadButton is not null) _unreadButton.IsChecked = IsUnread;
            if (_flaggedButton is not null) _flaggedButton.IsChecked = IsFlagged;
            if (_senderSuggestBox is not null)
            {
                _senderSuggestBox.ItemsSource = SenderSuggestions;
                if (SenderSuggestionItemTemplate is not null) _senderSuggestBox.ItemTemplate = SenderSuggestionItemTemplate;
                _senderSuggestBox.PlaceholderText = SenderPlaceholderText;
            }
            SynchronizeSenderVisuals();
        }
        finally { _isSynchronizingOptions = false; }
    }

    private static void SetOptions(ComboBox? comboBox, IEnumerable<SearchBarOptionItem> source, int selectedValue)
    {
        if (comboBox is null) return;
        var values = source.ToArray();
        comboBox.ItemsSource = values;
        comboBox.SelectedItem = values.FirstOrDefault(x => x.Value == selectedValue) ?? values.FirstOrDefault();
    }

    private void SynchronizeSenderVisuals()
    {
        var selected = SelectedSenderContact;
        if (_senderToken is not null) _senderToken.Visibility = ToVisibility(selected is not null);
        if (_senderTokenInitials is not null) _senderTokenInitials.Text = selected?.Initials ?? string.Empty;
        if (_senderTokenName is not null) _senderTokenName.Text = selected?.DisplayName ?? string.Empty;
        if (_senderSuggestBox is not null) _senderSuggestBox.Visibility = ToVisibility(selected is null);
    }

    private void UpdateModeVisuals()
    {
        var mail = Mode == SearchBarMode.Mail;
        if (_mailOptionsPanel is not null) _mailOptionsPanel.Visibility = ToVisibility(mail);
        if (_meaningToggle is not null)
        {
            _meaningToggle.Visibility = ToVisibility(mail && (IsSemanticSearchAvailable || !string.IsNullOrWhiteSpace(SemanticUnavailableReasonText)));
            _meaningToggle.IsEnabled = IsSemanticSearchAvailable && !IsSemanticSearchBusy;
            AutomationProperties.SetHelpText(_meaningToggle, IsSemanticSearchAvailable ? SemanticSearchExplanationText : SemanticUnavailableReasonText);
            ToolTipService.SetToolTip(_meaningToggle, IsSemanticSearchAvailable ? SemanticSearchExplanationText : SemanticUnavailableReasonText);
        }
        if (_semanticBusyRing is not null)
        {
            _semanticBusyRing.IsActive = mail && IsSemanticSearchBusy;
            _semanticBusyRing.Visibility = ToVisibility(mail && IsSemanticSearchBusy);
        }
        if (_meaningSparkle is not null) _meaningSparkle.Visibility = ToVisibility(!IsSemanticSearchBusy);
        if (_semanticExplanationPanel is not null) _semanticExplanationPanel.Visibility = ToVisibility(IsSemanticSearchEnabled && !string.IsNullOrWhiteSpace(SemanticSearchExplanationText));
        if (_semanticExplanationTextBlock is not null) _semanticExplanationTextBlock.Text = SemanticSearchExplanationText;
    }

    private void SynchronizeLocalizedText()
    {
        if (_meaningToggleLabel is not null) _meaningToggleLabel.Text = MeaningToggleText;
        if (_meaningToggle is not null) AutomationProperties.SetName(_meaningToggle, MeaningToggleText);
        if (_queryButton is not null) { AutomationProperties.SetName(_queryButton, SearchButtonText); ToolTipService.SetToolTip(_queryButton, SearchButtonText); }
        if (_searchOptionsButton is not null) { AutomationProperties.SetName(_searchOptionsButton, SearchOptionsText); ToolTipService.SetToolTip(_searchOptionsButton, SearchOptionsText); }
        if (_historyHeadingText is not null) _historyHeadingText.Text = RecentSearchesText;
        if (_clearHistoryButton is not null) _clearHistoryButton.Content = ClearHistoryText;
        if (_clearTextButton is not null) { AutomationProperties.SetName(_clearTextButton, ClearText); ToolTipService.SetToolTip(_clearTextButton, ClearText); }
        if (_emptyHistoryText is not null) _emptyHistoryText.Text = EmptyHistoryText;
        if (_attachmentsButton is not null) _attachmentsButton.Content = AttachmentsFilterText;
        if (_unreadButton is not null) _unreadButton.Content = UnreadFilterText;
        if (_flaggedButton is not null) _flaggedButton.Content = FlaggedFilterText;
        if (_resetButton is not null) _resetButton.Content = ResetText;
        if (_panelSearchButton is not null) _panelSearchButton.Content = SearchButtonText;
        if (_senderTokenRemoveButton is not null) AutomationProperties.SetName(_senderTokenRemoveButton, RemoveSenderText);
        if (_scopeComboBox is not null) AutomationProperties.SetName(_scopeComboBox, ScopeLabelText);
        if (_reachComboBox is not null) AutomationProperties.SetName(_reachComboBox, ReachLabelText);
        if (_dateComboBox is not null) AutomationProperties.SetName(_dateComboBox, DateLabelText);
        if (_senderSuggestBox is not null) AutomationProperties.SetName(_senderSuggestBox, SenderLabelText);
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root, string? automationId = null) where T : DependencyObject
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T candidate && (automationId is null || AutomationProperties.GetAutomationId(candidate) == automationId || (candidate as FrameworkElement)?.Name == automationId)) return candidate;
            var descendant = FindVisualDescendant<T>(child, automationId);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element is null || ancestor is null) return false;
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
