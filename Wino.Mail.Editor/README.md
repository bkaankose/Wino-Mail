# Wino.Mail.Editor

Reusable modern-.NET UWP mail composer control. The library owns its browser-hosted editor assets, formatting UI, system-font discovery, inline-image handling, and table editing.

```xml
<editor:WinoMailEditor
    x:Name="Composer"
    StatusBarVisibility="Collapsed"
    UseBuiltInFilePickers="False" />
```

The default `WebViewMode="Auto"` uses WebView2 when its runtime is available and automatically falls back to the built-in UWP `WebView`. Force compatibility mode when required:

```xml
<editor:WinoMailEditor WebViewMode="WebView" />
```

Use `WebViewMode="WebView2"` only when failure is preferable to fallback. `ActiveWebViewMode` reports the host selected after initialization. The mode can be changed at runtime; the control preserves the current HTML, unloads the old browser, creates only the requested browser through `x:Load`, and reapplies editor settings.

Configure commands with `EnabledFeatures`:

```csharp
Composer.EnabledFeatures =
    MailEditorFeatures.FontFamily |
    MailEditorFeatures.FontSize |
    MailEditorFeatures.TextStyles |
    MailEditorFeatures.Paragraph |
    MailEditorFeatures.Hyperlinks |
    MailEditorFeatures.InlineImages |
    MailEditorFeatures.Attachments |
    MailEditorFeatures.SmimeSigning;
```

Host integration points:

- `CommandRequested`: intercept attachment/image requests or observe S/MIME state changes.
- `AttachmentsSelected`: receives files selected by the built-in attachment picker.
- `InlineImagesSelected`: receives files selected before they are embedded as data URIs.
- `ContentChanged` and `SelectionStateChanged`: editor state notifications.
- `IsSmimeSigningEnabled` and `IsSmimeEncryptionEnabled`: host-owned send options. Cryptographic signing/encryption intentionally remains in the mail send pipeline.
- `AvailableFonts`: bindable dependency property populated from Win2D system-font discovery; hosts may replace it with a filtered list of `EditorFontFamilyOption` values.
- `WebViewMode`: selects automatic fallback, WebView2-only, or built-in UWP WebView compatibility mode.
- `ToolbarVisibility`, `StatusBarVisibility`, `IsReadOnly`, and `UseBuiltInFilePickers`: embedding behavior.

`GetHtmlAsync()` always returns a complete `<html><head>…</head><body>…</body></html>` document. `SetHtmlAsync()` accepts a full document or a body fragment; `SetReplyHtmlAsync()` adds the reply area above a previous message.

Editor and renderer files are packaged into the consuming app under `Wino.Mail.Editor/Assets/Editor`; nothing is extracted to `LocalCache`. Both controls expose `WebViewMode` (`Auto`, `WebView2`, or `WebView`), create only the selected browser through `x:Load`, and support runtime mode changes. Both browser modes read the packaged files through `ms-appx:///` and load a self-contained document with `NavigateToString`, avoiding browser-specific relative resource resolution and requiring no physical folder lookup or cache copy. `WinoMailRenderer` renders supplied HTML in a read-only document and preserves accessibility context, navigation interception, typography overrides, original HTML retrieval, and access to whichever underlying browser is active. Chromium mode additionally uses Linkify and DarkReader; compatibility mode uses the lightweight packaged reader without those Chromium-oriented bundles.
