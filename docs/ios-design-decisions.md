# Wino Mail for iOS and iPadOS: design decisions

Decided on 13 October 2026. These decisions fix the shell, workflow and control choices for the universal UIKit app in `src/Wino.Mail.iOS`, its UI projects `src/Wino.Mail.UIKit`, `src/Wino.Mail.UIKit.Phone` and `src/Wino.Mail.UIKit.Pad`, and the UIKit control libraries under `controls/`. Shared ViewModels, services, sync, storage and the HTML reader/editor engine are unchanged by any of them; every row below is a presentation decision.

Design canvas: <https://claude.ai/artifact/9iAW48NVfnnMNZTh39YhQL> (built on the Wino Design System with the WinoIcons fonts and the Windows wallpapers).

Build, identity and verification commands are in [iOS development](harness/development-ios.md). The [macOS design decisions](macos-design-decisions.md), Revision 2, apply unless this document overrides them.

## Principles

1. **Native first.** C# UIKit on .NET for iOS. Native controls (`UITabBarController`, `UISplitViewController`, `UINavigationController`, `UITableView`, `UISearchController`, sheets, popovers, `UIMenu`) wherever UIKit has one. Wino draws only the mail row, folder row, avatar, pills, chips, zone backdrop, info bar and settings cards, as on the Mac. No Uno, MAUI or Mac Catalyst.
2. **Same place, simpler phone.** The iPad mirrors the Mac shell. The iPhone keeps the same modes, rows, names and command order in a phone flow.
3. **Shared backend.** UIKit views bind through explicit C# adapters to the shared ViewModels. Business rules live in shared code, never in an Apple project.

## Settled decisions

