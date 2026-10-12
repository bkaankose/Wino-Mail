# Wino Mail for macOS: design decisions

Decided on 7 October 2026. These decisions fix the shell, workflow and control choices for the native AppKit app in `src/Wino.Mail.MacOS` and the AppKit control libraries under `controls/`. Shared ViewModels, services, sync, storage and the HTML reader/editor engine are unchanged by any of them; every row below is a presentation decision.

Design canvas: <https://claude.ai/artifact/JJXoYw35R5XSeWGTkpJAPy> (boards: cover, main window light/dark, reader states, compose, search, briefing, narrow window, four Settings pages, sheets and popovers, onboarding, Calendar/To Do/Contacts layout frames, decision record, component sheet).

Control inventory and the open choice list this document resolves: `appkit-ui-control-catalog.md` (choices A01 to A15).

## Principles

1. **Same place.** Folders on the left, list in the middle, reader on the right. Mode switcher at the sidebar foot. Settings groups, page names and card order are identical to Windows, so nobody asks "where was that setting".
2. **Native control.** `NSSplitViewController`, `NSToolbar` in a unified title bar, `NSOutlineView` sidebar, view-based `NSTableView` rows, `NSSearchField`, `NSTokenField`, sheets and popovers. Wino draws only what AppKit lacks: settings cards, badges, banners, avatars.
3. **Shared backend.** AppKit views bind through explicit C# adapters to the shared ViewModels. Nothing platform-specific leaks into the shared layer.

Pixel parity with Windows is not a goal. Layout parity, naming parity and command-order parity are.

## Shell decisions

| Topic | Windows | macOS | AppKit |
| --- | --- | --- | --- |
| Window | `ShellWindow` with custom `TitleBar` | Full-size content view, unified compact title bar, 1120 x 760 minimum | `NSWindow` + `NSWindowController` |
| Panes | NavigationView pane, list column, rendering frame | Sidebar 260, list 380, reader flexible, inspector 360; widths persisted | `NSSplitViewController` with four items |
| Global commands | Custom title bar: search, sync, briefing, What's New, account | Unified toolbar. Left: sidebar toggle, sync button with folder title and subtitle. Centre: New Message, Archive, Delete, Move, Flag, Mark read, Reply, Reply all, Forward. Right: search field, Daily Briefing, What's New, Wino Account | `NSToolbar` with `sidebarTrackingSeparator` |
| Mode switch | `AppModeFooterSwitcherControl` at the bottom of the nav pane | Segmented control in the sidebar footer, Settings gear beside it, Cmd+1..4, View menu | `NSSegmentedControl` |
| New item | Button at the top of the nav pane | Leading toolbar button whose label follows the mode (New Message, New Event, New Task, New Contact), Cmd+N. Sidebar top holds accounts only | `NSToolbarItem` |
| Sidebar | Account items with nested folders | Accounts as section headers with provider colour; folders with accent-tinted icons and counts; "More folders"; an attention row under an account that needs sign-in | `NSOutlineView` source list |
| Settings | Shell mode with breadcrumbs | Separate window on Cmd+, with a sidebar that mirrors the Windows groups and order: General, Personalization; Mail (Mail preferences, Message list, Read and compose pane, Notifications, Unread badges); App (Keyboard shortcuts, Backup and restore, Companion); Accounts (Wino Account, Wino Intelligence, Manage accounts); About. Back/forward in the toolbar | `NSWindowController` + `NSSplitViewController` |
| Theming | Mica/Acrylic, 20 app themes, custom themes | Native sidebar vibrancy, system accent, system fonts, native selection. The predefined Wino themes stay selectable: a theme sets the accent and an optional backdrop gradient or image behind list and reader only. Appearance follows the system or a Light/Dark override. Custom theme editor deferred | `NSVisualEffectView`, layer-backed backdrop |
| Type | Segoe UI Variable 14/20 body, 12 caption | SF Pro 13 body, 11 caption, 15 headings, 17 to 19 reader subject | System fonts |
| Shape | 4/8 radii, elevation 16 and 28 | System radii: 6 on controls and grouped cards, 10 on popovers; no custom shadows inside the window | Native |

