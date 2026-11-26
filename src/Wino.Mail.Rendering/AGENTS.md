# Wino.Mail.Rendering

The glue between the native mail renderer (`Wino.Renderer`, a separate repository) and the shared seams of the app. Every head that reads and composes with the renderer uses this one project, so a change here changes each of them. It targets `net10.0` and references the engine through `build/Wino.Renderer.Engine.props`: the `WinoMail.Renderer` package, or a renderer checkout when `WINO_RENDERER_ROOT` (or `-p:WinoRendererRoot`) names one.

## What is here

| Type | Job |
| --- | --- |
| `MailEngineHost` | The one platform backend and the one `MailEngine` of the process, made on first use through the head's `IMailEngineBackendFactory` |
| `EngineMailMessageReader` | `IMailMessageReader`: a message from its MIME in the reading pane |
| `EngineHtmlReaderSession` | `IHtmlMailReaderSession`: an HTML string (the event description) |
| `EngineMailEditorSession` | `IHtmlMailEditorSession` and `IComposeBodyWriter`: composer, signature, template and event notes. Also `IComposeMentions`: the engine's "@" query, the person written into the message and the user's mention settings, which `ComposePageViewModel.Mentions` drives for every head |
| `EngineReaderPresentation` | Theme, typography, viewport and high contrast of one reader view |
| `EnginePrintDocuments` | The print options every head lays a message out with |
| `EngineUnsubscribeReader`, `EngineSpellCheckLanguages` | Unsubscribe information and the spell-check languages from the engine |
| `MailRenderingRegistration.AddMailRendering` | Registers the host and the services above; the head registers its backend factory |

## Hard stops

- **No UI framework type.** The sessions talk to the engine's `IMailView` and to Wino's `IDispatcher`. A view, a window, a menu or a print dialog stays in the head.
- **One engine per process.** Only `MailEngineHost` creates one; heads and sessions take it from the container.
- **No HTML handling outside the engine.** Sanitizing, link rules, remote content and dark mode belong to the renderer repository; a fault there is fixed there.
- **The engine API is public, the glue is not a second copy of it.** Map between Wino's types and the engine's here; put rules every platform needs in the engine.

## Check your change

```sh
dotnet build src/Wino.Mail.Rendering/Wino.Mail.Rendering.csproj -c Debug
dotnet build src/Wino.Mail.Rendering/Wino.Mail.Rendering.csproj -c Release
```

Both with zero warnings; the trim and AOT analyzers are on. Logic runs from a scratch script against the real engine (see "Running backend code without an app" in the [root guidance](../../AGENTS.md)). Then build every head that uses it and report the ones this machine cannot build as pending: Windows ([src/Wino.Mail.WinUI/AGENTS.md](../Wino.Mail.WinUI/AGENTS.md)), and the Apple heads once they are on the renderer.
