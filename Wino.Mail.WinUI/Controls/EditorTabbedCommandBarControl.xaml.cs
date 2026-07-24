using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wino.Core.Domain;
using Wino.Editor;

namespace Wino.Mail.Controls;

public sealed partial class EditorTabbedCommandBarControl : UserControl, IEditorCommandControl
{
    [GeneratedDependencyProperty]
    public partial IEditorCommandTarget? CommandTarget { get; set; }

    [GeneratedDependencyProperty]
    public partial object? PaneCustomContent { get; set; }

    [GeneratedDependencyProperty]
    public partial object? InsertCustomContent { get; set; }

    [GeneratedDependencyProperty]
    public partial object? OptionsCustomContent { get; set; }

    [GeneratedDependencyProperty]
    public partial EditorColorOption? SelectedTextColorOption { get; set; }

    [GeneratedDependencyProperty]
    public partial EditorColorOption? SelectedHighlightColorOption { get; set; }

    private bool _isApplyingState;
    private IEditorCommandTarget? _subscribedTarget;
    private static readonly SolidColorBrush TransparentBrush = new(EditorColorOption.ParseColorValue(null));
    private IReadOnlyList<EditorColorOption> _textColorOptions = Array.Empty<EditorColorOption>();
    private IReadOnlyList<EditorColorOption> _highlightColorOptions = Array.Empty<EditorColorOption>();