## Mail workflow

| Topic | Decision | AppKit |
| --- | --- | --- |
| Mail rows | Wino row design kept: avatar, account nickname indicator, sender, subject, preview, date, unread dot and accent subject, attachment and category chips, flag, thread disclosure with count. 56/76/88 pt by density. Accent fill with white text when selected | `NSTableRowView` + `NSTableCellView` |
| Hover and swipe | Hover actions (Archive, Delete, Flag, Mark read) overlay the date on the hovered row; trackpad swipe right archives, left deletes | `NSTableView` row actions, `NSTrackingArea` |
| Reader | Subject, intelligence header, sender row with Reply/Reply all/Forward/More, body, attachment strip with Save all | `WKWebView` through `MacOsMailReader` |
| Reader states | No selection (illustration plus keyboard hints), loading (skeleton plus "Downloading message from the server"), multi-selection (count, summary, bulk actions) | Static views |
| Compose | Opens in the reading pane like Windows. "Open in new window" in the composer command row detaches the draft into its own window; closing that window keeps the draft. Send is the only filled button. Recipients are token fields with Cc/Bcc reveal; the editor toolbar stays in-content with Format/Insert/Options tabs | `NSViewController`, second `NSWindow`, `NSTokenField`, `NSSegmentedControl` |
| Search | Toolbar search field. When active, a scope bar appears above the mail list with Local/Online and filter tokens (account, folder, unread, has attachment, flagged); recents and contact suggestions in a popover under the field; results replace the list; Return searches online too | `NSSearchToolbarItem`, `NSPopover` |
| Daily Briefing | Trailing inspector pane toggled from the toolbar, 360 pt, resizable; unseen badge on the toolbar button; privacy line at the foot | `NSSplitViewItem(inspectorWithViewController:)` |
| Narrow window | Below 1008 pt the sidebar collapses to an overlay and the toolbar compacts (search becomes an icon, actions overflow); below 640 pt list and reader stack | `NSSplitViewItem.canCollapse` |
| Dialogs | Simple prompts are alert sheets; forms are window sheets with the Windows field order and consequence-led button labels ("Delete permanently", not "OK"); Move mail is a popover anchored to the toolbar button with an outline view and type-to-filter; keyboard shortcut recording uses Mac key names and conflict warnings | `NSAlert` sheet, `NSWindow` sheet, `NSPopover` |
| Menu bar | File (New Message, New Window), Edit, View (modes, density, sidebar, inspector), Message (every mail action with key equivalents), Window, Help | `NSMenu` built in `AppDelegate` |
| Keyboard | Cmd+N new, Cmd+R reply, Shift+Cmd+R reply all, Shift+Cmd+F forward, Cmd+E archive, Delete trash, Shift+Cmd+L flag, Shift+Cmd+U mark read, Cmd+F search, Cmd+, settings, Cmd+1..4 modes | `NSMenuItem` key equivalents |

## Catalog choices A01 to A15

