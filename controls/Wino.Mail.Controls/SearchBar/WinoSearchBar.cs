using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.Controls.SearchBar;

/// <summary>
/// A reusable title-bar search field. The page's scope button (left) and filter button (right)
/// appear inside the field while it has focus; the field then grows outward by their widths so the
/// query itself does not move. Recent searches are shown through the AutoSuggestBox's own list.
/// </summary>
public sealed partial class WinoSearchBar : Control, IDisposable
{
    // Below this field width the reach button drops its label and keeps only its icon.
    private const double ReachLabelMinFieldWidth = 340;

    // A click on the compact button that light-dismisses the popup must not reopen it straight away.
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    private readonly ObservableCollection<SearchBarHistoryEntry> _historyEntries = [];
    private Grid? _root;
    private Border? _field;
    private AutoSuggestBox? _input;
    private TextBox? _textBox;
    private FrameworkElement? _leadingHost;
    private FrameworkElement? _trailingHost;
    private FrameworkElement? _headerHost;
    private Button? _headerButton;
    private Button? _clearButton;
    private ToggleButton? _reachButton;
    private FontIcon? _reachIcon;
    private TextBlock? _reachLabel;
    private Button? _filterButton;
    private Button? _compactButton;
    private ContentControl? _compactHost;
    private Popup? _popup;
    private INotifyCollectionChanged? _observedHistory;
    private DateTime _popupClosedAt;
    private object? _highlightedSuggestion;
    private string _typedText = string.Empty;
    private bool _showingHistory;
    private bool _hasFocus;
    private bool _disposed;

