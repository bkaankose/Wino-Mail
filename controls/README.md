# Controls

Reusable presentation lives here. Windows uses WinUI 3, macOS uses AppKit, iOS and iPadOS use UIKit, and Android uses Android Views. The only playground is the Windows sample host.

- `Wino.Mail.Controls.Core` owns platform-neutral projection and collection rules.
- `Wino.Mail.Controls.WinUI` retains the existing reusable WinUI controls and CLR namespaces.
- `Wino.Mail.Controls.AppKit` presents observable collections with native table rows.
- `Wino.Presentation.Core` provides typed bindings, disposable scopes, command error handling, design tokens, and Domain-aware control rules for the Apple and Android heads, without a UI framework.
- `Wino.Presentation.AppKit` provides Auto Layout helpers, WinoIcons, styles, surfaces, and zones for AppKit.
- `Wino.Editor.Core` owns portable sessions, JSON contracts, operation queues, document policies, and the sole embedded reader/editor asset tree in `Editor/`.
- `Wino.Editor.WinUI` provides WebView2 controls and Windows session adapters.
- `Wino.Editor.WebKit` implements WKWebView sessions for macOS and iOS with generation-correlated messages and native delegate ownership.
- `Wino.Presentation.UIKit` and `Wino.Mail.Controls.UIKit` are the UIKit twins of the AppKit presentation and control libraries, with the same member names.
- `Wino.Presentation.Android` and `Wino.Mail.Controls.Android` are the Android Views twins of the UIKit libraries, with tokens in Android resources and an `ILLink.Descriptors.xml` for views that only XML layouts name.
- `Wino.Editor.Android` implements Android `WebView` reader and editor sessions over `Wino.Editor.Core`.
- `Wino.Mail.Controls.Playground.WinUI` demonstrates every public Windows control. Native Mac scenarios belong in `src/Wino.Mail.MacOS`, iOS scenarios in `src/Wino.Mail.iOS` and its UIKit projects, and Android scenarios in `src/Wino.Mail.Android` (its Debug `ControlsGallery` activity shows the Android controls).

See [agent guidance](AGENTS.md) for build, resource ownership, and verification rules. Build evidence does not replace manual lab checks.
