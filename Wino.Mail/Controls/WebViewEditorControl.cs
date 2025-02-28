using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.UWP;
using Wino.Core.UWP.Extensions;

namespace Wino.Mail.Controls;
public sealed partial class WebViewEditorControl : Control, IDisposable
{
    private readonly INativeAppService _nativeAppService = App.Current.Services.GetService<INativeAppService>();
    private readonly IFontService _fontService = App.Current.Services.GetService<IFontService>();
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>();

    [GeneratedDependencyProperty]
    public partial bool IsEditorDarkMode { get; set; }
    async partial void OnIsEditorDarkModeChanged(bool newValue)
    {
        await UpdateEditorThemeAsync();
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorBold { get; set; }
    private bool _isEditorBoldInternal;
    async partial void OnIsEditorBoldChanged(bool newValue)
    {
        if (newValue != _isEditorBoldInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("bold", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorItalic { get; set; }
    private bool _isEditorItalicInternal;
    async partial void OnIsEditorItalicChanged(bool newValue)
    {
        if (newValue != _isEditorItalicInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("italic", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorUnderline { get; set; }
    private bool _isEditorUnderlineInternal;
    async partial void OnIsEditorUnderlineChanged(bool newValue)
    {
        if (newValue != _isEditorUnderlineInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("underline", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorStrikethrough { get; set; }
    private bool _isEditorStrikethroughInternal;
    async partial void OnIsEditorStrikethroughChanged(bool newValue)
    {
        if (newValue != _isEditorStrikethroughInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("strikethrough", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorOl { get; set; }
    private bool _isEditorOlInternal;
    async partial void OnIsEditorOlChanged(bool newValue)
    {
        if (newValue != _isEditorOlInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("insertorderedlist", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorUl { get; set; }
    private bool _isEditorUlInternal;
    async partial void OnIsEditorUlChanged(bool newValue)
    {
        if (newValue != _isEditorUlInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("insertunorderedlist", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsEditorIndentEnabled { get; private set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorOutdentEnabled { get; private set; }

    [GeneratedDependencyProperty]
    public partial int EditorAlignmentSelectedIndex { get; set; }
    private int _editorAlignmentSelectedIndexInternal;
    async partial void OnEditorAlignmentSelectedIndexChanged(int newValue)
    {
        if (newValue != _editorAlignmentSelectedIndexInternal)
        {
            var alignmentAction = newValue switch
            {
                0 => "justifyleft",
                1 => "justifycenter",
                2 => "justifyright",
                3 => "justifyfull",
                _ => throw new ArgumentOutOfRangeException(nameof(newValue))
            };

            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize(alignmentAction, BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorWebViewEditor { get; set; }

    async partial void OnIsEditorWebViewEditorChanged(bool newValue)
    {
        await _chromium.ExecuteScriptFunctionSafeAsync("toggleToolbar", JsonSerializer.Serialize(newValue, BasicTypesJsonContext.Default.Boolean));
    }

    private const string PART_WebView = "WebView";
    private WebView2 _chromium;
    private bool _disposedValue;
    private readonly TaskCompletionSource<bool> _domLoadedTask = new();

    public WebViewEditorControl()
    {
        this.DefaultStyleKey = typeof(WebViewEditorControl);

        IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
    }

    protected override async void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _chromium = GetTemplateChild(PART_WebView) as WebView2;

        await InitializeComponent();
    }

    private async Task InitializeComponent()
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation");
        _chromium.CoreWebView2Initialized += ChromiumInitialized;

        await _chromium.EnsureCoreWebView2Async();
    }

    public async void EditorIndentAsync()
    {
        await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("indent", BasicTypesJsonContext.Default.String));
    }

    public async void EditorOutdentAsync()
    {
        await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("outdent", BasicTypesJsonContext.Default.String));
    }

    public void ToggleEditorTheme()
    {
        IsEditorDarkMode = !IsEditorDarkMode;
    }

    public async Task<string> GetHtmlBodyAsync()
    {
        var editorContent = await _chromium.ExecuteScriptFunctionSafeAsync("GetHTMLContent");

        return JsonSerializer.Deserialize(editorContent, BasicTypesJsonContext.Default.String);
    }

    public async void ShowImagePicker()
    {
        await _chromium.ExecuteScriptFunctionSafeAsync("imageInput.click");
    }

    public async Task InsertImagesAsync(List<ImageInfo> images)
    {
        await _chromium.ExecuteScriptFunctionSafeAsync("insertImages", JsonSerializer.Serialize(images, DomainModelsJsonContext.Default.ListImageInfo));
    }

    public async void ShowEmojiPicker()
    {
        CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);

        await FocusEditorAsync(focusControlAsWell: true);
    }

    public WebView2 GetUnderlyingWebView() => _chromium;

    public async Task RenderHtmlAsync(string htmlBody)
    {
        await _domLoadedTask.Task;

        await UpdateEditorThemeAsync();
        await InitializeEditorAsync();

        await _chromium.ExecuteScriptFunctionAsync("RenderHTML", parameters: JsonSerializer.Serialize(string.IsNullOrEmpty(htmlBody) ? " " : htmlBody, BasicTypesJsonContext.Default.String));
    }

    private async Task<string> InitializeEditorAsync()
    {
        var fonts = _fontService.GetFonts();
        var composerFont = _preferencesService.ComposerFont;
        int composerFontSize = _preferencesService.ComposerFontSize;
        var readerFont = _preferencesService.ReaderFont;
        int readerFontSize = _preferencesService.ReaderFontSize;
        return await _chromium.ExecuteScriptFunctionAsync("initializeJodit", false,
            JsonSerializer.Serialize(fonts, BasicTypesJsonContext.Default.ListString),
            JsonSerializer.Serialize(composerFont, BasicTypesJsonContext.Default.String),
            JsonSerializer.Serialize(composerFontSize, BasicTypesJsonContext.Default.Int32),
            JsonSerializer.Serialize(readerFont, BasicTypesJsonContext.Default.String),
            JsonSerializer.Serialize(readerFontSize, BasicTypesJsonContext.Default.Int32));
    }

    private async void ChromiumInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        var editorBundlePath = (await _nativeAppService.GetEditorBundlePathAsync()).Replace("editor.html", string.Empty);

        _chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.editor", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);
        _chromium.Source = new Uri("https://app.editor/editor.html");

        _chromium.CoreWebView2.DOMContentLoaded += DomLoaded;

        _chromium.CoreWebView2.WebMessageReceived += ScriptMessageReceived;
    }

    public async Task UpdateEditorThemeAsync()
    {
        await _domLoadedTask.Task;

        if (IsEditorDarkMode)
        {
            _chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            await _chromium.ExecuteScriptFunctionSafeAsync("SetDarkEditor");
        }
        else
        {
            _chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            await _chromium.ExecuteScriptFunctionSafeAsync("SetLightEditor");
        }
    }

    /// <summary>
    /// Places the cursor in the composer.
    /// </summary>
    /// <param name="focusControlAsWell">Whether control itself should be focused as well or not.</param>
    public async Task FocusEditorAsync(bool focusControlAsWell)
    {
        await _chromium.ExecuteScriptSafeAsync("editor.selection.setCursorIn(editor.editor.firstChild, true)");

        if (focusControlAsWell)
        {
            _chromium.Focus(FocusState.Keyboard);
            _chromium.Focus(FocusState.Programmatic);
        }
    }

    private void ScriptMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var change = JsonSerializer.Deserialize(args.WebMessageAsJson, DomainModelsJsonContext.Default.WebViewMessage);

        if (change.Type == "bold")
        {
            _isEditorBoldInternal = change.Value == "true";
            IsEditorBold = _isEditorBoldInternal;
        }
        else if (change.Type == "italic")
        {
            _isEditorItalicInternal = change.Value == "true";
            IsEditorItalic = _isEditorItalicInternal;
        }
        else if (change.Type == "underline")
        {
            _isEditorUnderlineInternal = change.Value == "true";
            IsEditorUnderline = _isEditorUnderlineInternal;
        }
        else if (change.Type == "strikethrough")
        {
            _isEditorStrikethroughInternal = change.Value == "true";
            IsEditorStrikethrough = _isEditorStrikethroughInternal;
        }
        else if (change.Type == "ol")
        {
            _isEditorOlInternal = change.Value == "true";
            IsEditorOl = _isEditorOlInternal;
        }
        else if (change.Type == "ul")
        {
            _isEditorUlInternal = change.Value == "true";
            IsEditorUl = _isEditorUlInternal;
        }
        else if (change.Type == "indent")
        {
            IsEditorIndentEnabled = change.Value != "disabled";
        }
        else if (change.Type == "outdent")
        {
            IsEditorOutdentEnabled = change.Value != "disabled";
        }
        else if (change.Type == "alignment")
        {
            var parsedValue = change.Value switch
            {
                "jodit-icon_left" => 0,
                "jodit-icon_center" => 1,
                "jodit-icon_right" => 2,
                "jodit-icon_justify" => 3,
                _ => 0
            };
            _editorAlignmentSelectedIndexInternal = parsedValue;
            EditorAlignmentSelectedIndex = _editorAlignmentSelectedIndexInternal;
        }
    }

    private void DomLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => _domLoadedTask.TrySetResult(true);

    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing && _chromium != null)
            {
                _chromium.CoreWebView2Initialized -= ChromiumInitialized;

                if (_chromium.CoreWebView2 != null)
                {
                    _chromium.CoreWebView2.DOMContentLoaded -= DomLoaded;
                    _chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
                }

                _chromium.Close();
            }
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
