using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls;

public sealed partial class EditorTabbedCommandBarControl : UserControl, IEditorCommandControl
{
    public static readonly DependencyProperty CommandTargetProperty = DependencyProperty.Register(
        nameof(CommandTarget),
        typeof(IEditorCommandTarget),
        typeof(EditorTabbedCommandBarControl),
        new PropertyMetadata(null, OnCommandTargetChanged));

    public static readonly DependencyProperty PaneCustomContentProperty = DependencyProperty.Register(
        nameof(PaneCustomContent),
        typeof(object),
        typeof(EditorTabbedCommandBarControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty InsertCustomContentProperty = DependencyProperty.Register(
        nameof(InsertCustomContent),
        typeof(object),
        typeof(EditorTabbedCommandBarControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty OptionsCustomContentProperty = DependencyProperty.Register(
        nameof(OptionsCustomContent),
        typeof(object),
        typeof(EditorTabbedCommandBarControl),
        new PropertyMetadata(null));

    private bool _isApplyingState;
    private IEditorCommandTarget? _subscribedTarget;

    public IEditorCommandTarget? CommandTarget
    {
        get => (IEditorCommandTarget?)GetValue(CommandTargetProperty);
        set => SetValue(CommandTargetProperty, value);
    }

    public object? PaneCustomContent
    {
        get => GetValue(PaneCustomContentProperty);
        set => SetValue(PaneCustomContentProperty, value);
    }

    public object? InsertCustomContent
    {
        get => GetValue(InsertCustomContentProperty);
        set => SetValue(InsertCustomContentProperty, value);
    }

    public object? OptionsCustomContent
    {
        get => GetValue(OptionsCustomContentProperty);
        set => SetValue(OptionsCustomContentProperty, value);
    }

    public EditorTabbedCommandBarControl()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void AttachCommandTarget(IEditorCommandTarget? target)
    {
        if (_subscribedTarget == target)
        {
            return;
        }

        if (_subscribedTarget != null)
        {
            _subscribedTarget.StateChanged -= CommandTarget_StateChanged;
            _subscribedTarget.CapabilitiesChanged -= CommandTarget_CapabilitiesChanged;
        }

        _subscribedTarget = target;

        if (_subscribedTarget != null)
        {
            _subscribedTarget.StateChanged += CommandTarget_StateChanged;
            _subscribedTarget.CapabilitiesChanged += CommandTarget_CapabilitiesChanged;
            ApplyCapabilities(_subscribedTarget.Capabilities);
            ApplyState(_subscribedTarget.CurrentState);
        }
    }

    public void DetachCommandTarget()
    {
        if (_subscribedTarget == null)
        {
            return;
        }

        _subscribedTarget.StateChanged -= CommandTarget_StateChanged;
        _subscribedTarget.CapabilitiesChanged -= CommandTarget_CapabilitiesChanged;
        _subscribedTarget = null;
    }

    private static void OnCommandTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (EditorTabbedCommandBarControl)d;
        control.AttachCommandTarget((IEditorCommandTarget?)e.NewValue);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachCommandTarget(CommandTarget);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachCommandTarget();
    }

    private void CommandTarget_StateChanged(object? sender, EditorState e)
    {
        ApplyState(e);
    }

    private void CommandTarget_CapabilitiesChanged(object? sender, EditorCapabilities e)
    {
        ApplyCapabilities(e);
    }

    private void ApplyCapabilities(EditorCapabilities capabilities)
    {
        FontFamilyComboBox.ItemsSource = capabilities.Fonts;
        FontSizeComboBox.ItemsSource = capabilities.FontSizes;
        AlignmentComboBox.ItemsSource = capabilities.Alignments;
        ParagraphStyleComboBox.ItemsSource = capabilities.ParagraphStyles;
        TextColorComboBox.ItemsSource = capabilities.TextColors;
        HighlightColorComboBox.ItemsSource = capabilities.HighlightColors;
        LineHeightComboBox.ItemsSource = capabilities.LineHeights;
    }

    private void ApplyState(EditorState state)
    {
        _isApplyingState = true;

        BoldButton.IsChecked = state.IsBold;
        ItalicButton.IsChecked = state.IsItalic;
        UnderlineButton.IsChecked = state.IsUnderline;
        StrikeButton.IsChecked = state.IsStrikethrough;
        BulletListButton.IsChecked = state.IsUnorderedList;
        OrderedListButton.IsChecked = state.IsOrderedList;
        IndentButton.IsEnabled = state.CanIndent;
        OutdentButton.IsEnabled = state.CanOutdent;
        RemoveLinkButton.IsEnabled = !string.IsNullOrWhiteSpace(state.LinkUrl);
        RemoveLinkButton.Visibility = RemoveLinkButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        BuiltInToolbarButton.IsChecked = state.IsBuiltInToolbarVisible;
        SpellCheckButton.IsChecked = state.IsSpellCheckEnabled;

        AlignmentComboBox.SelectedItem = state.Alignment;
        FontFamilyComboBox.SelectedItem = MatchStringItem(FontFamilyComboBox.ItemsSource, state.FontFamily);
        FontSizeComboBox.SelectedItem = MatchValueItem<int>(FontSizeComboBox.ItemsSource, state.FontSize);
        LineHeightComboBox.SelectedItem = MatchStringItem(LineHeightComboBox.ItemsSource, state.LineHeight);
        ParagraphStyleComboBox.SelectedItem = MatchParagraphItem(state.ParagraphStyle);
        TextColorComboBox.SelectedItem = MatchColorItem(TextColorComboBox.ItemsSource, state.TextColor);
        HighlightColorComboBox.SelectedItem = MatchColorItem(HighlightColorComboBox.ItemsSource, state.HighlightColor);

        _isApplyingState = false;
    }

    private static object? MatchStringItem(object? itemsSource, string? value)
    {
        if (itemsSource is IEnumerable<string> strings)
        {
            return strings.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static object? MatchValueItem<T>(object? itemsSource, T? value) where T : struct
    {
        if (!value.HasValue || itemsSource is not IEnumerable<T> values)
        {
            return null;
        }

        foreach (var item in values)
        {
            if (EqualityComparer<T>.Default.Equals(item, value.Value))
            {
                return item;
            }
        }

        return null;
    }

    private object? MatchParagraphItem(string? tag)
    {
        if (ParagraphStyleComboBox.ItemsSource is not IEnumerable<EditorParagraphStyleOption> styles)
        {
            return null;
        }

        return styles.FirstOrDefault(item => string.Equals(item.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static object? MatchColorItem(object? itemsSource, string? value)
    {
        if (itemsSource is not IEnumerable<EditorColorOption> colors)
        {
            return null;
        }

        return colors.FirstOrDefault(item => string.Equals(item.Value, value ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ExecuteAsync(EditorCommand command)
    {
        if (_isApplyingState || CommandTarget == null)
        {
            return;
        }

        await CommandTarget.ExecuteCommandAsync(command);
    }

    private async void BoldButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleBold());
    private async void ItalicButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleItalic());
    private async void UnderlineButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleUnderline());
    private async void StrikeButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleStrikethrough());
    private async void BulletListButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleUnorderedList());
    private async void OrderedListButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleOrderedList());
    private async void IndentButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.Indent());
    private async void OutdentButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.Outdent());
    private async void ImageButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.InsertImage());
    private async void EmojiButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.InsertEmoji());
    private async void RemoveLinkButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.RemoveLink());

