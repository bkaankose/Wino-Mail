using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

public sealed partial class WinoContextFlyoutPresenter : Control
{
    private const string SearchBoxPart = "PART_SearchBox";
    private const string ItemsListPart = "PART_ItemsList";
    private const string EmptyTextPart = "PART_EmptyText";

    private readonly WinoContextFlyout _owner;
    private readonly ObservableCollection<WinoContextFlyoutItemBase> _visibleItems = [];
    private readonly Dictionary<KeyboardAccelerator, WinoContextFlyoutItem> _acceleratorItems = [];
    private IReadOnlyList<WinoContextFlyoutItemBase> _allItems = [];
    private TextBox? _searchBox;
    private ListView? _itemsList;
    private TextBlock? _emptyText;

    internal WinoContextFlyoutPresenter(WinoContextFlyout owner)
    {
        _owner = owner;
        DefaultStyleKey = typeof(WinoContextFlyoutPresenter);
        Language = owner.Language;
    }

    public ObservableCollection<WinoContextFlyoutItemBase> VisibleItems => _visibleItems;

    public string SearchPlaceholderText => _owner.SearchPlaceholderText;

    public string NoResultsText => _owner.NoResultsText;

    protected override void OnApplyTemplate()
    {
        UnregisterHandlers();
        base.OnApplyTemplate();

        _searchBox = GetTemplateChild(SearchBoxPart) as TextBox;
        _itemsList = GetTemplateChild(ItemsListPart) as ListView;
        _emptyText = GetTemplateChild(EmptyTextPart) as TextBlock;

        if (_searchBox is not null)
        {
            _searchBox.TextChanged += SearchBoxTextChanged;
            _searchBox.KeyDown += SearchBoxKeyDown;
        }

        if (_itemsList is not null)
        {
            _itemsList.ItemClick += ItemsListItemClick;
            _itemsList.KeyDown += ItemsListKeyDown;
            _itemsList.ContainerContentChanging += ItemsListContainerContentChanging;
        }
    }

    internal void PrepareForOpen()
    {
        _allItems = _owner.BuildFlatItems();

        if (_searchBox is not null)
        {
            _searchBox.Text = string.Empty;
        }

        ApplyFilter(string.Empty);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _searchBox?.Focus(FocusState.Programmatic);
        });
    }

    private void SearchBoxTextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(_searchBox?.Text ?? string.Empty);

    private void ApplyFilter(string query)
    {
        var entries = _allItems.Select(item => item switch
        {
            WinoContextFlyoutItem command => new ContextFlyoutFilterEntry(
                false,
                command.Text,
                command.Breadcrumb,
                command.SearchKeywords),
            _ => new ContextFlyoutFilterEntry(true)
        }).ToList();
        var visibleIndexes = ContextFlyoutFilter.GetVisibleIndexes(entries, query);
        _visibleItems.Clear();

        foreach (var index in visibleIndexes)
        {
            _visibleItems.Add(_allItems[index]);
        }

        UpdateEmptyState();
        SelectFirstEnabledItem();
        RegisterKeyboardAccelerators();
    }

    private void SelectFirstEnabledItem()
    {
        if (_itemsList is null)
        {
            return;
        }

        _itemsList.SelectedItem = _visibleItems.OfType<WinoContextFlyoutItem>()
            .FirstOrDefault(item => item.IsEnabled && item.Command?.CanExecute(item.CommandParameter) == true);
    }

    private void RegisterKeyboardAccelerators()
    {
        foreach (var accelerator in KeyboardAccelerators.ToArray())
        {
            accelerator.Invoked -= KeyboardAcceleratorInvoked;
        }

        KeyboardAccelerators.Clear();
        _acceleratorItems.Clear();

        foreach (var item in _visibleItems.OfType<WinoContextFlyoutItem>())
        {
            if (item.KeyboardAccelerator is not { } accelerator
                || !ContextFlyoutShortcutPolicy.CanExecuteWhileFiltering(
                    accelerator.Key.ToString(),
                    accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Control),
                    accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Menu),
                    accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Shift),
                    accelerator.Modifiers.HasFlag(VirtualKeyModifiers.Windows)))
            {
                continue;
            }

            accelerator.Invoked += KeyboardAcceleratorInvoked;
            _acceleratorItems[accelerator] = item;
            KeyboardAccelerators.Add(accelerator);
        }
    }

    private void KeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_acceleratorItems.TryGetValue(sender, out var item))
        {
            _owner.Invoke(item, _allItems);
            args.Handled = true;
        }
    }

    private void SearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            InvokeSelectedItem();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _owner.Hide();
            e.Handled = true;
        }
    }

    private void ItemsListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            InvokeSelectedItem();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _owner.Hide();
            e.Handled = true;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_itemsList is null)
        {
            return;
        }

        var enabledItems = _visibleItems.OfType<WinoContextFlyoutItem>()
            .Where(item => item.IsEnabled && item.Command?.CanExecute(item.CommandParameter) == true)
            .ToList();

        if (enabledItems.Count == 0)
        {
            return;
        }

        var index = enabledItems.IndexOf(_itemsList.SelectedItem as WinoContextFlyoutItem);
        index = index < 0
            ? 0
            : Math.Clamp(index + delta, 0, enabledItems.Count - 1);
        _itemsList.SelectedItem = enabledItems[index];
        _itemsList.ScrollIntoView(enabledItems[index]);
    }

    private void InvokeSelectedItem()
    {
        if (_itemsList?.SelectedItem is WinoContextFlyoutItem item)
        {
            _owner.Invoke(item, _allItems);
        }
    }

    private void ItemsListItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WinoContextFlyoutItem item)
        {
            _owner.Invoke(item, _allItems);
        }
    }

    private void ItemsListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        if (args.Item is not WinoContextFlyoutItem item)
        {
            container.IsEnabled = false;
            container.IsHitTestVisible = false;
            container.IsTabStop = false;
            return;
        }

        container.IsEnabled = item.IsEnabled && item.Command?.CanExecute(item.CommandParameter) == true;
        container.IsHitTestVisible = true;
        container.IsTabStop = true;
        AutomationProperties.SetAutomationId(container, item.AutomationId);
        AutomationProperties.SetName(container, item.Text);
    }

    private void UpdateEmptyState()
    {
        if (_itemsList is not null)
        {
            _itemsList.Visibility = _visibleItems.OfType<WinoContextFlyoutItem>().Any()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_emptyText is not null)
        {
            _emptyText.Visibility = _visibleItems.OfType<WinoContextFlyoutItem>().Any()
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void UnregisterHandlers()
    {
        if (_searchBox is not null)
        {
            _searchBox.TextChanged -= SearchBoxTextChanged;
            _searchBox.KeyDown -= SearchBoxKeyDown;
        }

        if (_itemsList is not null)
        {
            _itemsList.ItemClick -= ItemsListItemClick;
            _itemsList.KeyDown -= ItemsListKeyDown;
            _itemsList.ContainerContentChanging -= ItemsListContainerContentChanging;
        }
    }
}
