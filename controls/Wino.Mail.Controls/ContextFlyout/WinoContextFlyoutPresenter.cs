using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

public sealed partial class WinoContextFlyoutPresenter : Control
{
    private const string SearchRowPart = "PART_SearchRow";
    private const string SearchBoxPart = "PART_SearchBox";
    private const string BackButtonPart = "PART_BackButton";
    private const string HeaderItemsPart = "PART_HeaderItems";
    private const string ItemsListPart = "PART_ItemsList";
    private const string EmptyTextPart = "PART_EmptyText";
    private const string ShowBackButtonStoryboardKey = "ShowBackButtonStoryboard";
    private const string HideBackButtonStoryboardKey = "HideBackButtonStoryboard";

    private readonly WinoContextFlyout _owner;
    private readonly ObservableCollection<ContextFlyoutRow> _visibleItems = [];
    private readonly Dictionary<KeyboardAccelerator, ContextFlyoutRow> _acceleratorItems = [];
    private readonly Stack<IReadOnlyList<ContextFlyoutMenuEntry>> _navigationStack = [];
    private readonly DataTemplateSelector _itemTemplateSelector;
    private readonly DataTemplate _headerItemTemplate;
    private IReadOnlyList<ContextFlyoutMenuEntry> _currentItems = [];
    private IReadOnlyList<ContextFlyoutRow> _currentRows = [];
    private IReadOnlyList<ContextFlyoutSearchCandidate> _searchCandidates = [];
    private IReadOnlyList<ContextFlyoutHeaderRow> _headerRows = [];
    private Grid? _searchRow;
    private TextBox? _searchBox;
    private Button? _backButton;
    private ItemsRepeater? _headerItems;
    private Storyboard? _showBackButtonStoryboard;
    private Storyboard? _hideBackButtonStoryboard;
    private ListView? _itemsList;
    private TextBlock? _emptyText;
    private bool _isBackButtonVisible;
    private bool _isUpdatingSearch;
    private bool _isOpen;
    private int _focusRequestVersion;

    internal WinoContextFlyoutPresenter(WinoContextFlyout owner)
    {
        _owner = owner;
        DefaultStyleKey = typeof(WinoContextFlyoutPresenter);

        var itemResources = new WinoContextFlyoutResources();
        Resources.MergedDictionaries.Add(itemResources);
        _itemTemplateSelector = itemResources.TemplateSelector;
        _headerItemTemplate = itemResources.HeaderItemTemplate;
    }

    public ObservableCollection<ContextFlyoutRow> VisibleItems => _visibleItems;

    protected override void OnApplyTemplate()
    {
        UnregisterHandlers();
        base.OnApplyTemplate();

        if (!string.IsNullOrWhiteSpace(_owner.Language))
        {
            Language = _owner.Language;
        }

        _searchRow = GetTemplateChild(SearchRowPart) as Grid;
        _searchBox = GetTemplateChild(SearchBoxPart) as TextBox;
        _backButton = GetTemplateChild(BackButtonPart) as Button;
        _headerItems = GetTemplateChild(HeaderItemsPart) as ItemsRepeater;
        _itemsList = GetTemplateChild(ItemsListPart) as ListView;
        _emptyText = GetTemplateChild(EmptyTextPart) as TextBlock;

        if (_searchBox is not null)
        {
            _searchBox.PlaceholderText = _owner.SearchPlaceholderText;
            _searchBox.TextChanged += SearchBoxTextChanged;
            _searchBox.KeyDown += SearchBoxKeyDown;
        }

        if (_backButton is not null)
        {
            _showBackButtonStoryboard = _backButton.Resources[ShowBackButtonStoryboardKey] as Storyboard;
            _hideBackButtonStoryboard = _backButton.Resources[HideBackButtonStoryboardKey] as Storyboard;
            SetBackButtonStoryboardTargets(_showBackButtonStoryboard, _backButton);
            SetBackButtonStoryboardTargets(_hideBackButtonStoryboard, _backButton);
            _backButton.Click += BackButtonClick;
            UpdateBackButton(useTransitions: false);
        }

        if (_headerItems is not null)
        {
            _headerItems.ItemTemplate = _headerItemTemplate;
            _headerItems.ElementPrepared += HeaderItemsElementPrepared;
            _headerItems.ElementClearing += HeaderItemsElementClearing;
        }

        if (_itemsList is not null)
        {
            _itemsList.ItemsSource = _visibleItems;
            _itemsList.ItemTemplateSelector = _itemTemplateSelector;
            _itemsList.ItemClick += ItemsListItemClick;
            _itemsList.KeyDown += ItemsListKeyDown;
            _itemsList.ContainerContentChanging += ItemsListContainerContentChanging;
        }

        if (_emptyText is not null)
        {
            _emptyText.Text = _owner.NoResultsText;
        }
    }