    public Brush SelectedTextColorBrush => SelectedTextColorOption?.Brush ?? TransparentBrush;
    public Brush SelectedHighlightColorBrush => SelectedHighlightColorOption?.Brush ?? TransparentBrush;

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
            _subscribedTarget.ShortcutRequested -= CommandTarget_ShortcutRequested;
        }

        _subscribedTarget = target;

        if (_subscribedTarget != null)
        {
            _subscribedTarget.StateChanged += CommandTarget_StateChanged;
            _subscribedTarget.ShortcutRequested += CommandTarget_ShortcutRequested;
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
        _subscribedTarget.ShortcutRequested -= CommandTarget_ShortcutRequested;
        _subscribedTarget = null;
    }

    partial void OnCommandTargetChanged(IEditorCommandTarget? newValue)
    {
        AttachCommandTarget(newValue);
    }

    partial void OnSelectedTextColorOptionChanged(EditorColorOption? newValue) => Bindings.Update();

    partial void OnSelectedHighlightColorOptionChanged(EditorColorOption? newValue) => Bindings.Update();

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

    private void CommandTarget_ShortcutRequested(object? sender, EditorShortcutKind shortcut)
    {
        if (shortcut == EditorShortcutKind.OpenLinkDialog)
        {
            _ = ShowLinkDialogAsync();
        }
    }

    private void ApplyCapabilities(EditorCapabilities capabilities)
    {
        var wasApplyingState = _isApplyingState;
        _isApplyingState = true;

        try
        {
            var fonts = capabilities.Fonts.ToArray();
            var fontSizes = capabilities.FontSizes.ToArray();
            var alignments = capabilities.Alignments.ToArray();
            var paragraphStyles = capabilities.ParagraphStyles.ToArray();
            var lineHeights = capabilities.LineHeights.ToArray();

            SetItemsSourceIfChanged(FontFamilyComboBox, fonts);
            SetItemsSourceIfChanged(FontSizeComboBox, fontSizes);
            SetItemsSourceIfChanged(AlignmentComboBox, alignments);
            SetItemsSourceIfChanged(ParagraphStyleComboBox, paragraphStyles);
            _textColorOptions = capabilities.TextColors.ToArray();
            _highlightColorOptions = capabilities.HighlightColors.ToArray();
            SetItemsSourceIfChanged(TextColorComboBox, _textColorOptions);
            SetItemsSourceIfChanged(HighlightColorComboBox, _highlightColorOptions);
            SetItemsSourceIfChanged(LineHeightComboBox, lineHeights);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error applying capabilities: {ex}");
        }
        finally
        {
            _isApplyingState = wasApplyingState;
        }
    }

    private static void SetItemsSourceIfChanged(ItemsControl control, object itemsSource)
    {
        if (!ReferenceEquals(control.ItemsSource, itemsSource))
        {
            control.ItemsSource = itemsSource;
        }
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
        RemoveLinkButton.IsEnabled = !string.IsNullOrWhiteSpace(state.LinkUrl) ||
            (state.IsImageSelected && !string.IsNullOrWhiteSpace(state.ImageLinkUrl));
        RemoveLinkButton.Visibility = RemoveLinkButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ImagePropertiesButton.Visibility = state.IsImageSelected ? Visibility.Visible : Visibility.Collapsed;
        BuiltInToolbarButton.IsChecked = state.IsBuiltInToolbarVisible;
        SpellCheckButton.IsChecked = state.IsSpellCheckEnabled;

        AlignmentComboBox.SelectedItem = state.Alignment;
        FontFamilyComboBox.SelectedItem = MatchStringItem(FontFamilyComboBox.ItemsSource, state.FontFamily);
        FontSizeComboBox.SelectedItem = MatchValueItem<int>(FontSizeComboBox.ItemsSource, state.FontSize);
        LineHeightComboBox.SelectedItem = MatchStringItem(LineHeightComboBox.ItemsSource, state.LineHeight);
        ParagraphStyleComboBox.SelectedItem = MatchParagraphItem(state.ParagraphStyle);
        SelectedTextColorOption = ResolveColorOption(_textColorOptions, state.TextColor);
        SelectedHighlightColorOption = ResolveColorOption(_highlightColorOptions, state.HighlightColor);
        TextColorComboBox.SelectedItem = SelectedTextColorOption;
        HighlightColorComboBox.SelectedItem = SelectedHighlightColorOption;

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

    private static EditorColorOption? MatchColorItem(IEnumerable<EditorColorOption> colors, string? value)
    {
        var normalizedValue = value ?? string.Empty;
        var matchedByValue = colors.FirstOrDefault(item => string.Equals(item.Value, normalizedValue, StringComparison.OrdinalIgnoreCase));
        if (matchedByValue != null)
        {
            return matchedByValue;
        }

        var targetColor = EditorColorOption.ParseColorValue(value);
        return colors.FirstOrDefault(item => item.Brush.Color.Equals(targetColor));
    }

    private static EditorColorOption? ResolveColorOption(IEnumerable<EditorColorOption> colors, string? value)
    {
        var colorOptions = colors.ToList();
        var matchedColor = MatchColorItem(colors, value);
        if (matchedColor != null)
        {
            return matchedColor;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return colorOptions.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Value));
        }

        var targetColor = EditorColorOption.ParseColorValue(value);
        var selectableColors = colorOptions
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToList();

        if (selectableColors.Count == 0)
        {
            return colorOptions.FirstOrDefault();
        }

        return selectableColors
            .OrderBy(item => GetColorDistance(item.Brush.Color, targetColor))
            .First();
    }

    private static int GetColorDistance(Color left, Color right)
    {
        var redDiff = left.R - right.R;
        var greenDiff = left.G - right.G;
        var blueDiff = left.B - right.B;
        return (redDiff * redDiff) + (greenDiff * greenDiff) + (blueDiff * blueDiff);
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
    private async void ClearFormattingButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ClearFormatting());
    private async void BulletListButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleUnorderedList());
    private async void OrderedListButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.ToggleOrderedList());
    private async void IndentButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.Indent());
    private async void OutdentButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.Outdent());
    private async void ImageButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(EditorCommand.InsertImage());
    private async void ImagePropertiesButton_Click(object sender, RoutedEventArgs e) => await ShowImagePropertiesDialogAsync();
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
        if (_isApplyingState || e.AddedItems.FirstOrDefault() is not int fontSize)
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
        SelectedTextColorOption = TextColorComboBox.SelectedItem as EditorColorOption;

        if (_isApplyingState || SelectedTextColorOption == null)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetTextColor(SelectedTextColorOption.Value));
    }

    private async void HighlightColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedHighlightColorOption = HighlightColorComboBox.SelectedItem as EditorColorOption;

        if (_isApplyingState || SelectedHighlightColorOption == null)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetHighlightColor(SelectedHighlightColorOption.Value));
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

    private async void LinkButton_Click(object sender, RoutedEventArgs e) => await ShowLinkDialogAsync();

    private async Task ShowLinkDialogAsync()
    {
        if (CommandTarget == null)
        {
            return;
        }

        var currentState = CommandTarget.CurrentState;
        var isImageSelected = currentState.IsImageSelected;
        var urlTextBox = new TextBox
        {
            Header = Translator.Composer_LinkUrl,
            Text = isImageSelected ? currentState.ImageLinkUrl ?? string.Empty : currentState.LinkUrl ?? string.Empty,
            PlaceholderText = Translator.Composer_LinkUrlPlaceholder
        };
        var textTextBox = new TextBox
        {
            Header = Translator.Composer_LinkText,
            Text = currentState.SelectedText ?? string.Empty,
            PlaceholderText = Translator.Composer_LinkTextPlaceholder,
            Visibility = isImageSelected ? Visibility.Collapsed : Visibility.Visible
        };
        var openInNewWindow = new CheckBox
        {
            Content = Translator.Composer_OpenLinkInNewWindow,
            IsChecked = true
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isImageSelected ? Translator.Composer_EditImageLink : Translator.Composer_InsertLink,
            PrimaryButtonText = Translator.Buttons_Apply,
            CloseButtonText = Translator.Buttons_Cancel,
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

    private async Task ShowImagePropertiesDialogAsync()
    {
        if (CommandTarget?.CurrentState is not { IsImageSelected: true } currentState)
        {
            return;
        }

        var altTextBox = new TextBox
        {
            Header = Translator.Composer_ImageAltText,
            Text = currentState.ImageAltText ?? string.Empty,
            PlaceholderText = Translator.Composer_ImageAltTextPlaceholder
        };
        var urlTextBox = new TextBox
        {
            Header = Translator.Composer_LinkUrl,
            Text = currentState.ImageLinkUrl ?? string.Empty,
            PlaceholderText = Translator.Composer_LinkUrlPlaceholder
        };
        var openInNewWindow = new CheckBox
        {
            Content = Translator.Composer_OpenLinkInNewWindow,
            IsChecked = true
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Translator.Composer_ImageProperties,
            PrimaryButtonText = Translator.Buttons_Apply,
            CloseButtonText = Translator.Buttons_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    altTextBox,
                    urlTextBox,
                    openInNewWindow
                }
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ExecuteAsync(EditorCommand.SetImageProperties(new EditorImagePropertiesCommandArgs(
                altTextBox.Text.Trim(),
                string.IsNullOrWhiteSpace(urlTextBox.Text) ? null : urlTextBox.Text.Trim(),
                openInNewWindow.IsChecked == true)));
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



