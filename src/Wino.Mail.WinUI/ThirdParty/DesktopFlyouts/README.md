# Embedded DesktopFlyouts host

This folder contains a Wino-specific, trimmed adaptation of DesktopFlyouts.WinUI 1.4.0,
upstream commit `bf5f4cf1e6bb23aff4bf4893f0de05635153eb3a`.

The host and animation port was reviewed against upstream `main` commit
`e2356fe9f796a36e1ffc55a86d549fc41f39a6d9` on 2026-09-13.
`TransitionHelpers.cs` retains the four upstream slide storyboards verbatim, apart from
namespace and platform directives. The surface follows `DesktopFlyouts.Wasdk/DesktopFlyout.xaml`:
a 12-DIP outer margin, a rounded island with a one-pixel stroke, `ThemeShadow` at Z=36,
and `SystemBackdropElement` inside the animated root. The Mica controller and configuration
are ported from the upstream backdrop helpers; Acrylic and other platform branches are omitted.

The HWND uses `WS_EX_NOREDIRECTIONBITMAP`, stays at its final position during animation,
and has no window-wide backdrop. Both native windows receive the upstream rectangular
clipping region and explicit SiteBridge show/hide lifecycle. Do not replace the storyboards
with a timer that calls `SetWindowPos`, or move the backdrop onto `DesktopWindowXamlSource`.

Wino retains its taskbar placement, exception boundary, and visible-only focus monitor.
Tray icon, menu flyout, UWP/Uno, multi-island, and gesture machinery are excluded.
See `LICENSE` for the upstream MIT license.

The in-process boundary catches managed failures at tray, host, refresh, command, and
disposal boundaries. It cannot isolate the main application from process-corrupting
failures such as access violations, stack overflows, or exhausted process memory.
