using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI.ViewManagement.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;

namespace Wino.Mail.Controls;

public sealed partial class WebViewEditorControl : Control, IDisposable, IEditorCommandTarget
{
    private const string PART_WebView = "WebView";

    private static readonly IReadOnlyList<int> DefaultFontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36];
    private static readonly IReadOnlyList<EditorColorOption> DefaultTextColors =
    [
        new("Default", string.Empty),
        new("Black", "#000000"),
        new("Gray", "#666666"),
        new("Red", "#c62828"),
        new("Orange", "#ef6c00"),
        new("Yellow", "#f9a825"),
        new("Green", "#2e7d32"),
        new("Blue", "#1565c0"),
        new("Purple", "#6a1b9a")
    ];
    private static readonly IReadOnlyList<EditorColorOption> DefaultHighlightColors =
    [
        new("None", string.Empty),
        new("Yellow", "#fff59d"),
        new("Green", "#c8e6c9"),
        new("Blue", "#bbdefb"),
        new("Pink", "#f8bbd0"),
        new("Orange", "#ffe0b2")
    ];
    private static readonly IReadOnlyList<EditorParagraphStyleOption> DefaultParagraphStyles =
    [
        new("Paragraph", "div"),
        new("Heading 1", "h1"),
        new("Heading 2", "h2"),
        new("Heading 3", "h3"),
        new("Quote", "blockquote"),
        new("Code", "pre")
    ];
    private static readonly IReadOnlyList<string> DefaultLineHeights = ["normal", "1", "1.15", "1.5", "2"];
    private static readonly IReadOnlyList<EditorTextAlignment> DefaultAlignments =
        [EditorTextAlignment.Left, EditorTextAlignment.Center, EditorTextAlignment.Right, EditorTextAlignment.Justify];

    private readonly INativeAppService _nativeAppService = App.Current.Services.GetService<INativeAppService>()!;
    private readonly IFontService _fontService = App.Current.Services.GetService<IFontService>()!;
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;

    [GeneratedDependencyProperty]
    public partial bool IsEditorDarkMode { get; set; }
    async partial void OnIsEditorDarkModeChanged(bool newValue)
    {
        UpdateState(GetCurrentStateOrDefault() with { IsDarkMode = newValue });
        await UpdateEditorThemeAsync();
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorWebViewEditor { get; set; }
    async partial void OnIsEditorWebViewEditorChanged(bool newValue)
    {
        UpdateState(GetCurrentStateOrDefault() with { IsBuiltInToolbarVisible = newValue });
        await ApplyBuiltInToolbarVisibilityAsync();
    }

    private WebView2? _chromium;
    private bool _disposedValue;
    private bool? _lastAppliedDarkTheme;
    private readonly TaskCompletionSource<bool> _domLoadedTask = new();
    private readonly TaskCompletionSource<bool> _editorReadyTask = new();
    private readonly object _editorInitializationLock = new();
    private Task? _editorInitializationTask;

    public event EventHandler<EditorState>? StateChanged;
    public event EventHandler<EditorCapabilities>? CapabilitiesChanged;

    public EditorState CurrentState { get; private set; }
    public EditorCapabilities Capabilities { get; private set; }

    public WebViewEditorControl()
    {
        DefaultStyleKey = typeof(WebViewEditorControl);

        IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
        Capabilities = BuildCapabilities(_fontService.GetFonts());
        CurrentState = CreateDefaultState();
    }

    protected override async void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        var newWebView = GetTemplateChild(PART_WebView) as WebView2;
        if (newWebView == null)
        {
            return;
        }

        if (_chromium != null && !ReferenceEquals(_chromium, newWebView))
        {
            DetachChromium(_chromium, closeWebView: false);
        }

        _chromium = newWebView;
        await InitializeWebViewAsync();
    }

    public async Task ExecuteCommandAsync(EditorCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposedValue, this);
        await EnsureEditorReadyAsync();

        if (_chromium == null)
        {
            return;
        }

        switch (command.Kind)
        {
            case EditorCommandKind.ToggleBold:
                await ExecuteEditorCommandAsync("bold");
                break;
            case EditorCommandKind.ToggleItalic:
                await ExecuteEditorCommandAsync("italic");
                break;
            case EditorCommandKind.ToggleUnderline:
                await ExecuteEditorCommandAsync("underline");
                break;
            case EditorCommandKind.ToggleStrikethrough:
                await ExecuteEditorCommandAsync("strikethrough");
                break;
            case EditorCommandKind.ToggleOrderedList:
                await ExecuteEditorCommandAsync("insertorderedlist");
                break;
            case EditorCommandKind.ToggleUnorderedList:
                await ExecuteEditorCommandAsync("insertunorderedlist");
                break;
            case EditorCommandKind.Indent:
                await ExecuteEditorCommandAsync("indent");
                break;
            case EditorCommandKind.Outdent:
                await ExecuteEditorCommandAsync("outdent");
                break;
            case EditorCommandKind.SetAlignment when command.Value is EditorTextAlignment alignment:
                await ExecuteEditorCommandAsync(alignment switch
                {
                    EditorTextAlignment.Left => "justifyleft",
                    EditorTextAlignment.Center => "justifycenter",
                    EditorTextAlignment.Right => "justifyright",
                    EditorTextAlignment.Justify => "justifyfull",
                    _ => "justifyleft"
                });
                break;
            case EditorCommandKind.SetFontFamily when command.Value is string fontFamily:
                await _chromium.ExecuteScriptFunctionSafeAsync("setFontFamily", JsonSerializer.Serialize(fontFamily, BasicTypesJsonContext.Default.String));
                break;
            case EditorCommandKind.SetFontSize when command.Value is int fontSize:
                await _chromium.ExecuteScriptFunctionSafeAsync("setFontSize", JsonSerializer.Serialize(fontSize, BasicTypesJsonContext.Default.Int32));
                break;
            case EditorCommandKind.SetParagraphStyle when command.Value is string paragraphStyle:
                await _chromium.ExecuteScriptFunctionSafeAsync("setParagraphStyle", JsonSerializer.Serialize(paragraphStyle, BasicTypesJsonContext.Default.String));
                break;
            case EditorCommandKind.SetTextColor when command.Value is string textColor:
                await _chromium.ExecuteScriptFunctionSafeAsync("setTextColor", JsonSerializer.Serialize(textColor, BasicTypesJsonContext.Default.String));
                break;
            case EditorCommandKind.SetHighlightColor when command.Value is string highlightColor:
                await _chromium.ExecuteScriptFunctionSafeAsync("setHighlightColor", JsonSerializer.Serialize(highlightColor, BasicTypesJsonContext.Default.String));
                break;
            case EditorCommandKind.SetLineHeight when command.Value is string lineHeight:
                await _chromium.ExecuteScriptFunctionSafeAsync("setLineHeight", JsonSerializer.Serialize(lineHeight, BasicTypesJsonContext.Default.String));
                break;
            case EditorCommandKind.InsertImage:
                await ShowImagePickerAsync();
                return;
            case EditorCommandKind.InsertLink when command.Value is EditorLinkCommandArgs linkArgs:
                await _chromium.ExecuteScriptFunctionSafeAsync("upsertLink", SerializeLinkArgs(linkArgs));
                break;
            case EditorCommandKind.RemoveLink:
                await _chromium.ExecuteScriptFunctionSafeAsync("removeLink");
                break;
            case EditorCommandKind.InsertEmoji:
                await ShowEmojiPickerAsync();
                return;
            case EditorCommandKind.InsertTable when command.Value is EditorTableCommandArgs tableArgs:
                await _chromium.ExecuteScriptFunctionSafeAsync("insertTableHtml", SerializeTableArgs(tableArgs));
                break;
            case EditorCommandKind.ToggleBuiltInToolbar when command.Value is bool isToolbarVisible:
                IsEditorWebViewEditor = isToolbarVisible;
                return;
            case EditorCommandKind.ToggleTheme when command.Value is bool isDarkMode:
                IsEditorDarkMode = isDarkMode;
                return;
            case EditorCommandKind.ToggleSpellCheck when command.Value is bool isSpellCheckEnabled:
                await _chromium.ExecuteScriptFunctionSafeAsync("setSpellCheck", JsonSerializer.Serialize(isSpellCheckEnabled, BasicTypesJsonContext.Default.Boolean));
                break;
            default:
                throw new InvalidOperationException($"Unsupported editor command: {command.Kind}");
        }

        await RefreshStateAsync();
    }

    public async Task EditorIndentAsync() => await ExecuteCommandAsync(EditorCommand.Indent());

    public async Task EditorOutdentAsync() => await ExecuteCommandAsync(EditorCommand.Outdent());

    public void ToggleEditorTheme()
    {
        IsEditorDarkMode = !IsEditorDarkMode;
    }

    public async Task<string?> GetHtmlBodyAsync()
    {
        await EnsureEditorReadyAsync();

        if (_chromium == null)
        {
            return null;
        }

        var editorContent = await _chromium.ExecuteScriptFunctionSafeAsync("GetHTMLContent");
        return JsonSerializer.Deserialize(editorContent, BasicTypesJsonContext.Default.String);
    }

    public async Task ShowImagePickerAsync()
    {
        await EnsureEditorReadyAsync();
        if (_chromium != null)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("imageInput.click");
        }
    }

    public async void ShowImagePicker()
    {
        await ShowImagePickerAsync();
    }

    public async Task InsertImagesAsync(List<ImageInfo> images)
    {
        await EnsureEditorReadyAsync();
        if (_chromium != null)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("insertImages", JsonSerializer.Serialize(images, DomainModelsJsonContext.Default.ListImageInfo));
            await RefreshStateAsync();
        }
    }

    public async Task ShowEmojiPickerAsync()
    {
        CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);
        await FocusEditorAsync(focusControlAsWell: true);
    }

    public async void ShowEmojiPicker()
    {
        await ShowEmojiPickerAsync();
    }

    public WebView2 GetUnderlyingWebView() => _chromium!;

    public async Task RenderHtmlAsync(string htmlBody)
    {
        await EnsureEditorReadyAsync();

        if (_chromium == null)
        {
            return;
        }

        await _chromium.ExecuteScriptFunctionAsync("RenderHTML", JsonSerializer.Serialize(string.IsNullOrEmpty(htmlBody) ? " " : htmlBody, BasicTypesJsonContext.Default.String));
        await RefreshStateAsync();
    }

    public async Task FocusEditorAsync(bool focusControlAsWell)
    {
        await EnsureEditorReadyAsync();

        if (_chromium == null)
        {
            return;
        }

        if (focusControlAsWell)
        {
            Focus(FocusState.Programmatic);
            _chromium.Focus(FocusState.Programmatic);
            _chromium.Focus(FocusState.Keyboard);
        }

        await _chromium.ExecuteScriptSafeAsync("focusEditor();");

        if (focusControlAsWell)
        {
            _chromium.Focus(FocusState.Keyboard);
        }
    }

    public async Task UpdateEditorThemeAsync(bool force = false)
    {
        if (_chromium?.CoreWebView2 == null)
        {
            return;
        }

        if (!_editorReadyTask.Task.IsCompleted)
        {
            return;
        }

        var isDark = IsEditorDarkMode;
        if (!force && _lastAppliedDarkTheme == isDark)
        {
            return;
        }

        _lastAppliedDarkTheme = isDark;
        _chromium.CoreWebView2.Profile.PreferredColorScheme = isDark
            ? CoreWebView2PreferredColorScheme.Dark
            : CoreWebView2PreferredColorScheme.Light;

        await _chromium.ExecuteScriptFunctionSafeAsync(isDark ? "SetDarkEditor" : "SetLightEditor");
        UpdateState(CurrentState with { IsDarkMode = isDark });
    }

    private async Task InitializeWebViewAsync()
    {
        if (_chromium == null)
        {
            return;
        }

        var sharedEnvironment = await WebViewExtensions.GetSharedEnvironmentAsync();
        _chromium.CoreWebView2Initialized -= ChromiumInitialized;
        _chromium.CoreWebView2Initialized += ChromiumInitialized;
        await _chromium.EnsureCoreWebView2Async(sharedEnvironment);
    }

    private Task EnsureEditorReadyAsync()
    {
        if (_chromium == null || _disposedValue)
        {
            return Task.CompletedTask;
        }

        return EnsureEditorReadyCoreAsync();
    }

    private async Task EnsureEditorReadyCoreAsync()
    {
        await EnsureEditorInitializedAsync();
        await _editorReadyTask.Task;
    }

    private Task EnsureEditorInitializedAsync()
    {
        lock (_editorInitializationLock)
        {
            _editorInitializationTask ??= EnsureEditorInitializedCoreAsync();
            return _editorInitializationTask;
        }
    }

    private async Task EnsureEditorInitializedCoreAsync()
    {
        if (_chromium == null || _disposedValue)
        {
            _editorReadyTask.TrySetResult(true);
            return;
        }

        await _domLoadedTask.Task;

        if (_chromium == null || _disposedValue)
        {
            _editorReadyTask.TrySetResult(true);
            return;
        }

        var fonts = _fontService.GetFonts();
        await _chromium.ExecuteScriptFunctionAsync(
            "initializeJodit",
            JsonSerializer.Serialize(fonts, BasicTypesJsonContext.Default.ListString),
            JsonSerializer.Serialize(_preferencesService.ComposerFont, BasicTypesJsonContext.Default.String),
            JsonSerializer.Serialize(_preferencesService.ComposerFontSize, BasicTypesJsonContext.Default.Int32),
            JsonSerializer.Serialize(_preferencesService.ReaderFont, BasicTypesJsonContext.Default.String),
            JsonSerializer.Serialize(_preferencesService.ReaderFontSize, BasicTypesJsonContext.Default.Int32),
            JsonSerializer.Serialize(DefaultTextColors.Select(option => option.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(), BasicTypesJsonContext.Default.ListString),
            JsonSerializer.Serialize(DefaultHighlightColors.Select(option => option.Value).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(), BasicTypesJsonContext.Default.ListString));

        UpdateCapabilities(BuildCapabilities(fonts));
        _editorReadyTask.TrySetResult(true);

        await UpdateEditorThemeAsync(force: true);
        await ApplyBuiltInToolbarVisibilityAsync(force: true);
        await RefreshStateAsync();
    }

    private async Task ExecuteEditorCommandAsync(string command)
    {
        if (_chromium != null)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("executeEditorCommand", JsonSerializer.Serialize(command, BasicTypesJsonContext.Default.String));
        }
    }

    private async Task ApplyBuiltInToolbarVisibilityAsync(bool force = false)
    {
        if (_chromium == null)
        {
            return;
        }

        if (!_editorReadyTask.Task.IsCompleted)
        {
            return;
        }

        await _chromium.ExecuteScriptFunctionSafeAsync("toggleToolbar", JsonSerializer.Serialize(IsEditorWebViewEditor, BasicTypesJsonContext.Default.Boolean));
        UpdateState(CurrentState with { IsBuiltInToolbarVisible = IsEditorWebViewEditor });
    }

    private async Task RefreshStateAsync()
    {
        if (_chromium == null || !_editorReadyTask.Task.IsCompleted)
        {
            return;
        }

        var stateResult = await _chromium.ExecuteScriptFunctionSafeAsync("getEditorState");
        if (string.IsNullOrWhiteSpace(stateResult))
        {
            return;
        }

        var snapshot = DeserializeStateSnapshot(stateResult);
        if (snapshot != null)
        {
            UpdateState(MapState(snapshot));
        }
    }

    private void ChromiumInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (args.Exception != null || _chromium?.CoreWebView2 == null)
        {
            return;
        }

        _ = ConfigureChromiumAsync();
    }

    private async Task ConfigureChromiumAsync()
    {
        if (_chromium?.CoreWebView2 == null)
        {
            return;
        }

        var editorBundlePath = (await _nativeAppService.GetEditorBundlePathAsync()).Replace("editor.html", string.Empty, StringComparison.OrdinalIgnoreCase);
        _chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.editor", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);
        _chromium.CoreWebView2.DOMContentLoaded -= DomLoaded;
        _chromium.CoreWebView2.DOMContentLoaded += DomLoaded;
        _chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
        _chromium.CoreWebView2.WebMessageReceived += ScriptMessageReceived;
        _chromium.Source = new Uri("https://app.editor/editor.html");
    }

    private void DomLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        _domLoadedTask.TrySetResult(true);
        _ = EnsureEditorInitializedAsync();
    }

    private void ScriptMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.WebMessageAsJson);
        if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
            !string.Equals(typeElement.GetString(), "state", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (document.RootElement.TryGetProperty("state", out var stateElement))
        {
            var snapshot = DeserializeStateSnapshot(stateElement);
            if (snapshot != null)
            {
                UpdateState(MapState(snapshot));
            }
        }
    }

    private EditorState MapState(EditorStateSnapshot snapshot)
    {
        return new EditorState
        {
            IsBold = snapshot.IsBold,
            IsItalic = snapshot.IsItalic,
            IsUnderline = snapshot.IsUnderline,
            IsStrikethrough = snapshot.IsStrikethrough,
            IsOrderedList = snapshot.IsOrderedList,
            IsUnorderedList = snapshot.IsUnorderedList,
            CanIndent = snapshot.CanIndent,
            CanOutdent = snapshot.CanOutdent,
            HasSelection = snapshot.HasSelection,
            IsDarkMode = IsEditorDarkMode,
            IsBuiltInToolbarVisible = IsEditorWebViewEditor,
            IsSpellCheckEnabled = snapshot.IsSpellCheckEnabled,
            Alignment = ParseAlignment(snapshot.Alignment),
            FontFamily = string.IsNullOrWhiteSpace(snapshot.FontFamily) ? _preferencesService.ComposerFont : snapshot.FontFamily,
            FontSize = snapshot.FontSize ?? _preferencesService.ComposerFontSize,
            ParagraphStyle = string.IsNullOrWhiteSpace(snapshot.ParagraphStyle) ? "div" : snapshot.ParagraphStyle,
            TextColor = snapshot.TextColor ?? string.Empty,
            HighlightColor = snapshot.HighlightColor ?? string.Empty,
            LineHeight = string.IsNullOrWhiteSpace(snapshot.LineHeight) ? "normal" : snapshot.LineHeight,
            LinkUrl = snapshot.LinkUrl,
            SelectedText = snapshot.SelectedText
        };
    }

    private static EditorTextAlignment ParseAlignment(string? alignment)
    {
        return alignment?.ToLowerInvariant() switch
        {
            "center" => EditorTextAlignment.Center,
            "right" => EditorTextAlignment.Right,
            "justify" => EditorTextAlignment.Justify,
            _ => EditorTextAlignment.Left
        };
    }

    private static string SerializeLinkArgs(EditorLinkCommandArgs args)
    {
        var url = JsonSerializer.Serialize(args.Url, BasicTypesJsonContext.Default.String);
        var text = JsonSerializer.Serialize(args.Text, BasicTypesJsonContext.Default.String);
        var openInNewWindow = JsonSerializer.Serialize(args.OpenInNewWindow, BasicTypesJsonContext.Default.Boolean);
        return $"{{\"url\":{url},\"text\":{text},\"openInNewWindow\":{openInNewWindow}}}";
    }

    private static string SerializeTableArgs(EditorTableCommandArgs args)
    {
        var rows = JsonSerializer.Serialize(args.Rows, BasicTypesJsonContext.Default.Int32);
        var columns = JsonSerializer.Serialize(args.Columns, BasicTypesJsonContext.Default.Int32);
        return $"{{\"rows\":{rows},\"columns\":{columns}}}";
    }

    private static EditorStateSnapshot? DeserializeStateSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DeserializeStateSnapshot(document.RootElement);
    }

    private static EditorStateSnapshot? DeserializeStateSnapshot(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new EditorStateSnapshot
        {
            IsBold = GetBoolean(element, "bold"),
            IsItalic = GetBoolean(element, "italic"),
            IsUnderline = GetBoolean(element, "underline"),
            IsStrikethrough = GetBoolean(element, "strikethrough"),
            IsOrderedList = GetBoolean(element, "orderedList"),
            IsUnorderedList = GetBoolean(element, "unorderedList"),
            CanIndent = GetBoolean(element, "canIndent", true),
            CanOutdent = GetBoolean(element, "canOutdent"),
            HasSelection = GetBoolean(element, "hasSelection"),
            IsSpellCheckEnabled = GetBoolean(element, "isSpellCheckEnabled", true),
            Alignment = GetString(element, "alignment"),
            FontFamily = GetString(element, "fontFamily"),
            FontSize = GetNullableInt32(element, "fontSize"),
            ParagraphStyle = GetString(element, "paragraphStyle"),
            TextColor = GetString(element, "textColor"),
            HighlightColor = GetString(element, "highlightColor"),
            LineHeight = GetString(element, "lineHeight"),
            LinkUrl = GetString(element, "linkUrl"),
            SelectedText = GetString(element, "selectedText")
        };
    }

    private static bool GetBoolean(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return valueElement.GetBoolean();
        }

        return defaultValue;
    }

    private static int? GetNullableInt32(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var valueElement) && valueElement.ValueKind == JsonValueKind.String)
        {
            return valueElement.GetString();
        }

        return null;
    }

    private static EditorCapabilities BuildCapabilities(IReadOnlyList<string> fonts)
    {
        return new EditorCapabilities
        {
            Fonts = fonts,
            FontSizes = DefaultFontSizes,
            TextColors = DefaultTextColors,
            HighlightColors = DefaultHighlightColors,
            ParagraphStyles = DefaultParagraphStyles,
            LineHeights = DefaultLineHeights,
            Alignments = DefaultAlignments
        };
    }

    private EditorState GetCurrentStateOrDefault()
    {
        return CurrentState ?? CreateDefaultState();
    }

    private EditorState CreateDefaultState()
    {
        return new EditorState
        {
            IsDarkMode = IsEditorDarkMode,
            IsBuiltInToolbarVisible = IsEditorWebViewEditor,
            IsSpellCheckEnabled = true,
            FontFamily = _preferencesService.ComposerFont,
            FontSize = _preferencesService.ComposerFontSize,
            ParagraphStyle = "div",
            TextColor = string.Empty,
            HighlightColor = string.Empty,
            LineHeight = "normal"
        };
    }

    private void UpdateState(EditorState newState)
    {
        if (newState == CurrentState)
        {
            return;
        }

        CurrentState = newState;
        StateChanged?.Invoke(this, CurrentState);
    }

    private void UpdateCapabilities(EditorCapabilities newCapabilities)
    {
        if (newCapabilities == Capabilities)
        {
            return;
        }

        Capabilities = newCapabilities;
        CapabilitiesChanged?.Invoke(this, Capabilities);
    }

    private void DetachChromium(WebView2 chromium, bool closeWebView)
    {
        chromium.CoreWebView2Initialized -= ChromiumInitialized;

        if (chromium.CoreWebView2 != null)
        {
            chromium.CoreWebView2.DOMContentLoaded -= DomLoaded;
            chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
        }

        if (closeWebView)
        {
            chromium.Close();
        }
    }

    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing && _chromium != null)
            {
                DetachChromium(_chromium, closeWebView: true);
                _chromium = null;
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private sealed class EditorWebMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("state")]
        public EditorStateSnapshot? State { get; set; }
    }

    private sealed class EditorStateSnapshot
    {
        [JsonPropertyName("bold")]
        public bool IsBold { get; set; }

        [JsonPropertyName("italic")]
        public bool IsItalic { get; set; }

        [JsonPropertyName("underline")]
        public bool IsUnderline { get; set; }

        [JsonPropertyName("strikethrough")]
        public bool IsStrikethrough { get; set; }

        [JsonPropertyName("orderedList")]
        public bool IsOrderedList { get; set; }

        [JsonPropertyName("unorderedList")]
        public bool IsUnorderedList { get; set; }

        [JsonPropertyName("canIndent")]
        public bool CanIndent { get; set; }

        [JsonPropertyName("canOutdent")]
        public bool CanOutdent { get; set; }

        [JsonPropertyName("hasSelection")]
        public bool HasSelection { get; set; }

        [JsonPropertyName("isSpellCheckEnabled")]
        public bool IsSpellCheckEnabled { get; set; } = true;

        [JsonPropertyName("alignment")]
        public string? Alignment { get; set; }

        [JsonPropertyName("fontFamily")]
        public string? FontFamily { get; set; }

        [JsonPropertyName("fontSize")]
        public int? FontSize { get; set; }

        [JsonPropertyName("paragraphStyle")]
        public string? ParagraphStyle { get; set; }

        [JsonPropertyName("textColor")]
        public string? TextColor { get; set; }

        [JsonPropertyName("highlightColor")]
        public string? HighlightColor { get; set; }

        [JsonPropertyName("lineHeight")]
        public string? LineHeight { get; set; }

        [JsonPropertyName("linkUrl")]
        public string? LinkUrl { get; set; }

        [JsonPropertyName("selectedText")]
        public string? SelectedText { get; set; }
    }
}









