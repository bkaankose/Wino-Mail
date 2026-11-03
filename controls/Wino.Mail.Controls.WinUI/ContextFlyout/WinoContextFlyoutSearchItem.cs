using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT;

namespace Wino.Mail.Controls.ContextFlyout;

/// <summary>
/// Menu row that hosts the search box of a searchable submenu. MenuFlyoutItemBase cannot be
/// derived from, so this re-templates MenuFlyoutItem and suppresses its own invoke paths: a
/// MenuFlyoutItem raises Click and closes the menu on pointer release, Enter and Space.
/// </summary>
public sealed partial class WinoContextFlyoutSearchItem : MenuFlyoutItem
{
    private const string SearchBoxPart = "PART_SearchBox";

    private TextBox? _searchBox;

    public WinoContextFlyoutSearchItem()
    {
        DefaultStyleKey = typeof(WinoContextFlyoutSearchItem);
        IsTabStop = false;
    }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string PlaceholderText { get; set; }

    public event Action<string>? QueryChanged;

    public event Action? Submitted;

    public string Query => _searchBox?.Text ?? string.Empty;

    [DynamicWindowsRuntimeCast(typeof(TextBox))]
    protected override void OnApplyTemplate()
    {
        if (_searchBox is not null)
        {
            _searchBox.TextChanged -= SearchBoxTextChanged;
            _searchBox.KeyDown -= SearchBoxKeyDown;
        }

        base.OnApplyTemplate();

        _searchBox = GetTemplateChild(SearchBoxPart) as TextBox;
        if (_searchBox is not null)
        {
            _searchBox.TextChanged += SearchBoxTextChanged;
            _searchBox.KeyDown += SearchBoxKeyDown;
        }
    }

    public void Reset()
    {
        if (_searchBox is not null)
        {
            _searchBox.Text = string.Empty;
        }
    }

    public void FocusSearch() => _searchBox?.Focus(FocusState.Programmatic);

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
    }

    private void SearchBoxTextChanged(object sender, TextChangedEventArgs e) => QueryChanged?.Invoke(Query);

    private void SearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Submitted?.Invoke();
            e.Handled = true;
        }
    }
}
