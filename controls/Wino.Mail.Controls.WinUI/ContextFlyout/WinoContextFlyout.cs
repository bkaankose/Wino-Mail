using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core.ContextFlyout;
using WinRT;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Context menu built on the platform <see cref="CommandBarFlyout"/>. Menus are supplied as
/// <see cref="ContextFlyoutMenuEntry"/> models or declared with the Wino context-flyout definition
/// types in XAML. Header entries become primary commands, entries become secondary commands, and
/// submenus cascade as native <see cref="MenuFlyout"/>s. Dynamic commands are reused between
/// opens, because inserting or removing an AppBarButton in a live command bar costs a few
/// milliseconds each; surplus ones are hidden rather than removed.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public partial class WinoContextFlyout : CommandBarFlyout
{
    /// <summary>
    /// Shared height of every secondary command and submenu row. The platform defaults (32 px for
    /// an overflow AppBarButton, 28 px for a mouse-opened MenuFlyoutItem) are too small to click.
    /// </summary>
    public const double RowHeight = 38;

    private sealed class CommandState
    {
        public ContextFlyoutMenuEntry? Entry { get; set; }

        public ContextFlyoutHeaderEntry? Header { get; set; }
    }

    private bool _areDeclaredItemsDirty;

    public WinoContextFlyout()
    {
        AlwaysExpanded = true;
        ShouldConstrainToRootBounds = false;
        Items.CollectionChanged += OnDeclaredItemsChanged;
        Opening += OnOpening;
    }

    /// <summary>
    /// Declarative menu definitions. Use this collection when authoring the flyout in XAML.
    /// <see cref="ItemsSource"/> takes precedence when both APIs are populated.
    /// </summary>
    public ObservableCollection<WinoContextFlyoutItemBase> Items { get; } = [];

    [GeneratedDependencyProperty]
    public partial IReadOnlyList<ContextFlyoutMenuEntry>? ItemsSource { get; set; }

    /// <summary>
    /// Frequent root actions shown as the primary command row above the item list.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IReadOnlyList<ContextFlyoutHeaderEntry>? HeaderItemsSource { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Search")]
    public partial string SearchPlaceholderText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "No results found")]
    public partial string NoResultsText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Language { get; set; }

    internal IReadOnlyList<ContextFlyoutMenuEntry> RootItems =>
        ItemsSource ?? Items.Select(static item => item.CreateEntry()).ToArray();

    internal IReadOnlyList<ContextFlyoutHeaderEntry> HeaderItems => HeaderItemsSource ?? [];

    partial void OnItemsSourcePropertyChanged(DependencyPropertyChangedEventArgs e) => Rebuild();

    partial void OnHeaderItemsSourcePropertyChanged(DependencyPropertyChangedEventArgs e) => Rebuild();

    protected override Control CreatePresenter()
    {
        var presenter = base.CreatePresenter();

        if (!string.IsNullOrWhiteSpace(Language))
        {
            presenter.Language = Language;
        }

        return presenter;
    }

    private void OnDeclaredItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _areDeclaredItemsDirty = true;

    private void OnOpening(object? sender, object e)
    {
        if (_areDeclaredItemsDirty && ItemsSource is null)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _areDeclaredItemsDirty = false;

        var headers = HeaderItems;
        Reconcile(
            PrimaryCommands,
            headers.Count,
            index => IsPlainAppBarButton(PrimaryCommands[index]),
            index => UpdateHeaderButton(PrimaryCommands[index], headers[index]),
            index => CreateHeaderButton(headers[index]));

        var items = NormalizeSeparators(RootItems);
        Reconcile(
            SecondaryCommands,
            items.Count,
            index => CanReuse(SecondaryCommands[index], items[index]),
            index => UpdateSecondaryElement(SecondaryCommands[index], items[index]),
            index => CreateSecondaryElement(items[index]));
    }

    /// <summary>
    /// Dynamic elements form a prefix of the collection, ahead of commands declared in XAML. Each
    /// slot keeps its element when the kind matches; otherwise the stale element is replaced.
    /// Surplus dynamic elements stay in place hidden, so a later menu of the same shape costs no
    /// insertions.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static void Reconcile(
        IList<ICommandBarElement> commands,
        int desiredCount,
        Func<int, bool> canReuse,
        Action<int> update,
        Func<int, ICommandBarElement> create)
    {
        for (var index = 0; index < desiredCount; index++)
        {
            var hasDynamicElement = index < commands.Count && IsDynamic(commands[index]);
            if (hasDynamicElement && canReuse(index))
            {
                update(index);
                ((FrameworkElement)commands[index]).Visibility = Visibility.Visible;
                continue;
            }

            if (hasDynamicElement)
            {
                commands.RemoveAt(index);
            }

            commands.Insert(index, create(index));
        }

        for (var index = desiredCount; index < commands.Count && IsDynamic(commands[index]); index++)
        {
            Release(commands[index]);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    private static void Release(ICommandBarElement element)
    {
        var frameworkElement = (FrameworkElement)element;
        frameworkElement.Visibility = Visibility.Collapsed;

        var state = (CommandState)frameworkElement.Tag;
        state.Entry = null;
        state.Header = null;

        if (element is AppBarButton button)
        {
            button.Flyout = null;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static bool IsDynamic(ICommandBarElement element)
        => element is FrameworkElement { Tag: CommandState };

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static CommandState StateOf(ICommandBarElement element)
        => (CommandState)((FrameworkElement)element).Tag;

    private static bool IsPlainAppBarButton(ICommandBarElement element)
        => element.GetType() == typeof(AppBarButton);

    [DynamicWindowsRuntimeCast(typeof(AppBarSeparator))]
    [DynamicWindowsRuntimeCast(typeof(AppBarToggleButton))]
    private static bool CanReuse(ICommandBarElement element, ContextFlyoutMenuEntry entry) => entry switch
    {
        ContextFlyoutSeparatorEntry => element is AppBarSeparator,
        ContextFlyoutToggleEntry => element is AppBarToggleButton,
        ContextFlyoutSubMenuEntry => IsPlainAppBarButton(element),
        ContextFlyoutCommandEntry => IsPlainAppBarButton(element),
        _ => false,
    };

    private AppBarButton CreateHeaderButton(ContextFlyoutHeaderEntry header)
    {
        var button = new AppBarButton { Tag = new CommandState() };
        button.Click += OnCommandClick;
        UpdateHeaderButton(button, header);

        return button;
    }

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    private static void UpdateHeaderButton(ICommandBarElement element, ContextFlyoutHeaderEntry header)
    {
        var button = (AppBarButton)element;
        StateOf(button).Header = header;

        button.Label = header.Label;
        button.Icon = CreateIcon(header.Icon);
        button.IsEnabled = header.CanExecute();
        button.KeyboardAcceleratorTextOverride = header.Shortcut?.DisplayText ?? string.Empty;
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(header.Shortcut?.DisplayText)
            ? header.Label
            : $"{header.Label} ({header.Shortcut!.DisplayText})");
        AutomationProperties.SetAutomationId(button, header.AutomationId);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    [DynamicWindowsRuntimeCast(typeof(ButtonBase))]
    private ICommandBarElement CreateSecondaryElement(ContextFlyoutMenuEntry entry)
    {
        ICommandBarElement element = entry switch
        {
            ContextFlyoutSeparatorEntry => new AppBarSeparator(),
            ContextFlyoutToggleEntry => new AppBarToggleButton { MinHeight = RowHeight },
            ContextFlyoutSubMenuEntry => new AppBarButton { MinHeight = RowHeight },
            ContextFlyoutCommandEntry => new AppBarButton { MinHeight = RowHeight },
            _ => throw new NotSupportedException($"Unsupported context flyout entry: {entry.GetType().FullName}"),
        };

        ((FrameworkElement)element).Tag = new CommandState();
        if (element is ButtonBase button)
        {
            button.Click += OnCommandClick;
        }

        UpdateSecondaryElement(element, entry);
        return element;
    }

    [DynamicWindowsRuntimeCast(typeof(AppBarButton))]
    [DynamicWindowsRuntimeCast(typeof(AppBarToggleButton))]
    private void UpdateSecondaryElement(ICommandBarElement element, ContextFlyoutMenuEntry entry)
    {
        StateOf(element).Entry = entry;

        switch (entry)
        {
            case ContextFlyoutSeparatorEntry:
                return;

            case ContextFlyoutSubMenuEntry subMenu:
            {
                var button = (AppBarButton)element;
                button.Label = subMenu.Text;
                button.Icon = CreateIcon(subMenu.Icon);
                button.IsEnabled = subMenu.IsEnabled && subMenu.Items.Count > 0;
                button.KeyboardAcceleratorTextOverride = string.Empty;
                button.ClearValue(Control.ForegroundProperty);
                button.Flyout = CreateMenuFlyout(subMenu);
                AutomationProperties.SetAutomationId(button, subMenu.AutomationId);
                return;
            }

            case ContextFlyoutToggleEntry toggle:
            {
                var button = (AppBarToggleButton)element;
                button.Label = toggle.Text;
                button.Icon = CreateIcon(toggle.Icon);
                button.IsChecked = toggle.IsChecked;
                button.IsEnabled = toggle.CanExecute();
                button.KeyboardAcceleratorTextOverride = toggle.Shortcut?.DisplayText ?? string.Empty;
                AutomationProperties.SetAutomationId(button, toggle.AutomationId);
                return;
            }

            case ContextFlyoutCommandEntry command:
            {
                var button = (AppBarButton)element;
                button.Label = command.Text;
                button.Icon = CreateIcon(command.Icon);
                button.IsEnabled = command.CanExecute();
                button.KeyboardAcceleratorTextOverride = command.Shortcut?.DisplayText ?? string.Empty;
                button.Flyout = null;
                ApplyDestructiveForeground(button, command.IsDestructive);
                AutomationProperties.SetAutomationId(button, command.AutomationId);
                return;
            }
        }
    }

    private static void ApplyDestructiveForeground(Control control, bool isDestructive)
    {
        if (isDestructive && CriticalBrush is { } brush)
        {
            control.Foreground = brush;
        }
        else
        {
            control.ClearValue(Control.ForegroundProperty);
        }
    }

    private static Brush? CriticalBrush
    {
        [DynamicWindowsRuntimeCast(typeof(Brush))]
        get => Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var resource) && resource is Brush brush
            ? brush
            : null;
    }

    private void ApplyLanguage(FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(Language))
        {
            element.Language = Language;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void OnCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CommandState state })
        {
            return;
        }

        if (state.Header is { } header)
        {
            header.Command?.Execute(header.CommandParameter);
            Hide();
            return;
        }

        // A submenu button owns a flyout; its click opens that flyout and must not close the menu.
        if (state.Entry is ContextFlyoutCommandEntry command)
        {
            command.Command?.Execute(command.CommandParameter);
            Hide();
        }
    }

    private MenuFlyout CreateMenuFlyout(ContextFlyoutSubMenuEntry subMenu)
    {
        var flyout = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            ShouldConstrainToRootBounds = false,
        };

        // Items are created on first open, so an unopened submenu costs nothing per menu open.
        var populated = false;
        if (subMenu.IsSearchable)
        {
            var search = new SearchableMenu(this, flyout, subMenu);
            flyout.Opening += (_, _) =>
            {
                if (!populated)
                {
                    populated = true;
                    search.Populate();
                }

                search.Reset();
            };
            flyout.Opened += (_, _) => search.FocusSearch();
        }
        else
        {
            flyout.Opening += (_, _) =>
            {
                if (populated)
                {
                    return;
                }

                populated = true;
                foreach (var entry in NormalizeSeparators(subMenu.Items))
                {
                    flyout.Items.Add(CreateMenuFlyoutItem(entry));
                }
            };
        }

        return flyout;
    }

    private MenuFlyoutItemBase CreateMenuFlyoutItem(ContextFlyoutMenuEntry entry)
    {
        MenuFlyoutItemBase item = entry switch
        {
            ContextFlyoutSeparatorEntry => new MenuFlyoutSeparator(),
            ContextFlyoutSubMenuEntry subMenu => CreateMenuFlyoutSubItem(subMenu),
            ContextFlyoutRadioEntry radio => CreateRadioMenuFlyoutItem(radio),
            ContextFlyoutToggleEntry toggle => CreateToggleMenuFlyoutItem(toggle),
            ContextFlyoutCommandEntry command => CreateCommandMenuFlyoutItem(command),
            _ => throw new NotSupportedException($"Unsupported context flyout entry: {entry.GetType().FullName}"),
        };

        ApplyLanguage(item);
        return item;
    }

    private MenuFlyoutSubItem CreateMenuFlyoutSubItem(ContextFlyoutSubMenuEntry subMenu)
    {
        var subItem = new MenuFlyoutSubItem
        {
            Text = subMenu.Text,
            Icon = CreateIcon(subMenu.Icon),
            IsEnabled = subMenu.IsEnabled && subMenu.Items.Count > 0,
            MinHeight = RowHeight,
        };
        AutomationProperties.SetAutomationId(subItem, subMenu.AutomationId);

        foreach (var child in NormalizeSeparators(subMenu.Items))
        {
            subItem.Items.Add(CreateMenuFlyoutItem(child));
        }

        return subItem;
    }

    private RadioMenuFlyoutItem CreateRadioMenuFlyoutItem(ContextFlyoutRadioEntry radio)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = radio.Text,
            Icon = CreateIcon(radio.Icon),
            GroupName = radio.GroupName,
            IsChecked = radio.IsChecked,
            IsEnabled = radio.CanExecute(),
            KeyboardAcceleratorTextOverride = radio.Shortcut?.DisplayText ?? string.Empty,
            MinHeight = RowHeight,
        };
        AutomationProperties.SetAutomationId(item, radio.AutomationId);
        item.Click += (_, _) => InvokeMenuCommand(radio);

        return item;
    }

    private ToggleMenuFlyoutItem CreateToggleMenuFlyoutItem(ContextFlyoutToggleEntry toggle)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = toggle.Text,
            Icon = CreateIcon(toggle.Icon),
            IsChecked = toggle.IsChecked,
            IsEnabled = toggle.CanExecute(),
            KeyboardAcceleratorTextOverride = toggle.Shortcut?.DisplayText ?? string.Empty,
            MinHeight = RowHeight,
        };
        AutomationProperties.SetAutomationId(item, toggle.AutomationId);
        item.Click += (_, _) => InvokeMenuCommand(toggle);

        return item;
    }

    private MenuFlyoutItem CreateCommandMenuFlyoutItem(ContextFlyoutCommandEntry command)
    {
        var item = new MenuFlyoutItem
        {
            Text = command.Text,
            Icon = CreateIcon(command.Icon),
            IsEnabled = command.CanExecute(),
            KeyboardAcceleratorTextOverride = command.Shortcut?.DisplayText ?? string.Empty,
            MinHeight = RowHeight,
        };
        ApplyDestructiveForeground(item, command.IsDestructive);
        AutomationProperties.SetAutomationId(item, command.AutomationId);
        item.Click += (_, _) => InvokeMenuCommand(command);

        return item;
    }

    private void InvokeMenuCommand(ContextFlyoutCommandEntry command)
    {
        command.Command?.Execute(command.CommandParameter);
        Hide();
    }

    /// <summary>
    /// Drops leading, duplicate, and trailing separators, so callers never have to normalize.
    /// </summary>
    private static List<ContextFlyoutMenuEntry> NormalizeSeparators(IReadOnlyList<ContextFlyoutMenuEntry> entries)
    {
        var filterEntries = entries
            .Select(entry => new ContextFlyoutFilterEntry(entry is ContextFlyoutSeparatorEntry))
            .ToList();

        return ContextFlyoutFilter.GetVisibleIndexes(filterEntries, string.Empty)
            .Select(index => entries[index])
            .ToList();
    }

    private static IconElement? CreateIcon(ContextFlyoutIcon? icon)
    {
        if (icon is null || string.IsNullOrEmpty(icon.Glyph))
        {
            return null;
        }

        var element = new WinoFontIcon { Glyph = icon.Glyph };
        if (AccountProfilePictureRenderer.TryParseColor(icon.ForegroundHex, out var skiaColor))
        {
            element.Foreground = new SolidColorBrush(Color.FromArgb(skiaColor.Alpha, skiaColor.Red, skiaColor.Green, skiaColor.Blue));
        }

        return element;
    }

    /// <summary>
    /// A cascading submenu with a search row. The tree is shown as-is until a query is typed;
    /// then the flattened matches take its place, each with its breadcrumb in the accelerator
    /// slot. Enter runs the first match.
    /// </summary>
    private sealed class SearchableMenu
    {
        private readonly WinoContextFlyout _owner;
        private readonly MenuFlyout _flyout;
        private readonly ContextFlyoutSubMenuEntry _subMenu;
        private readonly WinoContextFlyoutSearchItem _searchItem;
        private readonly List<MenuFlyoutItemBase> _treeItems = [];
        private readonly List<MenuFlyoutItem> _resultItems = [];
        private readonly MenuFlyoutItem _noResultsItem;
        private IReadOnlyList<ContextFlyoutSearchCandidate> _candidates = Array.Empty<ContextFlyoutSearchCandidate>();
        private List<ContextFlyoutFilterEntry> _filterEntries = [];

        public SearchableMenu(WinoContextFlyout owner, MenuFlyout flyout, ContextFlyoutSubMenuEntry subMenu)
        {
            _owner = owner;
            _flyout = flyout;
            _subMenu = subMenu;
            _searchItem = new WinoContextFlyoutSearchItem { PlaceholderText = owner.SearchPlaceholderText };
            _noResultsItem = new MenuFlyoutItem
            {
                Text = owner.NoResultsText,
                IsEnabled = false,
                MinHeight = RowHeight,
                Visibility = Visibility.Collapsed,
            };
            AutomationProperties.SetAutomationId(_noResultsItem, "WinoContextFlyoutEmptyText");
            owner.ApplyLanguage(_searchItem);
            owner.ApplyLanguage(_noResultsItem);
        }

        [DynamicWindowsRuntimeCast(typeof(MenuFlyoutItem))]
        public void Populate()
        {
            _flyout.Items.Add(_searchItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var entry in NormalizeSeparators(_subMenu.Items))
            {
                var item = _owner.CreateMenuFlyoutItem(entry);
                _treeItems.Add(item);
                _flyout.Items.Add(item);
            }

            _candidates = ContextFlyoutSearch.Collect(_subMenu.Items);
            _filterEntries = _candidates
                .Select(candidate => new ContextFlyoutFilterEntry(
                    false,
                    candidate.Source.Text,
                    candidate.Breadcrumb,
                    candidate.Source.SearchKeywords))
                .ToList();

            foreach (var candidate in _candidates)
            {
                var item = (MenuFlyoutItem)_owner.CreateMenuFlyoutItem(candidate.Source);
                item.KeyboardAcceleratorTextOverride = candidate.Breadcrumb;
                item.Visibility = Visibility.Collapsed;
                _resultItems.Add(item);
                _flyout.Items.Add(item);
            }

            _flyout.Items.Add(_noResultsItem);
            _searchItem.QueryChanged += ApplyFilter;
            _searchItem.Submitted += RunFirstMatch;
        }

        public void Reset()
        {
            _searchItem.Reset();
            ApplyFilter(string.Empty);
        }

        public void FocusSearch() => _searchItem.FocusSearch();

        private void ApplyFilter(string query)
        {
            var isFiltering = !string.IsNullOrWhiteSpace(query);
            foreach (var item in _treeItems)
            {
                item.Visibility = isFiltering ? Visibility.Collapsed : Visibility.Visible;
            }

            var visibleIndexes = isFiltering
                ? ContextFlyoutFilter.GetVisibleIndexes(_filterEntries, query)
                : Array.Empty<int>();
            for (var index = 0; index < _resultItems.Count; index++)
            {
                _resultItems[index].Visibility = visibleIndexes.Contains(index) ? Visibility.Visible : Visibility.Collapsed;
            }

            _noResultsItem.Visibility = isFiltering && visibleIndexes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RunFirstMatch()
        {
            for (var index = 0; index < _resultItems.Count; index++)
            {
                if (_resultItems[index].Visibility != Visibility.Visible || !_candidates[index].Source.CanExecute())
                {
                    continue;
                }

                _flyout.Hide();
                _owner.InvokeMenuCommand(_candidates[index].Source);
                return;
            }
        }
    }
}
