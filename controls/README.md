# Controls

This directory contains reusable Wino Mail UI controls and the one WinUI 3 playground app used to develop them without starting the full mail client.

- `Wino.Editor` provides the shared WebView2 mail reader and editor, including its embedded HTML, CSS, and JavaScript.
- `Wino.Mail.Controls.Core` contains the platform-neutral models and collection logic used by mail controls.
- `Wino.Mail.Controls` contains the WinUI controls built on the core abstractions.
- `Wino.Mail.Controls.Playground` is the single demo app. It references both control libraries and provides a page for every public UI control.

Keep reusable UI and its supporting abstractions here. When adding a public control, add its demonstration page to `Wino.Mail.Controls.Playground` in the same change.
