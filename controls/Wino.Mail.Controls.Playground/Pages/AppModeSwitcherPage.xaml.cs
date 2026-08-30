using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wino.Mail.Controls.AppModeSwitcher;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class AppModeSwitcherPage : Page
{
    /// <summary>
    /// The last entry in the selection list is the "nothing selected" state, which the
    /// control expresses as -1 rather than as another item.
    /// </summary>
    private const int NoSelectionOptionIndex = 4;

    /// <summary>
    /// Enough spread to show that the resting chip only changes hue: a dark accent and a
    /// warm one have to land at the same weight behind the glyphs.
    /// </summary>
    private static readonly (string Name, Color Color)[] Accents =
    [
        ("Windows blue", Color.FromArgb(255, 0x00, 0x78, 0xD4)),
        ("Teal", Color.FromArgb(255, 0x03, 0x83, 0x87)),
        ("Plum", Color.FromArgb(255, 0x87, 0x64, 0xB8)),
        ("Orange", Color.FromArgb(255, 0xCA, 0x50, 0x10)),
        ("Green", Color.FromArgb(255, 0x10, 0x7C, 0x10))
    ];

    public AppModeSwitcherPage()
    {
        InitializeComponent();

        BuildAccentSwatches();
    }

    private void StateChanged(object sender, RoutedEventArgs e) => ApplyState();

    private void StateChanged(object sender, TextChangedEventArgs e) => ApplyState();

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyState();

    private void SurfaceThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaneSurface is null || RailSurface is null || SurfaceThemeBox?.SelectedItem is not ComboBoxItem item)
            return;

        var theme = item.Tag as string switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        PaneSurface.RequestedTheme = theme;
        RailSurface.RequestedTheme = theme;
    }

    /// <summary>
    /// The control reports the invocation and leaves the selection alone, so the page has to
    /// move it. That is the same contract the shell honours, and driving the page this way
    /// exercises the events rather than only the properties.
    /// </summary>
    private void SwitcherModeInvoked(object? sender, WinoAppModeInvokedEventArgs e)
    {
        SettingsSelectedToggle.IsOn = false;
        SelectionButtons.SelectedIndex = e.Index;
    }

    private void SwitcherSettingsInvoked(object? sender, EventArgs e)
    {
        SelectionButtons.SelectedIndex = NoSelectionOptionIndex;
        SettingsSelectedToggle.IsOn = true;
    }

    private void ApplyState()
    {
        if (PaneSwitcher is null || RailSwitcher is null || SettingsLabelBox is null)
        {
            return;
        }

        var selectedIndex = SelectionButtons.SelectedIndex == NoSelectionOptionIndex
            ? -1
            : SelectionButtons.SelectedIndex;

        foreach (var switcher in EnumerateSwitchers())
        {
            switcher.SelectedIndex = selectedIndex;
            switcher.IsSettingsSelected = SettingsSelectedToggle.IsOn;
            switcher.IsSettingsVisible = SettingsVisibleToggle.IsOn;
            switcher.SettingsLabel = SettingsLabelBox.Text;
        }
    }

    private IEnumerable<WinoAppModeSwitcher> EnumerateSwitchers()
    {
        yield return PaneSwitcher;
        yield return RailSwitcher;
    }

    private void BuildAccentSwatches()
    {
        foreach (var (name, color) in Accents)
        {
            var swatch = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                Tag = color
            };

            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += AccentSwatchClicked;

            AccentSwatches.Children.Add(swatch);
        }
    }

    /// <summary>
    /// The app's theme service changes the accent by mutating the application resource in
    /// place, so the playground does the same rather than inventing a second route into the
    /// control.
    /// </summary>
    private void AccentSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Color color })
            return;

        Application.Current.Resources["SystemAccentColor"] = color;

        foreach (var switcher in EnumerateSwitchers())
        {
            switcher.RefreshAccent();
        }
    }
}
