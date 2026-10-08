# Controls

Reusable presentation lives here. Windows uses WinUI 3; macOS uses AppKit. The only playground is the Windows sample host.

- `Wino.Mail.Controls.Core` owns platform-neutral projection and collection rules.
- `Wino.Mail.Controls.WinUI` retains the existing reusable WinUI controls and CLR namespaces.
- `Wino.Mail.Controls.AppKit` presents observable collections with native table rows.
- `Wino.Presentation.AppKit` provides typed bindings, disposable scopes, command error handling, and Auto Layout helpers.
- `Wino.Editor.Core` owns portable sessions, JSON contracts, operation queues, document policies, and the sole embedded reader/editor asset tree in `Editor/`.
- `Wino.Editor.WinUI` provides WebView2 controls and Windows session adapters.
- `Wino.Editor.AppKit` implements WKWebView sessions with generation-correlated messages and native delegate ownership.
- `Wino.Mail.Controls.Playground.WinUI` demonstrates every public Windows control. Native Mac scenarios belong in `src/Wino.Mail.MacOS`.

See [agent guidance](AGENTS.md) for build, resource ownership, and verification rules. Build evidence does not replace manual lab checks.