| ID | Pattern | Selection | Why |
| --- | --- | --- | --- |
| A01 | Settings disclosure | `WinoSettingsCard` / `WinoSettingsExpander` NSViews, grouped flush like System Settings rows with an 8 pt radius on the group | Same card order and copy as Windows; native switches, pop-ups and steppers inside |
| A02 | Rich selection fields | `NSPopUpButton` for fixed choices, `NSComboBox` for editable; rich or searchable lists in an `NSPopover` | Stable item IDs rather than display text; menus get keyboard and VoiceOver for free |
| A03 | Date presentation | `NSDatePicker` (text field style in forms, clock-and-calendar style in the Calendar sidebar) | Locale formatting and keyboard editing come native |
| A04 | Search suggestions | Toolbar `NSSearchField` plus a scope bar above the list; suggestion popover | Local/Online and filter tokens stay visible while results show |
| A05 | Recipient tokens | `NSTokenField` with completion delegate and represented recipient objects | Paste, delimiters, removal and VoiceOver are solved by AppKit; validation stays in the shared layer |
| A06 | Threaded mail list | View-based `NSTableView` over the flattened shared projection | Stable selection during live updates; thread disclosure drawn in the row |
| A07 | Calendar and dual ranges | Custom `NSView` calendar grid; custom two-thumb range control. Deferred features | No native equivalent for event layout or dual sliders |
| A08 | Materials and themes | Native sidebar vibrancy; Wino theme equals accent plus backdrop behind list and reader | Keeps the Mac feel and the Wino identity; custom themes later |
| A09 | Paged content | Controller swap with previous/next buttons; no `NSPageController` | Briefing and onboarding have few pages; simpler lifecycle |
| A10 | Swipe actions | `NSTableView` row actions plus hover buttons plus context menu | Trackpad reveal and commit are native; mouse users get hover actions |
| A11 | Breadcrumbs | Back/forward in the Settings toolbar; `NSStackView` crumbs inside sub-pages only | The Settings window has a sidebar, so top-level crumbs are redundant |
| A12 | Toolbar placement | Unified `NSToolbar` for window commands; in-content editor toolbar in the composer | Window chrome is native; editor formatting stays beside the text |
| A13 | Rich context flyouts | `NSMenu` for commands; `NSPopover` for forms, pickers and previews | Move mail, filters and the account switcher are popovers; everything else is a menu |
| A14 | Dialog presentation | `NSAlert` sheets for prompts; `NSWindow` sheets for forms | Same field order and consequence-led button labels as Windows |
| A15 | Animation fidelity | Native progress indicators and static Wino glyphs first; shimmer through Core Animation later | Reduced-motion safe; fidelity work after parity |

## Reusable AppKit views Wino draws itself

`controls/Wino.Mail.Controls.AppKit` owns these, each with the same C# surface as the Windows control:

- **WinoMailRowView**: states normal, unread, selected, hovered with actions, thread header, expanded thread child.
- **WinoSidebarCellView**: account header, folder with accent icon and count, user folder with its own colour, nested folder, attention row, add account; 28 pt rows, radius 6.
- **WinoSettingsCard / WinoSettingsExpander**: API mirrors Windows (Header, Description, HeaderIcon, Content, ContentAlignment, IsEnabled, Command; IsExpanded and Items). Variants: switch, pop-up, stepper, navigation, expander header, nested, disabled.
- **WinoSynchronizationButton**: `NSButton` plus `NSProgressIndicator`; states idle, syncing with percentage, failed. The title and subtitle sit beside it so the toolbar does not reflow while syncing.
- **WinoInfoBar**: informational, caution, critical, success; shell banners slide in top-right of the content area, page banners sit inline. Never an alert for a status.
- **Search scope bar and chips**: 18 pt chips in rows, 22 pt in bars; value-carrying filter tokens open a pull-down, boolean filters toggle.
- **WinoContactPicture**: 40 in the reader, 32 in rows, 26 for thread children; provider badge; square account icon; the 16-step avatar palette.
- **Intelligence header and briefing cards**: reuse the shared readiness and content contracts.

## Scope of the first pass

Full fidelity: shell, Mail (list, reader, compose, search, briefing, narrow widths), Settings window (General, Message list, Personalization, Manage accounts), onboarding, sheets and popovers.

Layout frames only: Calendar (date picker and calendars in the sidebar, period segmented control in the toolbar, week grid, event details inspector), To Do (lists sidebar, task table, task details inspector), Contacts (filters sidebar, letter-indexed list, contact card).

Out of scope for this pass: custom theme editor, Companion and menu bar extra, notifications, printing, S/MIME, additional mail windows beyond the detached composer.

## Reference patterns to build first

In the order the catalog proposes, now with the choices above applied:

1. Settings card and expander with switch, pop-up and stepper variants, nested groups, disabled and validation states.
2. Shared input and command adapters (stable item selection, number parsing, `CanExecute`, property updates, event cleanup).
3. Dialog and popover host: alert sheet, form sheet, outline destination popover, anchoring, cancellation, Escape and focus restoration.
4. Sidebar and pane layout: outline cells, badges, menus, collapse behaviour, persisted split widths, footer mode switcher.
5. Mail rows and search: long table, thread projection, stable selection, live data, scope bar, suggestions.
6. Recipient token field and editor toolbar with focus returning to the web view.

