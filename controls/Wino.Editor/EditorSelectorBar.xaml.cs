using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Wino.Editor;

public sealed partial class EditorSelectorBar : UserControl
{
    public static readonly DependencyProperty SelectedCategoryProperty = DependencyProperty.Register(
        nameof(SelectedCategory),
        typeof(EditorToolbarCategory),
        typeof(EditorSelectorBar),
        new PropertyMetadata(EditorToolbarCategory.Formatting, OnSelectionPropertyChanged));

    public static readonly DependencyProperty FormattingVisibilityProperty = RegisterVisibility(nameof(FormattingVisibility));
    public static readonly DependencyProperty InsertVisibilityProperty = RegisterVisibility(nameof(InsertVisibility));
    public static readonly DependencyProperty TableVisibilityProperty = RegisterVisibility(nameof(TableVisibility));
    public static readonly DependencyProperty SecurityVisibilityProperty = RegisterVisibility(nameof(SecurityVisibility));

    public EditorSelectorBar()
    {
        InitializeComponent();
    }

    public event EventHandler<EditorToolbarCategory>? SelectionChanged;

    public EditorToolbarCategory SelectedCategory
    {
        get => (EditorToolbarCategory)GetValue(SelectedCategoryProperty);
        set => SetValue(SelectedCategoryProperty, value);
    }

    public Visibility FormattingVisibility { get => (Visibility)GetValue(FormattingVisibilityProperty); set => SetValue(FormattingVisibilityProperty, value); }
    public Visibility InsertVisibility { get => (Visibility)GetValue(InsertVisibilityProperty); set => SetValue(InsertVisibilityProperty, value); }
    public Visibility TableVisibility { get => (Visibility)GetValue(TableVisibilityProperty); set => SetValue(TableVisibilityProperty, value); }
    public Visibility SecurityVisibility { get => (Visibility)GetValue(SecurityVisibilityProperty); set => SetValue(SecurityVisibilityProperty, value); }

    private static DependencyProperty RegisterVisibility(string name) => DependencyProperty.Register(
        name,
        typeof(Visibility),
        typeof(EditorSelectorBar),
        new PropertyMetadata(Visibility.Visible, OnSelectionPropertyChanged));

    private static void OnSelectionPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((EditorSelectorBar)sender).UpdateVisualState();

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string value } || !Enum.TryParse(value, out EditorToolbarCategory category)) return;
        SelectedCategory = category;
        SelectionChanged?.Invoke(this, category);
    }

    private void EditorSelectorBar_Loaded(object sender, RoutedEventArgs e) => UpdateVisualState();

    private void UpdateVisualState()
    {
        if (FormattingButton is null) return;
        FormattingButton.Visibility = FormattingVisibility;
        InsertButton.Visibility = InsertVisibility;
        TableButton.Visibility = TableVisibility;
        SecurityButton.Visibility = SecurityVisibility;
        FormattingButton.IsChecked = SelectedCategory == EditorToolbarCategory.Formatting;
        InsertButton.IsChecked = SelectedCategory == EditorToolbarCategory.Insert;
        TableButton.IsChecked = SelectedCategory == EditorToolbarCategory.Table;
        SecurityButton.IsChecked = SelectedCategory == EditorToolbarCategory.Security;
    }
}
