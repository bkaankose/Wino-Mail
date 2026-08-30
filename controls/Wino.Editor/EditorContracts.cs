using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Wino.Editor;

/// <summary>
/// Host-facing compose contract aligned with Wino Mail's current WebView2 editor usage.
/// </summary>
public interface IHtmlMailEditor : IEditorCommandTarget, IDisposable
{
    bool IsEditorDarkMode { get; set; }
    bool IsEditorWebViewEditor { get; set; }
    bool IsPasteOptionsEnabled { get; set; }
    CoreWebView2Environment? WebViewEnvironment { get; set; }

    Task InitializeAsync();
    Task RenderHtmlAsync(string htmlBody);
    Task<string?> GetHtmlBodyAsync();
    Task SetDefaultTypographyAsync(string? fontFamily, int fontSize);
    Task FocusEditorAsync(bool focusControlAsWell);
    Task SetApplicationShortcutsAsync(IReadOnlyList<EditorApplicationShortcutGesture> shortcuts);
    event EventHandler<EditorApplicationShortcutGesture>? ApplicationShortcutRequested;
    Task InsertImagesAsync(IEnumerable<EditorImageInfo> images);
    void SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel level);
    WebView2 GetUnderlyingWebView();
}

/// <summary>
/// Host-facing reader contract aligned with Wino Mail's current rendering and printing flow.
/// </summary>
public interface IHtmlMailRenderer : IDisposable
{
    bool IsDarkMode { get; set; }
    CoreWebView2Environment? WebViewEnvironment { get; set; }

    Task InitializeAsync();
    Task RenderHtmlAsync(string html, bool shouldLinkify = true);
    Task ClearAsync();
    Task<string> GetOriginalHtmlAsync();
    Task SetReaderTypographyAsync(string? fontFamily, int fontSize);
    Task SetAccessibilityContextAsync(
        string? subject,
        string? sender,
        string? date,
        string? bodyContext = null);
    Task SetAccessibilityContextAsync(
        string? subject,
        string? sender,
        string? date,
        string? bodyAutomationName,
        string? plainTextFallbackAutomationName,
        string? accessibleText);
    Task EnterIdleAsync();
    void SetMemoryUsageTargetLevel(CoreWebView2MemoryUsageTargetLevel level);
    WebView2 GetUnderlyingWebView();
}
