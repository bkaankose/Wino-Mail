using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.Web.WebView2.Core;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Editor;

public sealed partial class WinoMailEditor : UserControl, IHtmlMailEditor
{
    public static readonly DependencyProperty EnabledFeaturesProperty = DependencyProperty.Register(
        nameof(EnabledFeatures), typeof(MailEditorFeatures), typeof(WinoMailEditor),
        new PropertyMetadata(MailEditorFeatures.All, OnConfigurationChanged));
    public static readonly DependencyProperty AvailableFontsProperty = DependencyProperty.Register(
        nameof(AvailableFonts), typeof(object), typeof(WinoMailEditor), new PropertyMetadata(null));
    public static readonly DependencyProperty IsEditorWebViewEditorProperty = DependencyProperty.Register(
        nameof(IsEditorWebViewEditor), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(true, OnBuiltInToolbarVisibilityChanged));
    public static readonly DependencyProperty ToolbarVisibilityProperty = DependencyProperty.Register(
        nameof(ToolbarVisibility), typeof(Visibility), typeof(WinoMailEditor),
        new PropertyMetadata(Visibility.Visible, OnConfigurationChanged));
    public static readonly DependencyProperty StatusBarVisibilityProperty = DependencyProperty.Register(
        nameof(StatusBarVisibility), typeof(Visibility), typeof(WinoMailEditor),
        new PropertyMetadata(Visibility.Collapsed, OnConfigurationChanged));
    public static readonly DependencyProperty UseBuiltInFilePickersProperty = DependencyProperty.Register(
        nameof(UseBuiltInFilePickers), typeof(bool), typeof(WinoMailEditor), new PropertyMetadata(true));
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(false, OnReadOnlyChanged));
    public static readonly DependencyProperty IsSmimeSigningEnabledProperty = DependencyProperty.Register(
        nameof(IsSmimeSigningEnabled), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(false, OnSecurityStateChanged));
    public static readonly DependencyProperty IsSmimeEncryptionEnabledProperty = DependencyProperty.Register(
        nameof(IsSmimeEncryptionEnabled), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(false, OnSecurityStateChanged));
    public static readonly DependencyProperty IsEditorDarkModeProperty = DependencyProperty.Register(
        nameof(IsEditorDarkMode), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(false, OnEditorThemeChanged));
    public static readonly DependencyProperty IsPasteOptionsEnabledProperty = DependencyProperty.Register(
        nameof(IsPasteOptionsEnabled), typeof(bool), typeof(WinoMailEditor),
        new PropertyMetadata(true, OnPasteModeChanged));

    private EditorBridge? _bridge;
    private Task? _initializationTask;
    private readonly TaskCompletionSource<bool> _loadedSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _updatingToolbar;
    private bool _disposed;

    public WinoMailEditor()
    {
        InitializeComponent();
        LoadSystemFonts();
        Capabilities = BuildCapabilities();
        ApplyConfiguration();
        SetTableOperationsEnabled(false);
    }

    public event EventHandler? ContentChanged;
    public event EventHandler<EditorSelectionState>? SelectionStateChanged;
    public event EventHandler<MailEditorCommandRequestedEventArgs>? CommandRequested;
    public event EventHandler<MailEditorFilesSelectedEventArgs>? AttachmentsSelected;
    public event EventHandler<MailEditorFilesSelectedEventArgs>? InlineImagesSelected;
    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorShortcutKind>? ShortcutRequested;

    public IReadOnlyList<EditorFontFamilyOption> AvailableFonts
    {
        get => GetValue(AvailableFontsProperty) as IReadOnlyList<EditorFontFamilyOption>
            ?? Array.Empty<EditorFontFamilyOption>();
        set => SetValue(AvailableFontsProperty, value ?? Array.Empty<EditorFontFamilyOption>());
    }

    public MailEditorFeatures EnabledFeatures { get => (MailEditorFeatures)GetValue(EnabledFeaturesProperty); set => SetValue(EnabledFeaturesProperty, value); }
    public Visibility ToolbarVisibility { get => (Visibility)GetValue(ToolbarVisibilityProperty); set => SetValue(ToolbarVisibilityProperty, value); }
    public Visibility StatusBarVisibility { get => (Visibility)GetValue(StatusBarVisibilityProperty); set => SetValue(StatusBarVisibilityProperty, value); }
    public bool UseBuiltInFilePickers { get => (bool)GetValue(UseBuiltInFilePickersProperty); set => SetValue(UseBuiltInFilePickersProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool IsSmimeSigningEnabled { get => (bool)GetValue(IsSmimeSigningEnabledProperty); set => SetValue(IsSmimeSigningEnabledProperty, value); }
    public bool IsSmimeEncryptionEnabled { get => (bool)GetValue(IsSmimeEncryptionEnabledProperty); set => SetValue(IsSmimeEncryptionEnabledProperty, value); }
    public bool IsEditorDarkMode { get => (bool)GetValue(IsEditorDarkModeProperty); set => SetValue(IsEditorDarkModeProperty, value); }
    public bool IsPasteOptionsEnabled { get => (bool)GetValue(IsPasteOptionsEnabledProperty); set => SetValue(IsPasteOptionsEnabledProperty, value); }
    public bool IsEditorWebViewEditor { get => (bool)GetValue(IsEditorWebViewEditorProperty); set => SetValue(IsEditorWebViewEditorProperty, value); }
    public CoreWebView2Environment? WebViewEnvironment { get; set; }
    public EditorState CurrentState { get; private set; } = new();
    public EditorCapabilities Capabilities { get; private set; }

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _loadedSource.Task;
        _initializationTask ??= InitializeCoreAsync();
        await _initializationTask;
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async Task SetHtmlAsync(string html)
    {
        await InitializeAsync();
        await _bridge!.SetContentAsync(html, "replace");
    }

    public Task RenderHtmlAsync(string html) => SetHtmlAsync(html);

    public async Task<string?> GetHtmlBodyAsync()
    {
        await InitializeAsync();
        return await _bridge!.GetBodyContentAsync();
    }

    public async Task SetDefaultTypographyAsync(string? fontFamily, int fontSize)
    {
        await InitializeAsync();
        await _bridge!.SetTypographyAsync(fontFamily, fontSize);
    }

    public async Task SetReplyHtmlAsync(string previousMessageHtml)
    {
        await InitializeAsync();
        await _bridge!.SetContentAsync(previousMessageHtml, "reply");
    }

    public async Task<string> GetHtmlAsync()
    {
        await InitializeAsync();
        return await _bridge!.GetContentAsync();
    }

    public async Task InsertImageAsync(string dataUri)
    {
        await InitializeAsync();
        await _bridge!.InsertImageAsync(dataUri);
    }

    public Task FocusEditorAsync() => FocusEditorAsync(true);

    public async Task FocusEditorAsync(bool focusControlAsWell)
    {
        await InitializeAsync();

        if (focusControlAsWell)
        {
            EditorWebView2.Focus(FocusState.Programmatic);
        }

        await _bridge!.FocusAsync();
    }

    public Microsoft.UI.Xaml.Controls.WebView2 GetUnderlyingWebView() =>
        !_disposed && EditorWebView2 is not null
            ? EditorWebView2
            : throw new ObjectDisposedException(nameof(WinoMailEditor));

    public void SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel level)
    {
        if (!_disposed && EditorWebView2?.CoreWebView2 is not null)
            EditorWebView2.CoreWebView2.MemoryUsageTargetLevel = level;
    }

    public async Task InsertImagesAsync(IEnumerable<EditorImageInfo> images)
    {
        foreach (var image in images)
        {
            await InsertImageAsync(image.Data);
        }
    }

    public Task EditorIndentAsync() => ExecuteCommandAsync(EditorCommand.Indent());

    public Task EditorOutdentAsync() => ExecuteCommandAsync(EditorCommand.Outdent());

    public Task ShowImagePickerAsync() => PickInlineImagesAsync();

    public async void ShowImagePicker() => await ShowImagePickerAsync();

    public void ToggleEditorTheme() => IsEditorDarkMode = !IsEditorDarkMode;

    private async void WinoMailEditor_Loaded(object sender, RoutedEventArgs e)
    {
        _loadedSource.TrySetResult(true);
        try
        {
            await InitializeAsync();
            SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Normal);
        }
        catch (Exception exception) { SetStatus($"Editor failed to start: {exception.Message}", true); }
    }

    private void WinoMailEditor_Unloaded(object sender, RoutedEventArgs e) =>
        SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel.Low);

    private async Task InitializeCoreAsync()
    {
        var environment = WebViewEnvironment ?? await WinoWebViewEnvironment.GetSharedEnvironmentAsync();
        await EditorWebView2.EnsureCoreWebView2Async(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _bridge = new EditorBridge(EditorWebView2);
        _bridge.SelectionStateChanged += Bridge_SelectionStateChanged;
        _bridge.ContentChanged += Bridge_ContentChanged;
        _bridge.LinkNavigationRequested += Bridge_LinkNavigationRequested;
        _bridge.ShortcutRequested += Bridge_ShortcutRequested;

        try
        {
            await _bridge.InitializeAsync();
        }
        catch
        {
            DisposeBridge();
            throw;
        }

        await _bridge!.SetReadOnlyAsync(IsReadOnly);
        await _bridge.SetPasteAsHtmlAsync(IsPasteOptionsEnabled);
        await _bridge.SetThemeAsync(IsEditorDarkMode);
        ApplyConfiguration();
        SetStatus("Ready (WebView2)", false);
    }

    private void DisposeBridge()
    {
        if (_bridge is null) return;
        _bridge.SelectionStateChanged -= Bridge_SelectionStateChanged;
        _bridge.ContentChanged -= Bridge_ContentChanged;
        _bridge.LinkNavigationRequested -= Bridge_LinkNavigationRequested;
        _bridge.ShortcutRequested -= Bridge_ShortcutRequested;
        _bridge.Dispose();
        _bridge = null;
    }

    private void Bridge_ContentChanged(object? sender, EventArgs e) => ContentChanged?.Invoke(this, EventArgs.Empty);

    private static async void Bridge_LinkNavigationRequested(object? sender, string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp ||
                 uri.Scheme == Uri.UriSchemeHttps ||
                 uri.Scheme == Uri.UriSchemeMailto ||
                 uri.Scheme == Uri.UriSchemeFtp))
            {
                await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        }
        catch { }
    }

    private void Bridge_ShortcutRequested(object? sender, string command)
    {
        if (string.Equals(command, "openLinkDialog", StringComparison.OrdinalIgnoreCase))
        {
            ShortcutRequested?.Invoke(this, EditorShortcutKind.OpenLinkDialog);
        }
    }

    private void Bridge_SelectionStateChanged(object? sender, EditorSelectionState state)
    {
        _updatingToolbar = true;
        try
        {
            BoldButton.IsChecked = state.Bold;
            ItalicButton.IsChecked = state.Italic;
            UnderlineButton.IsChecked = state.Underline;
            StrikeButton.IsChecked = state.Strikethrough;
            BulletsButton.IsChecked = state.UnorderedList;
            NumberingButton.IsChecked = state.OrderedList;
            ColorButton.Foreground = new SolidColorBrush(ParseColor(state.Color));
            SetTableOperationsEnabled(state.InTable);
            if (!string.IsNullOrWhiteSpace(state.FontFamily))
            {
                EditorFontFamilyOption? selectedFont = AvailableFonts.FirstOrDefault(
                    option => string.Equals(option.DisplayName, state.FontFamily, StringComparison.CurrentCultureIgnoreCase));
                if (selectedFont is not null) FontFamilyComboBox.SelectedItem = selectedFont;
            }
        }
        finally { _updatingToolbar = false; }
        SelectionStateChanged?.Invoke(this, state);
        CurrentState = new EditorState
        {
            IsBold = state.Bold,
            IsItalic = state.Italic,
            IsUnderline = state.Underline,
            IsStrikethrough = state.Strikethrough,
            IsOrderedList = state.OrderedList,
            IsUnorderedList = state.UnorderedList,
            HasSelection = state.HasSelection,
            IsImageSelected = state.ImageSelected,
            IsDarkMode = state.DarkMode,
            IsSpellCheckEnabled = state.SpellCheck,
            Alignment = Enum.TryParse<EditorTextAlignment>(state.Alignment, true, out var alignment) ? alignment : EditorTextAlignment.Left,
            FontFamily = state.FontFamily,
            FontSize = state.FontSize,
            ParagraphStyle = state.ParagraphStyle,
            TextColor = state.Color,
            HighlightColor = state.HighlightColor,
            LineHeight = state.LineHeight,
            LinkUrl = state.LinkUrl,
            ImageAltText = state.ImageAltText,
            ImageLinkUrl = state.ImageLinkUrl,
            SelectedText = state.SelectedText
        };
        StateChanged?.Invoke(this, CurrentState);
    }

    public async Task ExecuteCommandAsync(EditorCommand command)
    {
        await FocusEditorAsync(true);
        switch (command.Kind)
        {
            case EditorCommandKind.ToggleBold: await _bridge!.ExecuteCommandAsync("bold"); break;
            case EditorCommandKind.ToggleItalic: await _bridge!.ExecuteCommandAsync("italic"); break;
            case EditorCommandKind.ToggleUnderline: await _bridge!.ExecuteCommandAsync("underline"); break;
            case EditorCommandKind.ToggleStrikethrough: await _bridge!.ExecuteCommandAsync("strikeThrough"); break;
            case EditorCommandKind.ToggleOrderedList: await _bridge!.ExecuteCommandAsync("insertOrderedList"); break;
            case EditorCommandKind.ToggleUnorderedList: await _bridge!.ExecuteCommandAsync("insertUnorderedList"); break;
            case EditorCommandKind.Indent: await _bridge!.ExecuteCommandAsync("indent"); break;
            case EditorCommandKind.Outdent: await _bridge!.ExecuteCommandAsync("outdent"); break;
            case EditorCommandKind.SetAlignment: await _bridge!.ExecuteCommandAsync(command.Value is EditorTextAlignment value ? value switch { EditorTextAlignment.Center => "justifyCenter", EditorTextAlignment.Right => "justifyRight", EditorTextAlignment.Justify => "justifyFull", _ => "justifyLeft" } : "justifyLeft"); break;
            case EditorCommandKind.SetFontFamily: await _bridge!.ExecuteCommandAsync("fontName", command.Value?.ToString()); break;
            case EditorCommandKind.SetFontSize: await _bridge!.ExecuteCommandAsync("fontSize", command.Value?.ToString()); break;
            case EditorCommandKind.SetParagraphStyle: await _bridge!.SetParagraphStyleAsync(command.Value?.ToString() ?? "p"); break;
            case EditorCommandKind.SetTextColor: await _bridge!.ExecuteCommandAsync("foreColor", command.Value?.ToString()); break;
            case EditorCommandKind.SetHighlightColor: await _bridge!.ExecuteCommandAsync("backColor", command.Value?.ToString()); break;
            case EditorCommandKind.SetLineHeight: await _bridge!.SetLineHeightAsync(command.Value?.ToString() ?? "normal"); break;
            case EditorCommandKind.ClearFormatting: await _bridge!.ExecuteCommandAsync("removeFormat"); break;
            case EditorCommandKind.InsertImage: await PickInlineImagesAsync(); break;
            case EditorCommandKind.SetImageProperties when command.Value is EditorImagePropertiesCommandArgs image:
                await _bridge!.SetSelectedImagePropertiesAsync(image);
                break;
            case EditorCommandKind.InsertLink when command.Value is EditorLinkCommandArgs link:
                if (CurrentState.IsImageSelected)
                {
                    await _bridge!.SetSelectedImagePropertiesAsync(new EditorImagePropertiesCommandArgs(
                        CurrentState.ImageAltText ?? string.Empty,
                        link.Url,
                        link.OpenInNewWindow));
                }
                else
                {
                    await _bridge!.CreateLinkAsync(link.Url, link.Text, link.OpenInNewWindow);
                }
                break;
            case EditorCommandKind.RemoveLink:
                if (CurrentState.IsImageSelected)
                {
                    await _bridge!.SetSelectedImagePropertiesAsync(new EditorImagePropertiesCommandArgs(
                        CurrentState.ImageAltText ?? string.Empty));
                }
                else
                {
                    await _bridge!.RemoveLinkAsync();
                }
                break;
            case EditorCommandKind.InsertEmoji:
                await _bridge!.InsertEmojiAsync(command.Value?.ToString() ?? "😊");
                break;
            case EditorCommandKind.InsertTable when command.Value is EditorTableCommandArgs table: await _bridge!.InsertTableAsync(table.Rows, table.Columns); break;
            case EditorCommandKind.ToggleTheme: IsEditorDarkMode = command.Value is true; break;
            case EditorCommandKind.ToggleBuiltInToolbar: IsEditorWebViewEditor = command.Value is true; break;
            case EditorCommandKind.ToggleSpellCheck: await _bridge!.SetSpellCheckAsync(command.Value is true); break;
        }
    }

    private void LoadSystemFonts()
    {
        try
        {
            AvailableFonts = CanvasTextFormat.GetSystemFontFamilies()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Select(name => new EditorFontFamilyOption(name))
                .ToArray();
        }
        catch
        {
            AvailableFonts = new[] { "Segoe UI", "Arial", "Calibri", "Times New Roman", "Courier New" }
                .Select(name => new EditorFontFamilyOption(name))
                .ToArray();
        }
    }

    private async void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolbar || FontFamilyComboBox.SelectedItem is not EditorFontFamilyOption font) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync("fontName", font.DisplayName));
    }

    private async void FontSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingToolbar || FontSizeComboBox.SelectedItem is not ComboBoxItem { Content: string size }) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync("fontSize", size));
    }

    private async void FormattingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingToolbar || sender is not FrameworkElement { Tag: string command }) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync(command));
    }

    private async void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command }) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync(command));
    }

    private async void CommandMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command }) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync(command));
    }

    private async void ColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string color }) return;
        await ExecuteAsync(() => _bridge!.ExecuteCommandAsync("foreColor", color));
    }

    private async void ApplyLinkButton_Click(object sender, RoutedEventArgs e)
    {
        string url = LinkUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        LinkButton.Flyout.Hide();
        await ExecuteAsync(() => _bridge!.CreateLinkAsync(url));
    }

    private async void InsertTableConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        InsertTableButton.Flyout.Hide();
        await ExecuteAsync(() => _bridge!.InsertTableAsync(ParseDimension(TableRowsTextBox.Text), ParseDimension(TableColumnsTextBox.Text)));
    }

    private async void TableCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command }) return;
        await ExecuteAsync(() => _bridge!.ExecuteTableCommandAsync(command));
    }

    private async void AttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        var request = new MailEditorCommandRequestedEventArgs(MailEditorCommandKind.AddAttachments);
        CommandRequested?.Invoke(this, request);
        if (request.Handled || !UseBuiltInFilePickers) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) AttachmentsSelected?.Invoke(this, new MailEditorFilesSelectedEventArgs(files));
    }

    private async void InlineImageButton_Click(object sender, RoutedEventArgs e)
        => await PickInlineImagesAsync();

    private async Task PickInlineImagesAsync()
    {
        var request = new MailEditorCommandRequestedEventArgs(MailEditorCommandKind.InsertInlineImages);
        CommandRequested?.Invoke(this, request);
        if (request.Handled || !UseBuiltInFilePickers) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        InitializePicker(picker);
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        InlineImagesSelected?.Invoke(this, new MailEditorFilesSelectedEventArgs(files));
        foreach (StorageFile file in files) await InsertStorageImageAsync(file);
    }

    private EditorCapabilities BuildCapabilities() => new()
    {
        Fonts = AvailableFonts,
        FontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 48, 72],
        TextColors =
        [
            new("Default", string.Empty), new("Black", "#000000"), new("Gray", "#666666"),
            new("Red", "#c62828"), new("Orange", "#ef6c00"), new("Yellow", "#f9a825"),
            new("Green", "#2e7d32"), new("Blue", "#1565c0"), new("Purple", "#6a1b9a")
        ],
        HighlightColors =
        [
            new("None", string.Empty), new("Yellow", "#fff59d"), new("Green", "#c8e6c9"),
            new("Blue", "#bbdefb"), new("Pink", "#f8bbd0"), new("Orange", "#ffe0b2")
        ],
        ParagraphStyles =
        [
            new("Normal", "p"), new("Heading 1", "h1"), new("Heading 2", "h2"),
            new("Heading 3", "h3"), new("Quote", "blockquote"), new("Preformatted", "pre"),
            new("Code", "code")
        ],
        LineHeights = ["normal", "1", "1.15", "1.5", "2"],
        Alignments = Enum.GetValues<EditorTextAlignment>()
    };

    private void SmimeSignButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingToolbar) return;
        IsSmimeSigningEnabled = SmimeSignButton.IsChecked == true;
        CommandRequested?.Invoke(this, new MailEditorCommandRequestedEventArgs(MailEditorCommandKind.SmimeSigningChanged, IsSmimeSigningEnabled));
    }

    private void SmimeEncryptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingToolbar) return;
        IsSmimeEncryptionEnabled = SmimeEncryptButton.IsChecked == true;
        CommandRequested?.Invoke(this, new MailEditorCommandRequestedEventArgs(MailEditorCommandKind.SmimeEncryptionChanged, IsSmimeEncryptionEnabled));
    }

    private void CategoryBar_SelectionChanged(object? sender, EditorToolbarCategory category) => ShowCategory(category);

    private void ShowCategory(EditorToolbarCategory category)
    {
        FormattingBar.Visibility = category == EditorToolbarCategory.Formatting ? Visibility.Visible : Visibility.Collapsed;
        InsertBar.Visibility = category == EditorToolbarCategory.Insert ? Visibility.Visible : Visibility.Collapsed;
        TableBar.Visibility = category == EditorToolbarCategory.Table ? Visibility.Visible : Visibility.Collapsed;
        SecurityBar.Visibility = category == EditorToolbarCategory.Security ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyConfiguration()
    {
        if (CategoryBar is null) return;
        ToolbarHost.Visibility = ToolbarVisibility;
        CategoryBar.Visibility = ToolbarVisibility;
        StatusBar.Visibility = StatusBarVisibility;
        bool format = HasAny(MailEditorFeatures.FontFamily | MailEditorFeatures.FontSize | MailEditorFeatures.TextStyles | MailEditorFeatures.TextColor | MailEditorFeatures.Paragraph | MailEditorFeatures.UndoRedo);
        bool insert = HasAny(MailEditorFeatures.Attachments | MailEditorFeatures.InlineImages | MailEditorFeatures.Hyperlinks);
        bool table = HasAny(MailEditorFeatures.Tables);
        bool security = HasAny(MailEditorFeatures.SmimeSigning | MailEditorFeatures.SmimeEncryption);
        CategoryBar.FormattingVisibility = ToVisibility(format);
        CategoryBar.InsertVisibility = ToVisibility(insert);
        CategoryBar.TableVisibility = ToVisibility(table);
        CategoryBar.SecurityVisibility = ToVisibility(security);
        FontFamilyComboBox.Visibility = ToVisibility(HasAny(MailEditorFeatures.FontFamily));
        FontSizeComboBox.Visibility = ToVisibility(HasAny(MailEditorFeatures.FontSize));
        foreach (Control control in new Control[] { BoldButton, ItalicButton, UnderlineButton, StrikeButton }) control.Visibility = ToVisibility(HasAny(MailEditorFeatures.TextStyles));
        ColorButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.TextColor));
        foreach (Control control in new Control[] { BulletsButton, NumberingButton, OutdentButton, IndentButton, AlignmentButton }) control.Visibility = ToVisibility(HasAny(MailEditorFeatures.Paragraph));
        UndoButton.Visibility = RedoButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.UndoRedo));
        AttachmentButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.Attachments));
        InlineImageButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.InlineImages));
        LinkButton.Visibility = UnlinkButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.Hyperlinks));
        SmimeSignButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.SmimeSigning));
        SmimeEncryptButton.Visibility = ToVisibility(HasAny(MailEditorFeatures.SmimeEncryption));
        FormattingBar.IsEnabled = InsertBar.IsEnabled = TableBar.IsEnabled = SecurityBar.IsEnabled = !IsReadOnly && _bridge is not null;
        SelectFirstVisibleCategory(format, insert, table, security);
        ApplySecurityState();
    }

    private void SelectFirstVisibleCategory(bool format, bool insert, bool table, bool security)
    {
        EditorToolbarCategory selected = CategoryBar.SelectedCategory;
        bool selectedVisible = selected switch
        {
            EditorToolbarCategory.Formatting => format,
            EditorToolbarCategory.Insert => insert,
            EditorToolbarCategory.Table => table,
            EditorToolbarCategory.Security => security,
            _ => false
        };
        if (!selectedVisible)
            CategoryBar.SelectedCategory = format ? EditorToolbarCategory.Formatting : insert ? EditorToolbarCategory.Insert : table ? EditorToolbarCategory.Table : EditorToolbarCategory.Security;
        ShowCategory(CategoryBar.SelectedCategory);
    }

    private async void EditorWebView_Drop(object sender, DragEventArgs e)
    {
        if (IsReadOnly || !HasAny(MailEditorFeatures.InlineImages) || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (StorageFile file in items.OfType<StorageFile>().Where(file => file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            await InsertStorageImageAsync(file);
    }

    private void EditorWebView_DragOver(object sender, DragEventArgs e)
    {
        if (!IsReadOnly && HasAny(MailEditorFeatures.InlineImages) && e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async Task InsertStorageImageAsync(StorageFile file)
    {
        IBuffer buffer = await FileIO.ReadBufferAsync(file);
        var bytes = new byte[checked((int)buffer.Length)];
        using (DataReader reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        await InsertImageAsync($"data:{file.ContentType};base64,{Convert.ToBase64String(bytes)}");
    }

    private void InitializePicker(object picker)
    {
        if (XamlRoot is null)
            throw new InvalidOperationException("The editor must be connected to a WinUI window before opening a picker.");

        nint windowHandle = Win32Interop.GetWindowFromWindowId(
            XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try { await FocusEditorAsync(true); await action(); }
        catch (Exception exception) { SetStatus(exception.Message, true); }
    }

    private void SetTableOperationsEnabled(bool enabled)
    {
        AddRowButton.IsEnabled = RemoveRowButton.IsEnabled = AddColumnButton.IsEnabled = RemoveColumnButton.IsEnabled = DeleteTableButton.IsEnabled = enabled;
    }

    private void SetStatus(string message, bool error)
    {
        StatusBar.Message = message;
        StatusBar.Severity = error ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
    }

    private bool HasAny(MailEditorFeatures features) => (EnabledFeatures & features) != 0;
    private static Visibility ToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private static int ParseDimension(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? Math.Clamp(result, 1, 20) : 3;
    private static Color ParseColor(string value) => value.Length == 7 && byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, null, out byte r) && byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, null, out byte g) && byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, null, out byte b) ? Color.FromArgb(255, r, g, b) : Color.FromArgb(255, 0, 0, 0);

    private static void OnConfigurationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((WinoMailEditor)sender).ApplyConfiguration();
    private static void OnBuiltInToolbarVisibilityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WinoMailEditor)sender).ToolbarVisibility = (bool)args.NewValue
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static async void OnReadOnlyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (WinoMailEditor)sender;
        editor.ApplyConfiguration();
        if (editor._bridge is not null) await editor._bridge.SetReadOnlyAsync((bool)args.NewValue);
    }
    private static void OnSecurityStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((WinoMailEditor)sender).ApplySecurityState();
    private static async void OnEditorThemeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (WinoMailEditor)sender;
        if (editor._bridge is not null) await editor._bridge.SetThemeAsync((bool)args.NewValue);
    }
    private static async void OnPasteModeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (WinoMailEditor)sender;
        if (editor._bridge is not null) await editor._bridge.SetPasteAsHtmlAsync((bool)args.NewValue);
    }
    private void ApplySecurityState()
    {
        if (SmimeSignButton is null) return;
        _updatingToolbar = true;
        SmimeSignButton.IsChecked = IsSmimeSigningEnabled;
        SmimeEncryptButton.IsChecked = IsSmimeEncryptionEnabled;
        _updatingToolbar = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadedSource.TrySetException(new ObjectDisposedException(nameof(WinoMailEditor)));
        DisposeBridge();
        GC.SuppressFinalize(this);
    }
}
