# Wino.Editor

`Wino.Editor` is a WinUI 3 class library for composing and rendering HTML mail with WebView2. It contains no UWP implementation and no legacy `WebView` mode.

## Add the controls

Reference `Wino.Editor.csproj`, then add the namespace to a WinUI page:

```xml
xmlns:editor="using:Wino.Editor"
```

```xml
<editor:WinoMailEditor x:Name="MailEditor" />
<editor:WinoMailRenderer x:Name="MailRenderer" />
```

The two host-facing contracts are `IHtmlMailEditor` and `IHtmlMailRenderer`. The controls expose their underlying WinUI `WebView2` for Wino's printing and PDF flows. A host can set `WebViewEnvironment` before `Loaded` to reuse an existing `CoreWebView2Environment`.

Editor HTML, CSS, and JavaScript are embedded in the library assembly. A consuming application does not need to copy web assets into its package.

## Wino Mail integration

Wino Mail uses `WinoMailEditor` directly for compose, signatures, templates, and calendar notes. Its native command bar targets the library's `IEditorCommandTarget`. `WinoMailRenderer` owns mail and calendar HTML rendering. Wino's `ImageInfo` values are translated to `EditorImageInfo`, and compose content is read with `GetHtmlBodyAsync()`.

`GetHtmlBodyAsync()` deliberately returns the body fragment expected by Wino's compose pipeline. `GetHtmlAsync()` remains available when a complete HTML document is needed.

When Wino keeps its external command bar, set `ToolbarVisibility="Collapsed"` and `UseBuiltInFilePickers="False"`. Point the command bar at the editor through the library's `IEditorCommandTarget`, and handle `CommandRequested` for Wino-owned attachment, image, emoji, and security workflows.

For inline images, translate Wino's existing model without reflection:

```csharp
await MailEditor.InsertImagesAsync(
    images.Select(image => new EditorImageInfo(image.Data, image.Name)));
```

For message reading, call `RenderHtmlAsync`, `SetReaderTypographyAsync`, and the six-argument `SetAccessibilityContextAsync`. The existing `RenderHtmlAsync(string, bool)` overload uses `HtmlMailRenderMode.Original`; pass `HtmlMailRenderMode.Readability` to extract the primary content with the vendored Mozilla Readability library. Both modes sanitize with the offline DOMPurify bundle before insertion, and Readability mode sanitizes once before extraction and again afterward. `GetOriginalHtmlAsync()` continues to return the untouched input. Handle `NavigationRequested` with Wino's existing URI launcher. `GetUnderlyingWebView()` preserves the current print and PDF access point.

Set `BlockRemoteResources` before rendering a message whose remote content is not allowed. It blocks every network request at the WebView2 layer, including CSS backgrounds and web fonts that image stripping cannot reach.

## Rendering fidelity and dark mode

The reader renders each message into a shadow root. The mail's `<head>` and `<body>` styles are kept, and `html`, `body`, and `:root` selectors are rewritten onto wrapper elements. Mail CSS cannot restyle the reader, and reader CSS does not change the mail's layout. A shadow root is used instead of an iframe because Chromium cannot split an iframe across pages when printing or exporting to PDF. `@import` rules are dropped, `@font-face` rules are hoisted to the document, and `position: fixed` or `sticky` becomes `static`.

Dark mode follows the sender's intent first:

| Message | Dark theme result |
| --- | --- |
| Declares `color-scheme` or `supported-color-schemes` with `dark`, or has `prefers-color-scheme: dark` CSS | The sender's own dark styles |
| Declares `light only` | The original light design |
| Anything else | Selective adaptation by `mail-colors.js` |

The selective engine darkens light neutral and pastel backgrounds, keeps saturated and already dark colors, and changes text only when its contrast drops below what the sender's original pair had. Blocks with a background image or gradient are left untouched. Images whose surroundings were darkened keep their original backdrop so dark logos stay visible. The light theme always shows the original colors, so the host's per-message theme toggle doubles as Outlook's "original colors" switch. The composer uses the same engine for its dark writing surface through removable attributes, so sent HTML never contains dark-mode colors.

## Security boundaries

- Every HTML string that enters either document passes through DOMPurify, including editor content, pasted HTML, templates, and signatures.
- Both documents carry a nonce-based content security policy. Only the embedded scripts can run, so inline handlers and `javascript:` URLs stay inert even after a sanitizer bypass.
- WebView2 script dialogs, host objects, autofill, password saving, the status bar, and swipe navigation are off. DevTools are off in Release builds.
- The MIME visitor passes only `color-scheme` and `supported-color-schemes` meta tags to the reader, as a name and keyword-only content pair.

The pinned third-party scripts and their Apache-2.0 notices are under `Editor/ThirdParty`. `EditorAssetProvider` verifies the renderer globals during initialization and rejects an assembled `NavigateToString` document at or above WebView2's 2 MB limit.

## Native AOT and trimming

The project enables `IsAotCompatible`, the AOT analyzer, and the trim analyzer. All .NET/JavaScript bridge serialization uses the source-generated `EditorJsonContext`; reflection-based System.Text.Json serialization is disabled. Public models use explicit JSON names where they cross the bridge.

Validate the library from `D:\Wino-Mail` and the demo from `D:\WinoEditor` with:

```powershell
dotnet build controls\Wino.Editor\Wino.Editor.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build WinoWebEditor.WinUI\WinoWebEditor.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```
