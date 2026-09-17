using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Searchable, pageable context flyout. Menus are supplied as <see cref="ContextFlyoutMenuEntry"/>
/// models or declared with the Wino context-flyout definition types in XAML. The presenter renders
/// and filters those definitions; it does not host platform menu items.
/// </summary>
[ContentProperty(Name = nameof(Items))]
public partial class WinoContextFlyout : FlyoutBase
{
    private readonly PointerEventHandler _presenterPointerPressedHandler = OnPresenterPointerPressed;
    private WinoContextFlyoutPresenter? _presenter;

    public WinoContextFlyout()
    {
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>
    /// Declarative menu definitions. Use this collection when authoring the flyout in XAML.
    /// <see cref="ItemsSource"/> takes precedence when both APIs are populated.
    /// </summary>
    public ObservableCollection<WinoContextFlyoutItemBase> Items { get; } = [];

    [GeneratedDependencyProperty]
    public partial IReadOnlyList<ContextFlyoutMenuEntry>? ItemsSource { get; set; }

    /// <summary>
    /// Frequent root actions shown above the item list. Hidden on nested pages.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial IReadOnlyList<ContextFlyoutHeaderEntry>? HeaderItemsSource { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSearchEnabled { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Search")]
    public partial string SearchPlaceholderText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "No results found")]
    public partial string NoResultsText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Language { get; set; }

    protected override Control CreatePresenter()
    {
        _presenter = new WinoContextFlyoutPresenter(this);
        return _presenter;
    }

    internal IReadOnlyList<ContextFlyoutMenuEntry> RootItems =>
        ItemsSource ?? Items.Select(static item => item.CreateEntry()).ToArray();

    internal IReadOnlyList<ContextFlyoutHeaderEntry> HeaderItems => HeaderItemsSource ?? [];

    internal void Close() => Hide();

    private void OnOpened(object? sender, object e)
    {
        if (_presenter is null)
        {
            return;
        }

        // FlyoutBase can reuse the same presenter. Reattach transient input handling for each open.
        _presenter.RemoveHandler(UIElement.PointerPressedEvent, _presenterPointerPressedHandler);
        _presenter.AddHandler(UIElement.PointerPressedEvent, _presenterPointerPressedHandler, true);
        _presenter.PrepareForOpen();
    }

    private void OnClosed(object? sender, object e)
    {
        if (_presenter is not null)
        {
            _presenter.PrepareForClose();
            _presenter.RemoveHandler(UIElement.PointerPressedEvent, _presenterPointerPressedHandler);
        }
    }

    private static void OnPresenterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsXButton1Pressed
            && sender is WinoContextFlyoutPresenter presenter
            && presenter.TryNavigateBack())
        {
            e.Handled = true;
        }
    }
}
