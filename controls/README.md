# Controls

Reusable presentation lives here. Windows uses WinUI 3; macOS uses AppKit. The only playground is the Windows sample host.

- `MailCal.Mail.Controls.Core` owns platform-neutral projection and collection rules.
- `MailCal.Mail.Controls.WinUI` retains the existing reusable WinUI controls and CLR namespaces.
- `MailCal.Mail.Controls.AppKit` presents observable collections with native table rows.
- `MailCal.Presentation.AppKit` provides typed bindings, disposable scopes, command error handling, and Auto Layout helpers.
- `MailCal.Editor.Core` owns portable sessions, JSON contracts, operation queues, document policies, and the sole embedded reader/editor asset tree in `Editor/`.
- `MailCal.Editor.WinUI` provides WebView2 controls and Windows session adapters.
- `MailCal.Editor.AppKit` implements WKWebView sessions with generation-correlated messages and native delegate ownership.
- `MailCal.Mail.Controls.Playground.WinUI` demonstrates every public Windows control. Native Mac scenarios belong in `src/MailCal.Mail.MacOS`.

See [agent guidance](AGENTS.md) for build, resource ownership, and verification rules. Build evidence does not replace manual lab checks.
