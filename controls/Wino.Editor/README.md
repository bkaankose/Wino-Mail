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

For message reading, call `RenderHtmlAsync`, `SetReaderTypographyAsync`, and the six-argument `SetAccessibilityContextAsync`. Handle `NavigationRequested` with Wino's existing URI launcher. `GetUnderlyingWebView()` preserves the current print and PDF access point.

## Native AOT and trimming

The project enables `IsAotCompatible`, the AOT analyzer, and the trim analyzer. All .NET/JavaScript bridge serialization uses the source-generated `EditorJsonContext`; reflection-based System.Text.Json serialization is disabled. Public models use explicit JSON names where they cross the bridge.

Validate the library from `D:\Wino-Mail` and the demo from `D:\WinoEditor` with:

```powershell
dotnet build controls\Wino.Editor\Wino.Editor.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build WinoWebEditor.WinUI\WinoWebEditor.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```
