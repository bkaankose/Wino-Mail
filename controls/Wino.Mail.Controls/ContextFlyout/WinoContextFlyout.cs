using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Searchable, pageable context flyout. Menus are supplied as <see cref="ContextFlyoutMenuEntry"/>
/// definitions, never as XAML menu types: this control renders and filters a definition, it never
/// builds one.
/// </summary>
public partial class WinoContextFlyout : FlyoutBase
{
    public const double PresenterWidth = 250;

    private WinoContextFlyoutPresenter? _presenter;

    public WinoContextFlyout()
    {
        Opened += OnOpened;
        Closed += OnClosed;
    }

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

    internal IReadOnlyList<ContextFlyoutMenuEntry> RootItems => ItemsSource ?? [];

    internal IReadOnlyList<ContextFlyoutHeaderEntry> HeaderItems => HeaderItemsSource ?? [];

    internal void Close() => Hide();

    private void OnOpened(object? sender, object e) => _presenter?.PrepareForOpen();

    private void OnClosed(object? sender, object e) => _presenter?.PrepareForClose();
}
