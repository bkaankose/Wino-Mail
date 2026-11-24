# Wino.Editor.Abstractions

The platform-neutral contracts between a head and its mail reader and composer: the session interfaces (`IHtmlMailReaderSession`, `IHtmlMailEditorSession`), the command contract (`IEditorCommandTarget`, `EditorCommand`, `EditorState`, the shortcut and capability models) and the reader's link event arguments. Every head implements or consumes them, whether its reader is a web view or a native renderer. This file is binding for `controls/Wino.Editor.Abstractions`.

## Hard stops

- **No dependency.** The project references no project and no package, and it embeds no HTML, CSS or JavaScript. A head that renders without a web view references this project alone and so ships none of the web assets in [Wino.Editor.Core](../AGENTS.md).
- **No engine or platform type.** A contract names what a head needs (render, get the body, execute a command), never a WebView, a native view or a renderer type.
- The root namespace is `Wino.Editor`, the same as `Wino.Editor.Core`, and every file stays at the project root; `IDE0130` fails the build on a namespace that does not match the folder.
- A type that crosses the JavaScript bridge keeps its JSON attributes here, and its serialization stays in the source-generated `EditorJsonContext` in `Wino.Editor.Core`.

## Always

- A change to a contract here changes every head. Update each implementation (`src/Wino.Mail.Rendering`, shared by the heads on the native renderer, `Wino.Editor.WebKit`, `Wino.Editor.WebKitGtk`) in the same change, including the ones you cannot build, and report those builds as pending.
- The bridge message record (`RendererMessage`), the operation queue and the document assets belong to `Wino.Editor.Core`, not here.

## Running a change

```powershell
dotnet build controls/Wino.Editor.Abstractions/Wino.Editor.Abstractions.csproj -c Debug -p:Platform=x64
```

Then build the consumers as [controls/AGENTS.md](../AGENTS.md) describes.