## Verification

A compiled view does not establish parity. For each board on the canvas, manual acceptance in the lab compares pane proportions, density, action order and selected states against Windows, then checks light and dark appearance, resizing, keyboard traversal, Command/Shift selection, Return/Escape, trackpad gestures and VoiceOver.

## Revision 2: Wino parity (7 October 2026)

The first implementation followed native macOS too closely and lost Wino's character. These rules
replace the earlier shell decisions where they disagree. The design canvas boards marked
"Wino parity" and the component sheet are the reference.

| Area | Rule |
| --- | --- |
| Backdrop | The theme wallpaper or gradient paints the whole window, sidebar included (`ThemeBackdropView`). The sidebar shows it blurred instead of the native sidebar material. |
| Zones | Mail list, reader, calendar, To Do, Contacts and Settings pages sit in rounded Wino zones (`WinoZoneView`): radius 8 (7 for calendar and settings), 1pt stroke, soft shadow, gutters 6 to 8. |
| Icons | Every glyph comes from the WinoIcons fonts (`WinoIcons`, `WinoIconView`), following the Windows icon style preference. SF Symbols are not used for Wino concepts. |
| Shell pane | Same rows as Windows: New item (50), account row (50) with `WinoAccountIconView` (profile picture or provider logo), folder rows with special-folder glyphs, accent unread pills, section headers, linked accounts. Selection is a subtle fill with a 3pt accent pipe. |
| Mode switcher | A card at the bottom of the pane with five icon segments: Mail, Calendar, Contacts, To Do, Settings. The selected segment shows the filled glyph in the accent colour. |
| Title bar | Traffic lights, sidebar toggle, sync, title and subtitle, a centred search field, then Daily briefing, What's New and the Wino account. Mail actions move to the reader command bar, as on Windows. |
| Mail rows | Date right-aligned on the subject line, flag and attachment glyphs right-aligned on the sender line, preview on line 3, no empty space below the content. Date group headers are full-width rounded strips in the theme header colour. |
| Settings | Card controls are pinned to the trailing edge. Header icons are Wino glyphs. |
| Modes | Calendar, To Do and Contacts are built with the Windows layouts (week grid with event details, task list with detail drawer, contact list with detail pane). |
| Still Mac | Traffic lights, menu bar and shortcuts, sheets, popovers, SF Pro text, Settings as its own window (Cmd+,). |

## Implementation status (7 October 2026, evening)

Built after Revision 2 (Mac Debug build, 0 errors; checked with in-app window snapshots only, no manual
interaction yet):

- Foundation: `WinoIcons` (bundled WinoIcons fonts, icon style preference), `WinoIconView`, `WinoZoneView`,
  `WinoAccountIconView`, theme wallpapers and gradients in `MacWinoThemeService`, `ThemeBackdropView` behind
  the whole window, `MacDebugBridge` (Debug-only remote commands and window snapshots).
- Shell: Windows pane rows for all four modes, mode switcher card, Wino-glyph title bar with centred search,
  briefing, What's New and account buttons, mode switching and Cmd+1..4, back confirmation and results.
- Mail: Windows row template and group headers in zones, list header with pivots and filter, reader command bar.
- Settings: cards with trailing controls, backdrop window, group headers, Personalization tiles, Keyboard
  shortcuts and Storage pages.
- Calendar: week, work week, day and month surfaces, event details zone, quick event popover, composer sheet.
- To Do: list with detail drawer, quick add, smart views.
- Contacts: grouped list, detail pane, editor.
- Daily briefing panel, What's New window, Wino account popover.

Not built or still placeholders: drag and drop (mail, calendar, contacts), range selection in the calendar,
settings pages that depend on Windows features (Companion, Backup, Wino Account, Intelligence, Signatures,
Templates, Filters, Folder customization, theme gallery and editor), Wino Account sign-in dialogs, custom
themes, localisation keys for the few Mac-only strings, dev code signing with a stable identity (keychain
prompts on every rebuild), and the shared-layer fixes listed in the infra notes (MIME path separator, swallowed
MSAL errors, reader attention state).
