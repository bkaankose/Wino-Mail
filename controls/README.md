# Controls

Reusable presentation lives here. Windows uses WinUI 3, macOS uses AppKit, and iOS and iPadOS use UIKit. The only playground is the Windows sample host.

- `Wino.Mail.Controls.Core` owns platform-neutral projection and collection rules.
- `Wino.Mail.Controls.WinUI` retains the existing reusable WinUI controls and CLR namespaces.
- `Wino.Mail.Controls.AppKit` presents observable collections with native table rows.
- `Wino.Presentation.Core` provides typed bindings, disposable scopes, command error handling, design tokens, and Domain-aware control rules for both Apple heads, without a UI framework.
- `Wino.Presentation.AppKit` provides Auto Layout helpers, WinoIcons, styles, surfaces, and zones for AppKit.
- `Wino.Editor.Core` owns portable sessions, JSON contracts, operation queues, document policies, and the sole embedded reader/editor asset tree in `Editor/`.
- `Wino.Editor.WinUI` provides WebView2 controls and Windows session adapters.
- `Wino.Editor.WebKit` implements WKWebView sessions for macOS and iOS with generation-correlated messages and native delegate ownership.
- `Wino.Presentation.UIKit` and `Wino.Mail.Controls.UIKit` are the UIKit twins of the AppKit presentation and control libraries, with the same member names.
- `Wino.Mail.Controls.Playground.WinUI` demonstrates every public Windows control. Native Mac scenarios belong in `src/Wino.Mail.MacOS`, and iOS scenarios in `src/Wino.Mail.iOS` and its UIKit projects.

See [agent guidance](AGENTS.md) for build, resource ownership, and verification rules. Build evidence does not replace manual lab checks.