| Topic | Decision |
| --- | --- |
| Packaging | One universal app bundle with the Mac app's bundle ID `com.winomail.macos` (one App Store record for macOS, iOS and iPadOS; decided 13 Oct 2026), iOS and iPadOS 17.0 minimum. `Composition.cs` picks the phone or pad UI project by `UIDevice.UserInterfaceIdiom`. Both UI projects reference `Wino.Mail.UIKit` and never each other. |
| Modes | Mail, Calendar, Contacts and To Do, in the desktop order. Settings is never a mode on iPhone or iPad. |
| Settings | The account avatar opens Settings as a full-screen sheet: the Mailboxes bar (top-left) on iPhone, the toolbar (top-right) on iPad. Later Settings pages push inside the sheet. |
| iPhone shell | Tab bar with the four modes. Each tab is a navigation stack with the avatar button. |
| iPad shell | Mirrors the Mac shell: a toolbar row (sidebar toggle, sync with title and subtitle, centred search, Daily briefing, What's New, avatar), a sidebar with the Windows rows and the four-mode switcher card at its foot, a 360pt list, and the reader with its command bar on top. Never the floating top tab bar. |
| iPhone Mail flow | Mailboxes, then the folder list, then the message. The reader has a bottom toolbar: Archive, Delete, Move, Flag, a Reply menu (Reply, Reply all, Forward) and More. Leading swipe archives; trailing swipes are Delete, Flag and More. Long press opens a menu for rare commands. Focused/Other stays a segmented control. |
| Sheets | Compose, Move and the Daily briefing are sheets on iPhone. |
| Not on iPhone | Hover actions, density, reading-pane options, Companion, detached compose and global hotkeys. |
| Onboarding | Uses the Wino Mail logo (`src/Wino.Mail.WinUI/Assets/WinoLogo.png`), not the Inbox glyph. |
| Backdrop | The theme wallpaper or gradient paints the whole screen. Bars are blurred over it. Content sits in one rounded Wino zone per page. |
| Icons | Every glyph comes from the WinoIcons fonts. SF Symbols are not used. |

## Differences from Mac Revision 2

| Area | macOS (Revision 2) | iOS and iPadOS |
| --- | --- | --- |
| Mode switcher | Card with five segments, Settings included | Four segments on the iPad sidebar card; the iPhone uses the tab bar. `WinoModeSwitcher` shows four by default. |
| Settings | Its own window on Cmd+, | A full-screen sheet from the avatar on both idioms (and Cmd+, on iPad). Every Settings route registers as `FullScreenSheet` (`SettingsPageRoutes.RegisterSettingsPages`); later pages push inside the sheet. |
| Zones | Radius 8 (7 for calendar and settings) | `WinoZoneView` default radius 10. The iPad Mail columns use 8, like the Mac. |
| Type | SF Pro 13 body, 11 caption, 15 headings | iOS sizes scaled by Dynamic Type: body 17, caption 12, description 13, heading 20, page title 28. |
| Title bar | Traffic lights, unified toolbar | iPad: navigation bar toolbar row with the same items. iPhone: per-stack navigation bars. |
| Hover and swipe | Hover actions on rows, trackpad swipes | iPhone: swipe actions and long-press menus. iPad: swipe actions from the preferences, context menus, hover actions off by default (the pointer can enable them). |
| Density | Three row densities | iPhone fixed at Medium. iPad keeps the density preference. |
| Compose | In the reading pane, "Open in new window" detaches it | iPhone: full-screen sheet with Cancel (Save, Discard). iPad: in the reader column; "Open in new window" moves it to its own window scene. |
| Move | Popover | iPhone: sheet with medium and large detents. iPad: popover anchored to the button. |
| Daily briefing | 360pt inspector pane | iPhone: sheet with medium and large detents. iPad: 400pt overlay on the right. |
| Narrow widths | Sidebar overlay below 1008pt, stacked below 640pt | iPad: three tiled columns at 1008pt and wider, sidebar as an overlay below that, one column in compact width. |
| Keyboard | Menu bar key equivalents | iPad `UIKeyCommand`s: Cmd+1..4, Cmd+N, Cmd+R, Shift+Cmd+R, Shift+Cmd+F, Cmd+E, Delete, Shift+Cmd+L, Shift+Cmd+U, Cmd+F, Cmd+, plus the user's Mail shortcuts. No global hotkeys. |
| Settings pages | Windows groups and order | The shared `SettingsNavigationInfoProvider` order. Hidden: Companion, Backup and restore, Keyboard shortcuts on iPhone, and S/MIME (`IPlatformCapabilities.Smime` is false). |
| Still Mac only | Menu bar, Dock, Companion, menu bar extra, Sparkle, S/MIME | None of these exist on iOS. |

## Mode layouts

| Mode | iPhone | iPad |
| --- | --- | --- |
| Mail | Mailboxes, folder list, reader push, bottom toolbar | Sidebar, list column, reader column with command bar; drag mail onto sidebar folders |
| Calendar | Day, week and month with a collapsible mini-calendar header, agenda list, details pushed, composer as a full-screen sheet, quick event sheet | Week, work week, day and month grids with the calendar sidebar (New event, month picker, calendars), details in the reader column, composer in place, quick event popover |
| Contacts | Letter-indexed list, card pushed, editor as a full-screen sheet | Sidebar filters, list column, card in the reader column, editor as a form sheet |
| To Do | Lists (the shell-owned stack root, like Mailboxes), then tasks, then the detail pushed; quick add bar above the keyboard | Lists in the sidebar, task list, detail drawer in the reader column |

## Calendar

| | iPhone | iPad |
| --- | --- | --- |
| Surface | Day, Week and Month (Work week shows as Week); a header zone with the collapsible month picker and the period row; an agenda list toggle | Day, Week, Work week and Month in the content column, which the pad shell widens in Calendar mode so the reader column keeps 340pt for the details |
| Navigation | Range title, Today, New event and the calendars filter in the bar; swipe pages the period | Previous, next and Today in the toolbar row. Wide column: range title, period control and New event in the row. Narrow column (under 640pt, 13-inch portrait): the range and period control move into the zone's header, New event stays in the sidebar |
| Sidebar | None (the calendars filter is a sheet) | The sidebar's content slot shows New event, the month picker and the calendars with colour toggles, above the shell's mode switcher |
| Details | Pushed on the Calendar stack | The reader column; its idle state shows the calendar glyph |
| Composer | A full-screen sheet | In place over the surface |
| Quick event | Medium-detent sheet from a long press on empty time | Popover at the tapped or dragged slot |

The composer is a rendering-frame route. The calendar page attaches itself as the rendering-frame host (`IRenderingFrameHost`, the Mac `AttachRenderingHost`) and takes the composer; it owns the composer and releases it when it closes. Event details fall through to the shell's frame. The surface opens scrolled to the work-day start from the shared calendar settings (8:00 when working hours are off), draws the all-day strip on the zone surface with a hairline, and gives narrow tiles their whole width for a wrapped title.

## Project map

| Project | Target | Role |
| --- | --- | --- |
| `src/Wino.Platform.Apple` | `net10.0-macos;net10.0-ios` | Foundation-level services shared by both Apple apps: paths, configuration, Keychain store and secret protector, credential and token stores, App Store client, notification builder, categories and response handler, sounds, state persistence, reader runtime |
| `src/Wino.Core.Apple.Bindings` | `net10.0-macos;net10.0-ios` | StoreKit 2 Swift bridge for both; Sparkle for the macOS target only ([README](../src/Wino.Core.Apple.Bindings/README.md)) |
| `src/Wino.Platform.iOS` | `net10.0-ios` | UIKit-only services: clipboard, external launcher, attachments, rating, MSAL host, Google custom-scheme receiver, badge, notification image and sound hooks, and unavailable stubs (startup, presence, Shift key, S/MIME) |
| `src/Wino.Mail.UIKit` | `net10.0-ios` | Idiom-neutral infrastructure (dispatcher, navigation service over `NavigationCoordinator`, page registry, dialog service, theme service, presenters, Debug bridge) and shared pages (onboarding, reader, compose, idle, Settings, Wino Account sheets, briefing, What's New, Contacts, To Do) |
| `src/Wino.Mail.UIKit.Phone` | `net10.0-ios` | iPhone shell, tab bar, Mailboxes, phone list pages and registries |
| `src/Wino.Mail.UIKit.Pad` | `net10.0-ios` | iPad shell, split view, toolbar, sidebar, key commands, pop-out scenes, pad list pages and registries |
| `src/Wino.Mail.iOS` | `net10.0-ios` (Exe) | App head: `AppDelegate`, `SceneDelegate`, `Composition.cs`, lifecycle hooks, `Info.plist`, `Entitlements.plist`, asset catalog, bundle resources |
| `controls/Wino.Presentation.Core` | `net10.0` | Typed bindings, design tokens, `WinoThemeSurface`, control rules that use Domain types |
| `controls/Wino.Presentation.UIKit` | `net10.0-ios` | UIKit twin of `Wino.Presentation.AppKit`: `WinoLayout`, `WinoIcons`, `WinoStyle`, zones, `ThemeBackdropView`, `WinoBarBackdropView` |
| `controls/Wino.Mail.Controls.UIKit` | `net10.0-ios` | UIKit twin of `Wino.Mail.Controls.AppKit`: mail, shell, contact and task cells, avatars, pills, chips, info bar, settings cards and binder, mode switcher, mini-calendar, calendar surface, briefing card |
| `controls/Wino.Editor.WebKit` | `net10.0-macos;net10.0-ios` | WKWebView reader and editor sessions for both Apple heads (renamed from `Wino.Editor.AppKit`) |

`Wino.Platform.*` never references presentation projects. Logic both UIKit and AppKit need goes to `controls/Wino.Mail.Controls.Core` (pure math) or `Wino.Presentation.Core/Controls` (rules that use Domain types).

## Implementation status (13 October 2026)

Everything below runs in the iOS 27.0 Simulator (iPhone 18 Pro, iPad Pro 11-inch and 13-inch, M5) with Debug-seeded local data. Nothing has run on a device, and no real IMAP, Outlook or Gmail account has been used. Manual interaction (touch gestures, keyboard, VoiceOver) is pending for every area.

| Area | Status |
| --- | --- |
| Shared prerequisites | Done: shared ViewModel registration, `NavigationCoordinator`, notification responses, activation router, lifecycle coordinator, shared presentation rules, Google receiver seam, `SynchronizeOnceAsync`, `Wino.Presentation.Core`. |
| Apple shared layer | Done: `Wino.Platform.Apple`, `Wino.Core.Apple.Bindings` with a three-slice StoreKit 2 xcframework. The Mac app builds unchanged. |
| Head and shells | Both idioms launch to Welcome. Tab bar, split view, toolbar row, sidebar and mode card render. |
| Mail | Mailboxes, folder list, reader with toolbar or command bar, compose, Move, search and filters, edit mode, thread expansion, briefing, What's New, EML sheet, iPad pop-out composer. |
| Settings | The root list, ten pages and the Wino Account, alias, category, signature, reorder, certificate and purchase sheets render on both idioms (no account needed). |
| Contacts and To Do | List, card, editor (cancel and save); lists, tasks and detail on both idioms. |
| Calendar | Merged with the other modes: day, week, work week (iPad), month, agenda (iPhone), event details, the event composer, the quick event and the calendars filter run on both idioms. |
| Open | Onboarding and sign-in against real servers; the Google iOS OAuth client; device signing; background refresh and notifications on a device; Dark mode, Dynamic Type, rotation, Split View and Stage Manager, VoiceOver; pop-out reader; iPhone New Message with a real account. |

The shell hooks the modes rely on are in place: the rendering-frame host attach (`IRenderingFrameHost`, `UIKitNavigationService.AttachRenderingHost`), close-on-back for single-page sheets, the iPad sidebar content slot, the per-mode column policy and idle glyph, the narrow toolbar rule, and the iPhone mode menu root. Remaining wording gaps: the What's New notes are written for Windows (system tray, Ctrl+Shift+Space).

## Verification

A compiled view does not establish parity. For each canvas board, manual acceptance on iPhone and iPad simulators compares layout, row content, command order and selected states against Windows and the Mac, then checks light and dark appearance, Dynamic Type, rotation, Split View and Stage Manager, the hardware keyboard and VoiceOver. A signed device build checks Keychain, sign-in, notifications and background refresh. See [iOS development](harness/development-ios.md#verification).