    [GeneratedDependencyProperty(DefaultValue = SearchBarMode.Mail)]
    public partial SearchBarMode Mode { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Text { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string PlaceholderText { get; set; }

    /// <summary>Page-provided suggestions shown while the user types.</summary>
    [GeneratedDependencyProperty]
    public partial object? ItemsSource { get; set; }

    [GeneratedDependencyProperty]
    public partial DataTemplate? ItemTemplate { get; set; }

    /// <summary>Template for <see cref="SearchBarHistoryEntry"/> rows. The default style provides one.</summary>
    [GeneratedDependencyProperty]
    public partial DataTemplate? HistoryItemTemplate { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsCompact { get; set; }

    [GeneratedDependencyProperty]
    public partial IEnumerable<string>? SearchHistoryItemsSource { get; set; }

    [GeneratedDependencyProperty(DefaultValue = 8)]
    public partial int MaxHistorySuggestionCount { get; set; }

    [GeneratedDependencyProperty(DefaultValue = SearchBarReach.DownloadedOnly)]
    public partial SearchBarReach SearchReach { get; set; }

    /// <summary>Opened by the scope button at the left edge of the field. The button hides when this is null.</summary>
    [GeneratedDependencyProperty]
    public partial FlyoutBase? HeaderFlyout { get; set; }

    /// <summary>Opened by the filter button at the right edge of the field. The button hides when this is null.</summary>
    [GeneratedDependencyProperty]
    public partial FlyoutBase? FilterFlyout { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string SelectedHeaderButtonTitle { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Local")]
    public partial string LocalText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Online")]
    public partial string OnlineText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Filter")]
    public partial string FilterText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Search")]
    public partial string SearchButtonText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Clear search")]
    public partial string ClearText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Clear history")]
    public partial string ClearHistoryText { get; set; }

    public WinoSearchBar()
    {
        DefaultStyleKey = typeof(WinoSearchBar);
    }

    public event EventHandler<SearchBarSubmittedEventArgs>? SearchSubmitted;
    public event EventHandler<SearchBarTextChangedEventArgs>? SearchTextChanged;
    public event EventHandler? ClearSearchHistoryRequested;
    public event EventHandler? SearchDismissed;

    // TODO: The recent searches list is turned off for now. Set this to true to show it again.
    private const bool IsHistoryEnabled = false;

    private bool AllowsHistory => IsHistoryEnabled && Mode != SearchBarMode.Settings;

    protected override void OnApplyTemplate()
    {
        Detach();
        base.OnApplyTemplate();
        if (_disposed) return;

        _root = GetTemplateChild("PART_LayoutRoot") as Grid;
        _field = GetTemplateChild("PART_FieldBorder") as Border;
        _input = GetTemplateChild("PART_AutoSuggestBox") as AutoSuggestBox;
        _leadingHost = GetTemplateChild("PART_LeadingHost") as FrameworkElement;
        _trailingHost = GetTemplateChild("PART_TrailingHost") as FrameworkElement;
        _headerHost = GetTemplateChild("PART_HeaderHost") as FrameworkElement;
        _headerButton = GetTemplateChild("PART_HeaderButton") as Button;
        _clearButton = GetTemplateChild("PART_ClearButton") as Button;
        _reachButton = GetTemplateChild("PART_ReachButton") as ToggleButton;
        _reachIcon = GetTemplateChild("PART_ReachIcon") as FontIcon;
        _reachLabel = GetTemplateChild("PART_ReachLabel") as TextBlock;
        _filterButton = GetTemplateChild("PART_FilterButton") as Button;
        _compactButton = GetTemplateChild("PART_CompactButton") as Button;
        _compactHost = GetTemplateChild("PART_CompactFieldHost") as ContentControl;
        _popup = GetTemplateChild("PART_SearchPopup") as Popup;

        if (_input is not null)
        {
            _input.Text = Text;
            _input.PlaceholderText = PlaceholderText;
            _input.TextChanged += OnInputTextChanged;
            _input.QuerySubmitted += OnQuerySubmitted;
            _input.SuggestionChosen += OnSuggestionChosen;
            _input.GotFocus += OnInputGotFocus;
            _input.Loaded += OnInputLoaded;
            _input.PreviewKeyDown += OnInputPreviewKeyDown;
            // The text box marks its pointer presses handled; a click on an already focused, empty
            // field still has to bring the recent searches back.
            _input.AddHandler(PointerPressedEvent, new PointerEventHandler(OnInputPointerPressed), true);
        }

        if (_field is not null)
        {
            _field.GotFocus += OnFieldGotFocus;
            _field.LostFocus += OnFieldLostFocus;
            _field.SizeChanged += OnFieldSizeChanged;
        }

        if (_leadingHost is not null) _leadingHost.SizeChanged += OnEdgeSizeChanged;
        if (_trailingHost is not null) _trailingHost.SizeChanged += OnEdgeSizeChanged;
        if (_headerButton is not null) _headerButton.Click += OnHeaderClicked;
        if (_clearButton is not null) _clearButton.Click += OnClearClicked;
        if (_reachButton is not null) _reachButton.Click += OnReachClicked;
        if (_filterButton is not null) _filterButton.Click += OnFilterClicked;
        if (_compactButton is not null) _compactButton.Click += OnCompactClicked;
        if (_popup is not null)
        {
            _popup.PlacementTarget = _compactButton;
            _popup.VerticalOffset = 4;
            _popup.Closed += OnPopupClosed;
        }

        IsEnabledChanged += OnIsEnabledChanged;

        RebuildHistory();
        UseSuggestionSource(history: false);
        RefreshLayout();
        RefreshVisuals(useTransitions: false);
    }

    partial void OnTextChanged(string newValue)
    {
        if (_input is not null && _input.Text != newValue) _input.Text = newValue;
        RefreshVisuals();
    }

    partial void OnPlaceholderTextChanged(string newValue)
    {
        if (_input is not null) _input.PlaceholderText = newValue;
    }

    partial void OnItemsSourceChanged(object? newValue)
    {
        if (!_showingHistory && _input is not null) _input.ItemsSource = newValue;
    }

    partial void OnItemTemplateChanged(DataTemplate? newValue)
    {
        if (!_showingHistory && _input is not null) _input.ItemTemplate = newValue;
    }

    partial void OnHistoryItemTemplateChanged(DataTemplate? newValue)
    {
        if (_showingHistory && _input is not null) _input.ItemTemplate = newValue;
    }

    partial void OnModeChanged(SearchBarMode newValue)
    {
        RebuildHistory();
        RefreshVisuals();
    }

    partial void OnSearchReachChanged(SearchBarReach newValue) => RefreshVisuals();
    partial void OnIsCompactChanged(bool newValue) => RefreshLayout();
    partial void OnHeaderFlyoutPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FlyoutBase oldFlyout)
        {
            oldFlyout.Opened -= OnPageFlyoutOpenedOrClosed;
            oldFlyout.Closed -= OnPageFlyoutOpenedOrClosed;
            oldFlyout.Hide();
        }

        if (e.NewValue is FlyoutBase newFlyout)
        {
            newFlyout.Opened += OnPageFlyoutOpenedOrClosed;
            newFlyout.Closed += OnPageFlyoutOpenedOrClosed;
        }

        RefreshVisuals();
    }

    partial void OnFilterFlyoutPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FlyoutBase oldFlyout)
        {
            oldFlyout.Opened -= OnPageFlyoutOpenedOrClosed;
            oldFlyout.Closed -= OnPageFlyoutOpenedOrClosed;
            oldFlyout.Hide();
        }

        if (e.NewValue is FlyoutBase newFlyout)
        {
            newFlyout.Opened += OnPageFlyoutOpenedOrClosed;
            newFlyout.Closed += OnPageFlyoutOpenedOrClosed;
        }

        RefreshVisuals();
    }

    partial void OnSelectedHeaderButtonTitleChanged(string newValue) => RefreshVisuals();
    partial void OnLocalTextChanged(string newValue) => RefreshVisuals();
    partial void OnOnlineTextChanged(string newValue) => RefreshVisuals();
    partial void OnFilterTextChanged(string newValue) => RefreshVisuals();
    partial void OnClearTextChanged(string newValue) => RefreshVisuals();
    partial void OnClearHistoryTextChanged(string newValue) => RebuildHistory();
    partial void OnMaxHistorySuggestionCountChanged(int newValue) => RebuildHistory();

    partial void OnSearchHistoryItemsSourceChanged(IEnumerable<string>? newValue)
    {
        if (_observedHistory is not null) _observedHistory.CollectionChanged -= OnHistoryCollectionChanged;
        _observedHistory = newValue as INotifyCollectionChanged;
        if (_observedHistory is not null) _observedHistory.CollectionChanged += OnHistoryCollectionChanged;
        RebuildHistory();
    }

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildHistory();

    private void RebuildHistory()
    {
        _historyEntries.Clear();
        if (AllowsHistory && SearchHistoryItemsSource is not null)
        {
            foreach (var item in SearchHistoryItemsSource.Where(item => !string.IsNullOrWhiteSpace(item)).Take(Math.Max(0, MaxHistorySuggestionCount)))
                _historyEntries.Add(SearchBarHistoryEntry.Query(item));
        }

        if (_historyEntries.Count > 0) _historyEntries.Add(SearchBarHistoryEntry.ClearAction(ClearHistoryText));
        if (_showingHistory && _input is not null && _historyEntries.Count == 0) _input.IsSuggestionListOpen = false;
    }

    /// <summary>Points the suggestion list at the recent searches or at the page's suggestions.</summary>
    private void UseSuggestionSource(bool history)
    {
        if (_input is null) return;
        _showingHistory = history;
        _input.TextMemberPath = history ? nameof(SearchBarHistoryEntry.QueryText) : string.Empty;
        _input.ItemTemplate = history ? HistoryItemTemplate : ItemTemplate;
        _input.ItemsSource = history ? _historyEntries : ItemsSource;
    }

    private void ShowHistoryIfEmpty()
    {
        if (_input is null || !string.IsNullOrEmpty(_input.Text) || !AllowsHistory || !IsEnabled) return;
        if (!_showingHistory) UseSuggestionSource(history: true);
        _input.IsSuggestionListOpen = _historyEntries.Count > 0;
    }

    private void RefreshVisuals(bool useTransitions = true)
    {
        var isMail = Mode == SearchBarMode.Mail;
        var online = isMail && SearchReach == SearchBarReach.IncludeServer;
        var reachText = online ? OnlineText : LocalText;

        if (_headerHost is not null) _headerHost.Visibility = _hasFocus && HeaderFlyout is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_headerButton is not null)
        {
            AutomationProperties.SetName(_headerButton, SelectedHeaderButtonTitle);
            ToolTipService.SetToolTip(_headerButton, string.IsNullOrEmpty(SelectedHeaderButtonTitle) ? null : SelectedHeaderButtonTitle);
        }

        if (_clearButton is not null)
        {
            _clearButton.Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(_clearButton, ClearText);
            ToolTipService.SetToolTip(_clearButton, ClearText);
        }

        if (_reachButton is not null)
        {
            _reachButton.Visibility = _hasFocus && isMail ? Visibility.Visible : Visibility.Collapsed;
            _reachButton.IsChecked = online;
            AutomationProperties.SetName(_reachButton, reachText);
            ToolTipService.SetToolTip(_reachButton, reachText);
        }

        if (_reachIcon is not null) _reachIcon.Glyph = online ? "" : "";
        if (_reachLabel is not null) _reachLabel.Text = reachText;

        if (_filterButton is not null)
        {
            _filterButton.Visibility = _hasFocus && FilterFlyout is not null ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_filterButton, FilterText);
            ToolTipService.SetToolTip(_filterButton, FilterText);
        }

        VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", useTransitions);
        VisualStateManager.GoToState(this, !_hasFocus ? "Unfocused" : online ? "FocusedOnline" : "Focused", useTransitions);
        VisualStateManager.GoToState(this, online ? "Online" : "Local", useTransitions);
    }

    /// <summary>Compact mode shows only an icon; the field itself moves into a popup under it.</summary>
    private void RefreshLayout()
    {
        if (_root is null || _field is null || _compactHost is null) return;
        if (_popup is not null) _popup.IsOpen = false;

        if (IsCompact)
        {
            _root.Children.Remove(_field);
            _compactHost.Content = _field;
        }
        else
        {
            if (ReferenceEquals(_compactHost.Content, _field)) _compactHost.Content = null;
            if (!_root.Children.Contains(_field)) _root.Children.Insert(0, _field);
        }

        if (_compactButton is not null) _compactButton.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFieldGotFocus(object sender, RoutedEventArgs e)
    {
        if (_hasFocus) return;
        _hasFocus = true;
        RefreshVisuals();
    }

    // Focus moving between parts of the field raises LostFocus before the next GotFocus, and opening a
    // page flyout moves focus out of the field while the field should stay expanded.
    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateFocusState);

    private void OnPageFlyoutOpenedOrClosed(object? sender, object e) => DispatcherQueue.TryEnqueue(UpdateFocusState);

    private void UpdateFocusState()
    {
        if (_disposed || XamlRoot is null) return;
        var hasFocus = IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, _field)
            || HeaderFlyout?.IsOpen == true
            || FilterFlyout?.IsOpen == true;
        if (hasFocus == _hasFocus) return;
        _hasFocus = hasFocus;
        RefreshVisuals();
    }

    private void OnFieldSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Measured on the control, not the field: the field widens on focus, and a label that
        // appeared then would push the query aside.
        var width = IsCompact ? e.NewSize.Width : ActualWidth;
        if (_reachLabel is not null)
            _reachLabel.Visibility = width < ReachLabelMinFieldWidth ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnInputLoaded(object sender, RoutedEventArgs e)
    {
        _textBox = FindTextBox(_input);
        UpdateTextInset();

        // In compact mode the field only enters the tree when the popup opens.
        if (IsCompact && _popup?.IsOpen == true) _input?.Focus(FocusState.Programmatic);
    }

    private void OnEdgeSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTextInset();

    /// <summary>
    /// Keeps the query clear of the parts laid over the input's ends, and widens the field by the
    /// focus-only page buttons so the query stays where it was before they appeared.
    /// </summary>
    private void UpdateTextInset()
    {
        if (_field is not null)
        {
            var growLeft = IsCompact ? 0 : VisibleWidth(_headerHost);
            var growRight = IsCompact ? 0 : VisibleWidth(_filterButton);
            _field.Margin = new Thickness(-growLeft, 0, -growRight, 0);
        }

        if (_textBox is null) return;
        var left = _leadingHost is null ? 0 : _leadingHost.Margin.Left + _leadingHost.ActualWidth;
        var right = _trailingHost is null ? 0 : _trailingHost.Margin.Right + _trailingHost.ActualWidth;
        _textBox.Padding = new Thickness(left, 0, Math.Max(12, right + 4), 0);
    }

    private static double VisibleWidth(FrameworkElement? element)
        => element is { Visibility: Visibility.Visible } ? element.ActualWidth + element.Margin.Left + element.Margin.Right : 0;

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsEnabled && _input is not null) _input.IsSuggestionListOpen = false;
        RefreshVisuals();
    }

    private void OnInputGotFocus(object sender, RoutedEventArgs e) => ShowHistoryIfEmpty();

    private void OnInputPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_input?.IsSuggestionListOpen == false) ShowHistoryIfEmpty();
    }

    private void OnInputTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Arrowing through the list writes the row's text into the box, and the AutoSuggestBox can
        // report that as user input. Only real typing may switch the list: swapping the source while
        // the keyboard is on a row pulls the list out from under it.
        var isNavigation = _highlightedSuggestion is not null && sender.Text == GetSuggestionText(_highlightedSuggestion);
        var isUserInput = args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && !isNavigation;
        if (!isNavigation) _typedText = sender.Text;

        if (isUserInput)
        {
            _highlightedSuggestion = null;
            if (string.IsNullOrEmpty(sender.Text)) ShowHistoryIfEmpty();
            else if (_showingHistory) UseSuggestionSource(history: false);
        }

        Text = sender.Text;
        SearchTextChanged?.Invoke(this, new SearchBarTextChangedEventArgs(Text, isUserInput));
    }

    private static string GetSuggestionText(object suggestion)
        => suggestion is SearchBarHistoryEntry entry ? entry.QueryText : suggestion.ToString() ?? string.Empty;

    // Enter on a row reached with the arrow keys submits without ChosenSuggestion; remember the row.
    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        => _highlightedSuggestion = args.SelectedItem;

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion ?? _highlightedSuggestion;
        _highlightedSuggestion = null;

        if (chosen is SearchBarHistoryEntry { IsClearAction: true })
        {
            sender.IsSuggestionListOpen = false;
            ClearSearchHistoryRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var historyEntry = chosen as SearchBarHistoryEntry;
        var query = (historyEntry?.Text ?? args.QueryText).Trim();
        if (query.Length == 0) return;

        if (sender.Text != query) sender.Text = query;
        sender.IsSuggestionListOpen = false;

        // Hosts record the query in their history, and a change to the list on screen would reopen it.
        if (_showingHistory) UseSuggestionSource(history: false);

        var origin = historyEntry is not null
            ? SearchBarSubmissionOrigin.History
            : chosen is null ? SearchBarSubmissionOrigin.KeyboardOrQueryIcon : SearchBarSubmissionOrigin.Suggestion;
        SearchSubmitted?.Invoke(this, new SearchBarSubmittedEventArgs(query, historyEntry is null ? chosen : null, origin, Mode, SearchReach));

        if (IsCompact && _popup is not null) _popup.IsOpen = false;
    }

    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // The first Escape closes the list and leaves any highlighted row, the next one clears the
        // query and hands focus back to the page. The AutoSuggestBox's own Escape handling is not
        // used: it restores the last text it saw typed, which is stale once the query was set in code.
        if (e.Key != VirtualKey.Escape || _input is null) return;
        e.Handled = true;

        if (_input.IsSuggestionListOpen)
        {
            _input.IsSuggestionListOpen = false;
            _highlightedSuggestion = null;
            if (_input.Text != _typedText) _input.Text = _typedText;
            return;
        }

        Text = string.Empty;
        if (_popup is not null) _popup.IsOpen = false;
        SearchDismissed?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        Text = string.Empty;
        _input?.Focus(FocusState.Programmatic);
        ShowHistoryIfEmpty();
    }

    // The toggle only changes where the next search looks; typing continues in the field.
    private void OnReachClicked(object sender, RoutedEventArgs e)
    {
        SearchReach = _reachButton?.IsChecked == true ? SearchBarReach.IncludeServer : SearchBarReach.DownloadedOnly;
        _input?.Focus(FocusState.Programmatic);
    }

    private void OnHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (_input is not null) _input.IsSuggestionListOpen = false;
        HeaderFlyout?.ShowAt(_headerButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private void OnFilterClicked(object sender, RoutedEventArgs e)
    {
        if (_input is not null) _input.IsSuggestionListOpen = false;
        FilterFlyout?.ShowAt(_filterButton, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void OnCompactClicked(object sender, RoutedEventArgs e)
    {
        if (_popup is null || DateTime.UtcNow - _popupClosedAt < ReopenGuard) return;
        _popup.IsOpen = true;
    }

    private void OnPopupClosed(object? sender, object e) => _popupClosedAt = DateTime.UtcNow;

    private void Detach()
    {
        if (_input is not null)
        {
            _input.TextChanged -= OnInputTextChanged;
            _input.QuerySubmitted -= OnQuerySubmitted;
            _input.SuggestionChosen -= OnSuggestionChosen;
            _input.GotFocus -= OnInputGotFocus;
            _input.Loaded -= OnInputLoaded;
            _input.PreviewKeyDown -= OnInputPreviewKeyDown;
            _input.RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnInputPointerPressed));
        }

        if (_field is not null)
        {
            _field.GotFocus -= OnFieldGotFocus;
            _field.LostFocus -= OnFieldLostFocus;
            _field.SizeChanged -= OnFieldSizeChanged;
        }

        if (_leadingHost is not null) _leadingHost.SizeChanged -= OnEdgeSizeChanged;
        if (_trailingHost is not null) _trailingHost.SizeChanged -= OnEdgeSizeChanged;
        _textBox = null;

        if (_headerButton is not null) _headerButton.Click -= OnHeaderClicked;
        if (_clearButton is not null) _clearButton.Click -= OnClearClicked;
        if (_reachButton is not null) _reachButton.Click -= OnReachClicked;
        if (_filterButton is not null) _filterButton.Click -= OnFilterClicked;
        if (_compactButton is not null) _compactButton.Click -= OnCompactClicked;
        if (_popup is not null) _popup.Closed -= OnPopupClosed;
        IsEnabledChanged -= OnIsEnabledChanged;
    }

    private static TextBox? FindTextBox(DependencyObject? root)
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox textBox) return textBox;
            if (FindTextBox(child) is { } nested) return nested;
        }

        return null;
    }

    private static bool IsWithin(DependencyObject? child, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;
        while (child is not null)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            child = VisualTreeHelper.GetParent(child);
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
        if (_observedHistory is not null) _observedHistory.CollectionChanged -= OnHistoryCollectionChanged;
        HeaderFlyout?.Hide();
        FilterFlyout?.Hide();
        if (_popup is not null) _popup.IsOpen = false;
        SearchSubmitted = null;
        SearchTextChanged = null;
        ClearSearchHistoryRequested = null;
        SearchDismissed = null;
        SearchHistoryItemsSource = null;
        ItemsSource = null;
        HeaderFlyout = null;
        FilterFlyout = null;
        Template = null;
        GC.SuppressFinalize(this);
    }
}
