using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.UWP.Extensions;

namespace Wino.Mail.Controls;
public sealed partial class WebViewEditorControl : Control, IDisposable
{
    private readonly INativeAppService _nativeAppService = App.Current.Services.GetService<INativeAppService>();
    private readonly IFontService _fontService = App.Current.Services.GetService<IFontService>();
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>();
    private readonly IUnderlyingThemeService _underlyingThemeService = App.Current.Services.GetService<IUnderlyingThemeService>();

    [GeneratedDependencyProperty]
    public partial bool IsEditorDarkMode { get; set; }
    async partial void OnIsEditorDarkModeChanged(bool newValue)
    {
        await UpdateEditorThemeAsync();
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorBold { get; set; }
    private bool IsEditorBoldInternal { get; set; }
    async partial void OnIsEditorBoldChanged(bool newValue)
    {
        if (newValue != IsEditorBoldInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("bold", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorItalic { get; set; }
    private bool IsEditorItalicInternal { get; set; }
    async partial void OnIsEditorItalicChanged(bool newValue)
    {
        if (newValue != IsEditorItalicInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("italic", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorUnderline { get; set; }
    private bool IsEditorUnderlineInternal { get; set; }
    async partial void OnIsEditorUnderlineChanged(bool newValue)
    {
        if (newValue != IsEditorUnderlineInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("underline", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorStrikethrough { get; set; }
    private bool IsEditorStrikethroughInternal { get; set; }
    async partial void OnIsEditorStrikethroughChanged(bool newValue)
    {
        if (newValue != IsEditorStrikethroughInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("strikethrough", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorOl { get; set; }
    private bool IsEditorOlInternal { get; set; }
    async partial void OnIsEditorOlChanged(bool newValue)
    {
        if (newValue != IsEditorOlInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("insertorderedlist", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorUl { get; set; }
    private bool IsEditorUlInternal { get; set; }
    async partial void OnIsEditorUlChanged(bool newValue)
    {
        if (newValue != IsEditorUlInternal)
        {
            await _chromium.ExecuteScriptFunctionSafeAsync("editor.execCommand", JsonSerializer.Serialize("insertunorderedlist", BasicTypesJsonContext.Default.String));
        }
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorIndentEnabled { get; private set; }

    [GeneratedDependencyProperty]
    public partial bool IsEditorOutdentEnabled { get; private set; }

    [GeneratedDependencyProperty]
    public partial ObservableCollection<AlignmentOption> EditorAlignmentOptions { get; set; }

    [GeneratedDependencyProperty]
    public partial int EditorAlignmentSelectedId { get; set; }

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

        IsEditorIndentEnabled = true;

        IsEditorDarkMode = _underlyingThemeService.IsUnderlyingThemeDark();

        EditorAlignmentOptions = [
               new AlignmentOption { Tag = 1, DisplayText = "Left", Icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), App.Current.Resources["AlignLeftPathIcon"]) } },
                new AlignmentOption { Tag = 2, DisplayText = "Center", Icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), App.Current.Resources["AlignCenterPathIcon"]) } },
                new AlignmentOption { Tag = 3, DisplayText = "Right", Icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), App.Current.Resources["AlignRightPathIcon"]) } },
                new AlignmentOption { Tag = 4, DisplayText = "Justify", Icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), App.Current.Resources["AlignJustifyPathIcon"]) } }];
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
        _chromium.CoreWebView2Initialized -= ChromiumInitialized;
        _chromium.CoreWebView2Initialized += ChromiumInitialized;

        await _chromium.EnsureCoreWebView2Async();

        await RenderHtmlAsync(string.Empty);
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

    /// <summary>
    /// Inserts images into the editor.
    /// </summary>
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

        _chromium.CoreWebView2.DOMContentLoaded -= DomLoaded;
        _chromium.CoreWebView2.DOMContentLoaded += DomLoaded;

        _chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
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
        // TODO
        //await InvokeScriptSafeAsync("editor.selection.setCursorIn(editor.editor.firstChild, true)");

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
            IsEditorBoldInternal = change.Value == "true";
            IsEditorBold = IsEditorBoldInternal;
        }
        else if (change.Type == "italic")
        {
            IsEditorItalicInternal = change.Value == "true";
            IsEditorItalic = IsEditorItalicInternal;
        }
        else if (change.Type == "underline")
        {
            IsEditorUnderlineInternal = change.Value == "true";
            IsEditorUnderline = IsEditorUnderlineInternal;
        }
        else if (change.Type == "strikethrough")
        {
            IsEditorStrikethroughInternal = change.Value == "true";
            IsEditorStrikethrough = IsEditorStrikethroughInternal;
        }
        else if (change.Type == "ol")
        {
            IsEditorOlInternal = change.Value == "true";
            IsEditorOl = IsEditorOlInternal;
        }
        else if (change.Type == "ul")
        {
            IsEditorUlInternal = change.Value == "true";
            IsEditorUl = IsEditorUlInternal;
        }
        else if (change.Type == "indent")
        {
            IsEditorIndentEnabled = change.Value != "disabled";
        }
        else if (change.Type == "outdent")
        {
            IsEditorOutdentEnabled = change.Value != "disabled";
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

public class AlignmentOption
{
    public int Tag { get; set; }
    public string DisplayText { get; set; }
    public PathIcon Icon { get; set; }
}