    internal void PrepareForOpen()
    {
        _isOpen = true;
        _navigationStack.Clear();
        ShowPage(_owner.RootItems, animateBackButton: false);
    }

    internal void PrepareForClose()
    {
        _isOpen = false;
        _focusRequestVersion++;
        _navigationStack.Clear();
        _currentItems = [];
        _currentRows = [];
        _searchCandidates = [];
        _visibleItems.Clear();
        UnregisterKeyboardAccelerators();
        ClearHeaderItems();

        _isUpdatingSearch = true;
        if (_searchBox is not null)
        {
            _searchBox.Text = string.Empty;
        }
        _isUpdatingSearch = false;

        _showBackButtonStoryboard?.Stop();
        _hideBackButtonStoryboard?.Stop();
    }

    private void SearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isUpdatingSearch)
        {
            ApplyFilter(_searchBox?.Text ?? string.Empty);
        }
    }

    private void ApplyFilter(string query)
    {
        if (IsSearchAvailable && !string.IsNullOrWhiteSpace(query))
        {
            ApplySearchResults(query);
        }
        else
        {
            ApplyCurrentPage();
        }

        UpdateEmptyState();
        RegisterKeyboardAccelerators();
    }

    /// <summary>
    /// Shows the current page as-is. The filter runs with an empty query so leading, duplicate, and
    /// trailing separators are normalized in one place.
    /// </summary>
    private void ApplyCurrentPage()
    {
        var entries = _currentRows
            .Select(row => new ContextFlyoutFilterEntry(row.Kind == ContextFlyoutRowKind.Separator))
            .ToList();
        var visibleIndexes = ContextFlyoutFilter.GetVisibleIndexes(entries, string.Empty);

        _visibleItems.Clear();
        foreach (var index in visibleIndexes)
        {
            _visibleItems.Add(_currentRows[index]);
        }
    }

    private void ApplySearchResults(string query)
    {
        var entries = _searchCandidates.Select(candidate => new ContextFlyoutFilterEntry(
            false,
            candidate.DisplayText,
            candidate.Breadcrumb,
            candidate.Source.SearchKeywords)).ToList();
        var visibleIndexes = ContextFlyoutFilter.GetVisibleIndexes(entries, query);

        _visibleItems.Clear();
        foreach (var index in visibleIndexes)
        {
            _visibleItems.Add(ContextFlyoutRow.CreateSearchResult(_searchCandidates[index]));
        }
    }


    private void RegisterKeyboardAccelerators()
    {
        UnregisterKeyboardAccelerators();

        foreach (var row in _visibleItems)
        {
            if (row.Command?.Shortcut is not { } shortcut
                || !shortcut.CanExecuteWhileFiltering()
                || !TryCreateAccelerator(shortcut, out var accelerator))
            {
                continue;
            }

            accelerator.Invoked += KeyboardAcceleratorInvoked;
            _acceleratorItems[accelerator] = row;
            KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>
    /// Builds an accelerator owned by this presenter. Nothing is shared with page-level
    /// accelerators, so registering and clearing them cannot disturb the rest of the window.
    /// </summary>
    private static bool TryCreateAccelerator(ContextFlyoutShortcut shortcut, out KeyboardAccelerator accelerator)
    {
        if (!Enum.TryParse(shortcut.Key, true, out VirtualKey key) || key == VirtualKey.None)
        {
            accelerator = null!;
            return false;
        }

        var modifiers = VirtualKeyModifiers.None;

        if (shortcut.Control) modifiers |= VirtualKeyModifiers.Control;
        if (shortcut.Alt) modifiers |= VirtualKeyModifiers.Menu;
        if (shortcut.Shift) modifiers |= VirtualKeyModifiers.Shift;
        if (shortcut.Windows) modifiers |= VirtualKeyModifiers.Windows;

        accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };

        return true;
    }

    private void UnregisterKeyboardAccelerators()
    {
        foreach (var accelerator in KeyboardAccelerators.ToArray())
        {
            accelerator.Invoked -= KeyboardAcceleratorInvoked;
        }

        KeyboardAccelerators.Clear();
        _acceleratorItems.Clear();
    }

    private void KeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_acceleratorItems.TryGetValue(sender, out var row))
        {
            Activate(row);
            args.Handled = true;
        }
    }

    private void SearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down)
        {
            e.Handled = TryFocusItem(fromEnd: false);
        }
        else if (e.Key == VirtualKey.Up)
        {
            e.Handled = TryFocusItem(fromEnd: true);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            // Enter in the search field runs the first match, the way a command palette does.
            var firstMatch = _visibleItems.FirstOrDefault(row => row.CanActivate);
            if (firstMatch is not null)
            {
                Activate(firstMatch);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            NavigateBackOrClose();
            e.Handled = true;
        }
    }

    private void ItemsListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (GetFocusedRow() is { } row)
            {
                Activate(row);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            NavigateBackOrClose();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Moves keyboard focus from the search field onto a row. The list has no selection, so arrow
    /// keys inside it are handled by the list itself from there.
    /// </summary>
    private bool TryFocusItem(bool fromEnd)
    {
        if (_itemsList is null)
        {
            return false;
        }

        var candidates = Enumerable.Range(0, _visibleItems.Count);
        if (fromEnd)
        {
            candidates = candidates.Reverse();
        }

        foreach (var index in candidates)
        {
            if (!_visibleItems[index].CanActivate)
            {
                continue;
            }

            _itemsList.ScrollIntoView(_visibleItems[index]);

            if (_itemsList.ContainerFromIndex(index) is Control container
                && container.Focus(FocusState.Keyboard))
            {
                return true;
            }
        }

        return false;
    }

    private ContextFlyoutRow? GetFocusedRow()
    {
        if (_itemsList?.XamlRoot is null)
        {
            return null;
        }

        var element = FocusManager.GetFocusedElement(_itemsList.XamlRoot) as DependencyObject;
        while (element is not null and not ListViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        return element is ListViewItem container
            ? _itemsList.ItemFromContainer(container) as ContextFlyoutRow
            : null;
    }


    private void ItemsListItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ContextFlyoutRow row && _visibleItems.Contains(row))
        {
            Activate(row);
        }
    }

    private void ItemsListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.Item is not ContextFlyoutRow row || row.Kind == ContextFlyoutRowKind.Separator)
        {
            container.IsEnabled = false;
            container.IsHitTestVisible = false;
            container.IsTabStop = false;

            // A separator carries no command. Keeping it out of the content view stops assistive
            // technology from announcing the row object itself.
            AutomationProperties.SetAccessibilityView(container, AccessibilityView.Raw);
            AutomationProperties.SetAutomationId(container, string.Empty);
            AutomationProperties.SetName(container, string.Empty);
            return;
        }

        container.IsEnabled = row.CanActivate;
        container.IsHitTestVisible = true;
        container.IsTabStop = true;
        AutomationProperties.SetAccessibilityView(container, AccessibilityView.Content);
        AutomationProperties.SetAutomationId(container, row.AutomationId);
        AutomationProperties.SetName(container, row.Text);
    }

    private void UpdateEmptyState()
    {
        var hasItems = _visibleItems.Any(row => row.Kind != ContextFlyoutRowKind.Separator);

        if (_itemsList is not null)
        {
            _itemsList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_emptyText is not null)
        {
            _emptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UnregisterHandlers()
    {
        if (_searchBox is not null)
        {
            _searchBox.TextChanged -= SearchBoxTextChanged;
            _searchBox.KeyDown -= SearchBoxKeyDown;
        }

        if (_backButton is not null)
        {
            _backButton.Click -= BackButtonClick;
        }

        if (_headerItems is not null)
        {
            _headerItems.ElementPrepared -= HeaderItemsElementPrepared;
            _headerItems.ElementClearing -= HeaderItemsElementClearing;
        }

        if (_itemsList is not null)
        {
            _itemsList.ItemClick -= ItemsListItemClick;
            _itemsList.KeyDown -= ItemsListKeyDown;
            _itemsList.ContainerContentChanging -= ItemsListContainerContentChanging;
        }
    }

    private void Activate(ContextFlyoutRow row)
    {
        if (!row.CanActivate)
        {
            return;
        }

        if (row.Entry is ContextFlyoutSubMenuEntry subMenu)
        {
            _navigationStack.Push(_currentItems);
            ShowPage(subMenu.Items);
            return;
        }

        row.Command!.Command!.Execute(row.Command.CommandParameter);
        _owner.Close();
    }

    private void ShowPage(IReadOnlyList<ContextFlyoutMenuEntry> items, bool animateBackButton = true)
    {
        _currentItems = items;
        _currentRows = items.Select(ContextFlyoutRow.Create).ToList();
        _searchCandidates = ContextFlyoutSearch.Collect(items);

        _isUpdatingSearch = true;
        if (_searchBox is not null)
        {
            _searchBox.Text = string.Empty;
        }
        _isUpdatingSearch = false;

        UpdateChrome(animateBackButton);
        ApplyFilter(string.Empty);
        QueueInitialFocus();
    }

    private void QueueInitialFocus()
    {
        var requestVersion = ++_focusRequestVersion;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_isOpen || requestVersion != _focusRequestVersion)
            {
                return;
            }

            if (IsSearchAvailable && _searchBox?.Focus(FocusState.Programmatic) == true)
            {
                return;
            }

            if (_navigationStack.Count == 0 && TryFocusFirstHeaderItem())
            {
                return;
            }

            _itemsList?.Focus(FocusState.Programmatic);
        });
    }

    private bool TryFocusFirstHeaderItem()
    {
        if (_headerItems is null)
        {
            return false;
        }

        for (var index = 0; index < _headerRows.Count; index++)
        {
            if (_headerRows[index].IsEnabled
                && _headerItems.TryGetElement(index) is Control element
                && element.Focus(FocusState.Programmatic))
            {
                return true;
            }
        }

        return false;
    }

    private void BackButtonClick(object sender, RoutedEventArgs e) => NavigateBackOrClose();

    private void NavigateBackOrClose()
    {
        if (_navigationStack.TryPop(out var previousPage))
        {
            ShowPage(previousPage);
        }
        else
        {
            _owner.Close();
        }
    }

    private void UpdateChrome(bool useTransitions)
    {
        if (_searchRow is not null)
        {
            _searchRow.Visibility = IsSearchAvailable ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateBackButton(useTransitions);
        UpdateHeaderItems();
    }

    private void UpdateHeaderItems()
    {
        if (_headerItems is null)
        {
            return;
        }

        if (_navigationStack.Count > 0 || _owner.HeaderItems.Count == 0)
        {
            ClearHeaderItems();
            return;
        }

        _headerRows = _owner.HeaderItems.Select(ContextFlyoutHeaderRow.Create).ToList();
        _headerItems.ItemsSource = _headerRows;
        _headerItems.Visibility = Visibility.Visible;
    }

    private void ClearHeaderItems()
    {
        _headerRows = [];

        if (_headerItems is null)
        {
            return;
        }

        _headerItems.ItemsSource = null;
        _headerItems.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Header commands are invoked here rather than bound to the button, so the command runs before
    /// the flyout is dismissed.
    /// </summary>
    private void HeaderItemsElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is ButtonBase button)
        {
            button.Click += HeaderItemClick;
        }
    }

    private void HeaderItemsElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is ButtonBase button)
        {
            button.Click -= HeaderItemClick;
        }
    }

    private void HeaderItemClick(object sender, RoutedEventArgs e)
    {
        // The header template uses x:Bind, so realized elements carry no DataContext. The repeater
        // index is the reliable way back to the row.
        if (_headerItems is null || sender is not UIElement element)
        {
            return;
        }

        var index = _headerItems.GetElementIndex(element);
        if (index < 0 || index >= _headerRows.Count)
        {
            return;
        }

        var row = _headerRows[index];
        if (!row.IsEnabled)
        {
            return;
        }

        row.Entry.Command?.Execute(row.Entry.CommandParameter);
        _owner.Close();
    }

    private void UpdateBackButton(bool useTransitions = true)
    {
        if (_backButton is null)
        {
            return;
        }

        var canGoBack = _navigationStack.Count > 0;
        _backButton.IsHitTestVisible = canGoBack;
        _backButton.IsTabStop = canGoBack;

        if (useTransitions && canGoBack != _isBackButtonVisible)
        {
            var storyboard = canGoBack ? _showBackButtonStoryboard : _hideBackButtonStoryboard;
            var oppositeStoryboard = canGoBack ? _hideBackButtonStoryboard : _showBackButtonStoryboard;

            oppositeStoryboard?.Stop();
            storyboard?.Begin();
        }
        else if (!useTransitions)
        {
            _showBackButtonStoryboard?.Stop();
            _hideBackButtonStoryboard?.Stop();
            _backButton.Width = canGoBack ? 32 : 0;
            _backButton.Opacity = canGoBack ? 1 : 0;

            if (_backButton.RenderTransform is TranslateTransform transform)
            {
                transform.X = canGoBack ? 0 : -8;
            }
        }

        _isBackButtonVisible = canGoBack;
    }

    private static void SetBackButtonStoryboardTargets(Storyboard? storyboard, Button button)
    {
        if (storyboard is null || storyboard.Children.Count != 3 || button.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        Storyboard.SetTarget(storyboard.Children[0], button);
        Storyboard.SetTarget(storyboard.Children[1], button);
        Storyboard.SetTarget(storyboard.Children[2], transform);
    }

    private bool IsSearchAvailable => _owner.IsSearchEnabled || _navigationStack.Count > 0;
}