    private async void AlignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || AlignmentComboBox.SelectedItem is not EditorTextAlignment alignment)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetAlignment(alignment));
    }

    private async void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || FontFamilyComboBox.SelectedItem is not string fontFamily || string.IsNullOrWhiteSpace(fontFamily))
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetFontFamily(fontFamily));
    }

    private async void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || FontSizeComboBox.SelectedItem is not int fontSize)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetFontSize(fontSize));
    }

    private async void ParagraphStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || ParagraphStyleComboBox.SelectedItem is not EditorParagraphStyleOption paragraphStyle)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetParagraphStyle(paragraphStyle.Tag));
    }

    private async void TextColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || TextColorComboBox.SelectedItem is not EditorColorOption color)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetTextColor(color.Value));
    }

    private async void HighlightColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || HighlightColorComboBox.SelectedItem is not EditorColorOption color)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetHighlightColor(color.Value));
    }

    private async void LineHeightComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || LineHeightComboBox.SelectedItem is not string lineHeight || string.IsNullOrWhiteSpace(lineHeight))
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetLineHeight(lineHeight));
    }

    private async void BuiltInToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(EditorCommand.ToggleBuiltInToolbar(BuiltInToolbarButton.IsChecked == true));
    }

    private async void SpellCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(EditorCommand.ToggleSpellCheck(SpellCheckButton.IsChecked == true));
    }

    private async void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandTarget == null)
        {
            return;
        }

        var currentState = CommandTarget.CurrentState;
        var urlTextBox = new TextBox
        {
            Header = "URL",
            Text = currentState.LinkUrl ?? string.Empty,
            PlaceholderText = "https://example.com"
        };
        var textTextBox = new TextBox
        {
            Header = "Text",
            Text = currentState.SelectedText ?? string.Empty,
            PlaceholderText = "Link text"
        };
        var openInNewWindow = new CheckBox
        {
            Content = "Open in new window",
            IsChecked = true
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Insert link",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    urlTextBox,
                    textTextBox,
                    openInNewWindow
                }
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(urlTextBox.Text))
        {
            await ExecuteAsync(EditorCommand.InsertLink(new EditorLinkCommandArgs(urlTextBox.Text.Trim(), textTextBox.Text.Trim(), openInNewWindow.IsChecked == true)));
        }
    }

    private async void TableButton_Click(object sender, RoutedEventArgs e)
    {
        var rowsBox = new NumberBox
        {
            Header = "Rows",
            Minimum = 1,
            Maximum = 10,
            SmallChange = 1,
            Value = 2
        };
        var columnsBox = new NumberBox
        {
            Header = "Columns",
            Minimum = 1,
            Maximum = 10,
            SmallChange = 1,
            Value = 2
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Insert table",
            PrimaryButtonText = "Insert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    rowsBox,
                    columnsBox
                }
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ExecuteAsync(EditorCommand.InsertTable(new EditorTableCommandArgs((int)Math.Max(1, rowsBox.Value), (int)Math.Max(1, columnsBox.Value))));
        }
    }
}



