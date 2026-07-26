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

    private const string LineSpacingGroupName = "ComposerLineSpacing";

    private bool _isApplyingState;
    private IEditorCommandTarget? _subscribedTarget;
    private static readonly SolidColorBrush TransparentBrush = new(EditorColorOption.ParseColorValue(null));
    private IReadOnlyList<EditorColorOption> _textColorOptions = Array.Empty<EditorColorOption>();
    private IReadOnlyList<EditorColorOption> _highlightColorOptions = Array.Empty<EditorColorOption>();
    private readonly List<ICommandBarElement> _injectedInsertCommands = [];
    private readonly List<ICommandBarElement> _injectedOptionsCommands = [];

    public Brush SelectedTextColorBrush => SelectedTextColorOption?.Brush ?? TransparentBrush;
    public Brush SelectedHighlightColorBrush => SelectedHighlightColorOption?.Brush ?? TransparentBrush;
    public string SelectedTextColorName => SelectedTextColorOption?.Name ?? string.Empty;
    public string SelectedHighlightColorName => SelectedHighlightColorOption?.Name ?? string.Empty;

    // Shortcuts are part of the tooltip so the toolbar teaches them instead of hiding them.
    public string BoldTooltip => $"{Translator.Composer_Bold} (Ctrl+B)";
    public string ItalicTooltip => $"{Translator.Composer_Italic} (Ctrl+I)";
    public string UnderlineTooltip => $"{Translator.Composer_Underline} (Ctrl+U)";
    public string LinkTooltip => $"{Translator.Composer_InsertLink} (Ctrl+K)";

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

    partial void OnInsertCustomContentChanged(object? newValue)
        => ApplyInjectedCommands(InsertTabItem, _injectedInsertCommands, newValue);

    partial void OnOptionsCustomContentChanged(object? newValue)
    {
        ApplyInjectedCommands(OptionsTabItem, _injectedOptionsCommands, newValue);

        // Nothing injected means nothing to separate from.
        OptionsInjectedCommandsSeparator.Visibility = _injectedOptionsCommands.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// The composer owns these commands, but they have to sit directly in the command bar. A command bar only
    /// lays out labels and builds its overflow menu for its own children, so anything nested in a container
    /// would show up with the wrong label position and never collapse when the window gets narrow.
    /// </summary>
    private static void ApplyInjectedCommands(CommandBar tabItem, List<ICommandBarElement> injectedCommands, object? content)
    {
        foreach (var injectedCommand in injectedCommands)
        {
            tabItem.PrimaryCommands.Remove(injectedCommand);
        }

        injectedCommands.Clear();

        if (content is not Panel panel)
        {
            return;
        }

        var commands = panel.Children.OfType<ICommandBarElement>().ToList();

        foreach (var command in commands)
        {
            panel.Children.Remove((UIElement)command);
            tabItem.PrimaryCommands.Insert(injectedCommands.Count, command);
            injectedCommands.Add(command);
        }
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
            var paragraphStyles = capabilities.ParagraphStyles.ToArray();

            SetItemsSourceIfChanged(FontFamilyComboBox, fonts);
            SetItemsSourceIfChanged(FontSizeComboBox, fontSizes);
            SetItemsSourceIfChanged(ParagraphStyleComboBox, paragraphStyles);
            _textColorOptions = capabilities.TextColors.ToArray();
            _highlightColorOptions = capabilities.HighlightColors.ToArray();
            SetItemsSourceIfChanged(TextColorGridView, _textColorOptions);
            SetItemsSourceIfChanged(HighlightColorGridView, _highlightColorOptions);

            BuildLineSpacingMenu(capabilities.LineHeights);
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

    private void BuildLineSpacingMenu(IReadOnlyList<string> lineHeights)
    {
        if (LineSpacingMenuFlyout.Items.Count == lineHeights.Count)
        {
            return;
        }

        LineSpacingMenuFlyout.Items.Clear();

        foreach (var lineHeight in lineHeights)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = GetLineSpacingName(lineHeight),
                GroupName = LineSpacingGroupName,
                Tag = lineHeight
            };

            item.Click += LineSpacingMenuItem_Click;
            LineSpacingMenuFlyout.Items.Add(item);
        }
    }

    private EditorTextAlignment GetAlignment(AppBarToggleButton button)
    {
        if (button == AlignCenterButton) return EditorTextAlignment.Center;
        if (button == AlignRightButton) return EditorTextAlignment.Right;
        if (button == AlignJustifyButton) return EditorTextAlignment.Justify;

        return EditorTextAlignment.Left;
    }

    /// <summary>
    /// The editor reports the raw CSS line-height, which is not something to show to a person as is.
    /// </summary>
    private static string GetLineSpacingName(string lineHeight)
        => string.IsNullOrWhiteSpace(lineHeight) || string.Equals(lineHeight, "normal", StringComparison.OrdinalIgnoreCase)
            ? Translator.Composer_LineSpacingDefault
            : lineHeight;

    private void ApplyState(EditorState state)
    {
        _isApplyingState = true;

        try
        {
            ApplyStateCore(state);
        }
        catch (Exception ex)
        {
            // A failure here must not leave _isApplyingState stuck, which would silently swallow every command.
            Debug.WriteLine($"Error applying editor state: {ex}");
        }
        finally
        {
            _isApplyingState = false;
        }
    }

    private void ApplyStateCore(EditorState state)
    {
        BoldButton.IsChecked = state.IsBold;
        ItalicButton.IsChecked = state.IsItalic;
        UnderlineButton.IsChecked = state.IsUnderline;
        StrikeButton.IsChecked = state.IsStrikethrough;
        BulletListButton.IsChecked = state.IsUnorderedList;
        OrderedListButton.IsChecked = state.IsOrderedList;
        IndentButton.IsEnabled = state.CanIndent;
        OutdentButton.IsEnabled = state.CanOutdent;

        // Contextual commands stay in place and grey out. Hiding them would make the row jump under the pointer.
        RemoveLinkButton.IsEnabled = !string.IsNullOrWhiteSpace(state.LinkUrl) ||
            (state.IsImageSelected && !string.IsNullOrWhiteSpace(state.ImageLinkUrl));
        ImagePropertiesButton.IsEnabled = state.IsImageSelected;

        SpellCheckButton.IsChecked = state.IsSpellCheckEnabled;

        ApplyAlignmentState(state.Alignment);
        ApplyLineSpacingState(state.LineHeight);

        FontFamilyComboBox.SelectedItem = MatchFontItem(state.FontFamily);
        FontSizeComboBox.SelectedItem = MatchValueItem<int>(FontSizeComboBox.ItemsSource, state.FontSize);
        ParagraphStyleComboBox.SelectedItem = MatchParagraphItem(state.ParagraphStyle);
        SelectedTextColorOption = ResolveColorOption(_textColorOptions, state.TextColor);
        SelectedHighlightColorOption = ResolveColorOption(_highlightColorOptions, state.HighlightColor);
        TextColorGridView.SelectedItem = SelectedTextColorOption;
        HighlightColorGridView.SelectedItem = SelectedHighlightColorOption;
    }

    private void ApplyAlignmentState(EditorTextAlignment alignment)
    {
        AlignLeftButton.IsChecked = alignment == EditorTextAlignment.Left;
        AlignCenterButton.IsChecked = alignment == EditorTextAlignment.Center;
        AlignRightButton.IsChecked = alignment == EditorTextAlignment.Right;
        AlignJustifyButton.IsChecked = alignment == EditorTextAlignment.Justify;
    }

    private void ApplyLineSpacingState(string? lineHeight)
    {
        foreach (var item in LineSpacingMenuFlyout.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = item.Tag is string itemValue && string.Equals(itemValue, lineHeight, StringComparison.OrdinalIgnoreCase);
        }
    }

    private object? MatchFontItem(string? value)
    {
        if (FontFamilyComboBox.ItemsSource is not IEnumerable<EditorFontFamilyOption> fonts)
        {
            return null;
        }

        return fonts.FirstOrDefault(item => string.Equals(item.DisplayName, value, StringComparison.OrdinalIgnoreCase));
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

    private async void AlignmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || sender is not AppBarToggleButton button)
        {
            return;
        }

        // Alignment is a choice, not a switch: clicking the checked button must not turn alignment off.
        button.IsChecked = true;

        await ExecuteAsync(EditorCommand.SetAlignment(GetAlignment(button)));
    }

    private async void LineSpacingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingState || sender is not RadioMenuFlyoutItem { Tag: string lineHeight } || string.IsNullOrWhiteSpace(lineHeight))
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetLineHeight(lineHeight));
    }

    private async void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingState || FontFamilyComboBox.SelectedItem is not EditorFontFamilyOption font)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetFontFamily(font.DisplayName));
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

    private async void TextColorGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedTextColorOption = TextColorGridView.SelectedItem as EditorColorOption;

        if (_isApplyingState || SelectedTextColorOption == null)
        {
            return;
        }

        TextColorFlyout.Hide();

        await ExecuteAsync(EditorCommand.SetTextColor(SelectedTextColorOption.Value));
    }

    private async void HighlightColorGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedHighlightColorOption = HighlightColorGridView.SelectedItem as EditorColorOption;

        if (_isApplyingState || SelectedHighlightColorOption == null)
        {
            return;
        }

        HighlightColorFlyout.Hide();

        await ExecuteAsync(EditorCommand.SetHighlightColor(SelectedHighlightColorOption.Value));
    }

    // The primary half of the split button re-applies the color that is already shown on it.
    private async void TextColorSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (SelectedTextColorOption == null)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetTextColor(SelectedTextColorOption.Value));
    }

    private async void HighlightColorSplitButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (SelectedHighlightColorOption == null)
        {
            return;
        }

        await ExecuteAsync(EditorCommand.SetHighlightColor(SelectedHighlightColorOption.Value));
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
            Header = Translator.Composer_TableRows,
            Minimum = 1,
            Maximum = 10,
            SmallChange = 1,
            Value = 2
        };
        var columnsBox = new NumberBox
        {
            Header = Translator.Composer_TableColumns,
            Minimum = 1,
            Maximum = 10,
            SmallChange = 1,
            Value = 2
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Translator.Composer_InsertTable,
            PrimaryButtonText = Translator.Buttons_Insert,
            CloseButtonText = Translator.Buttons_Cancel,
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



