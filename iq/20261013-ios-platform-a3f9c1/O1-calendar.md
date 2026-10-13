# O1: Calendar mode on iPhone and iPad (T17, calendar part)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-aaf20085fad733334` (branch `worktree-agent-aaf20085fad733334`, HEAD 80ca7f38), seeded with `seed-after-KLM1.patch` (staged by `git apply --3way`). My work is unstaged/untracked. Nothing is committed.
**Mac mirror:** `~/Desktop/wt-O1`; helper `~/Desktop/wt-O1-tools/run.sh phone|pad launch|cmd|shot`. Simulators (iOS 27.0): "O1 iPhone 18 Pro" `87257FE1-F674-42DE-8B26-1FFA1CEA75C4`, "O1 iPad Pro 13" (M5) `455D529E-C646-48CB-802C-D6AE304730B5`.
Two sub-agents ported the event details page and the event composer in the same worktree; their results are merged here.

**Result:** the iOS head builds with 0 errors (`O1-ios-debug-sim.log`, `O1-build11.log`). Both idioms run Calendar end to end against a debug-seeded local calendar: week/day/month, agenda, event details, composer, quick event saved through the shared ViewModel, context menu, calendar filter, search, mode switch away and back.

## Seed fix (outside scope, needed to build)

`seed-after-KLM1.patch` lacks K's `src/Wino.Mail.UIKit.Phone/Mail/Debug/PhoneMailDebugHook.cs` and `ReaderStandInViewController.cs`: the root `.gitignore` rule `[Dd]ebug/` drops that folder from the patch, so the phone project did not compile (3 errors, `O1-baseline-build.log`). I copied both files from `~/Desktop/wt-K`. They are still git-ignored, so **any patch made from a tree with them misses them again**. Fix: rename the folder or force-add.

## Files

**Shared (`src/Wino.Mail.UIKit/Views/Calendar/`, namespace `Wino.Mail.UIKit.Views.Calendar`)**

| File | Contents |
| --- | --- |
| `CalendarPageViewControllerBase.cs` (+ `.Debug.cs`) | What both idioms share (Mac `CalendarPageViewController` minus layout): surface wiring, bindings, readiness, selection → details route, drag move/resize, drag refusal messages, context menu (`UIEditMenuInteraction`), quick event, Mac debug bridge commands (calprev/next/today/type/select/info/new/new-notes, cal-readonly-info, cal-details*, cal-attach-save, cal-range, calslot) plus `calquick NAME`, `calmenu N`, `caldetails-close`. |
| `CalendarTileMapper.cs`, `CalendarContextMenuLabels.cs` | UIKit twins of the Mac mapper and menu labels; `CreateMenu` builds the shared context menu as a `UIMenu`. |
| `QuickEventViewController.cs` | Mac `QuickEventPopover`: popover (iPad) or medium-detent sheet (iPhone). |
| `CalendarListView.cs` | Accounts and calendars with colour check toggles over `IAccountCalendarStateService` (Mac `ShellCalendarGroupCell`). |
| `CalendarReadinessView.cs` | Blocked state. |
| `CalendarShellHostAdapter.cs` | Host adapter and `CalendarComposerSheet` (see Hooks). |
| `CalendarGlyphs.cs` | Workaround for the `WinoIcons.Image` 3x-size defect (K report item 3); all calendar files use it. |
| `CalendarDebugCommands.cs` | Debug: `seedcalendar [ADDRESS]` (creates a local demo IMAP account if none, turns calendar on, adds "Demo calendar" + read-only "Holidays" and 11 events through `ICalendarService`, opens Calendar); `calcompose-close`. |
| `EventDetailsPageViewController.cs` + `.Actions`, `.Sections`, `.Debug` | Sub-agent port of the Mac details page: one scroll; action card (Join, RSVP, Delete, Edit series, Show as and Reminder menus); RSVP panel; when/location/repeat/calendar rows; people; description via `WebKitHtmlMailReaderSession`; attachments. Zone wrapper only when pushed. Mac debug hooks kept. |
| `CalendarEventComposePageViewController.cs` + `.Attendees`, `.Recurrence`, `.Drop`, `.Debug` | Sub-agent port of the Mac composer. Nav bar Cancel/Save; `CloseRequested` event (Cancel never runs the VM `CancelCommand`, which would GoBack in shell history). One column below 700pt, Mac two columns above. Notes reuse M1's editor web view and format bar. Attendee suggestions, recurrence editor, file drop, Mac debug commands. |
| `UIKitViewRegistration.Calendar.cs` | `RegisterCalendarViews` (details and composer transient; debug hook) and `AddCalendarShellHostAdapter<THost>()`. |

**iPhone (`src/Wino.Mail.UIKit.Phone/`)**
- `Calendar/CalendarPage.cs`: header zone (collapsible `WinoMiniCalendarView`; period row with ‹ Day · Week · Month › and agenda toggle) over the content zone.
- `Calendar/CalendarPageViewController.cs` + `.Agenda.cs` + `.Debug.cs`:
  - Bar: range title, Today, New event, calendars filter, plus the shell avatar.
  - Swipe left/right pages the period. Work week shows as Week.
  - Agenda list grouped by day; row tap opens details; long press shows the shared menu.
  - Debug commands: `calagenda on|off`, `calheader`, `calcalendars`.
- `Calendar/CalendarsSheetViewController.cs`: the filter sheet.
- `PhonePageRegistry.Calendar.cs`, `PhoneViewRegistration.Calendar.cs`.

**iPad (`src/Wino.Mail.UIKit.Pad/`)**
- `Calendar/CalendarPage.cs`: calendar zone with a composer host over the surface.
- `Calendar/CalendarPageViewController.cs`: toolbar items; details clearing; composer in place (`ICalendarComposeHost`).
- `Calendar/CalendarPageViewController.Layout.cs`: column widths and the sidebar adapter.
- `Calendar/CalendarPageViewController.Search.cs`: toolbar search with suggestions (Mac `.Search`).
- `Calendar/CalendarPageViewController.Debug.cs`: `cal-search`, `cal-search-open`, `calcompose-close`.
- `Calendar/PadCalendarSidebarViewController.cs`: New Event, month picker, calendars list, mode switcher card.
- `PadPageRegistry.Calendar.cs`, `PadViewRegistration.Calendar.cs`.

**Edits to existing files (only these):**
- `partial` keyword on `PhonePageRegistry`, `PadPageRegistry`, `PhoneViewRegistration`, `PadViewRegistration`.
  - `UIKitViewRegistration` already had it.
- In each of those five main files: one partial method declaration and one call (`RegisterCalendarPages()` / `RegisterCalendarViews(services)`).
- `en_US/resources.json`: 3 new keys `IOS_Calendar_Calendars`, `IOS_Calendar_Agenda`, `IOS_Calendar_NoEvents`. No Mac or Windows key covered them. Other locales are untouched.
- `EventDetailsPageViewController.Actions.cs` and `CalendarContextMenuLabels.cs`: `UIMenuOptions.None` / `UIMenuElementAttributes.None` do not exist in the binding. They now use `(…)0`.

## Registry entries

| Page | iPhone | iPad |
| --- | --- | --- |
| `CalendarPage` | `ShellContent` (Calendar tab root) | `ShellContent` (content column) |
| `EventDetailsPage` | `Push` (Calendar stack) | `RenderingFrame` (reader column = right details zone) |
| `CalendarEventComposePage` | `RenderingFrame` → adapter → full-screen sheet | `RenderingFrame` → adapter → in place over the surface |

## Hooks needed from the shells (gaps) and the local adapters used

1. **No rendering-host attach in `UIKitNavigationService`** (the Mac has `AttachRenderingHost`/`IRenderingFrameHost`).
   - Without it the composer would land in the reader column or be pushed, and its VM Cancel (`GoBack`) pops shell history.
   - Adapter: `CalendarShellHostAdapter` decorates the idiom's `IUIKitShellHost`. `AddCalendarShellHostAdapter<PhoneShellHost|PadShellHost>()` re-registers `IUIKitShellHost` (last registration wins).
   - It forwards everything except a `CalendarEventComposePageViewController` in `ShowInRenderingFrame`, which goes to the content page if it is an `ICalendarComposeHost` (iPad), else into a full-screen sheet.
   - Closing calls `UIKitNavigationService.DetachRenderingPage` and releases the page.
   - **Integration (N):** prefer a proper attach API in the nav service, then delete the adapter.
2. **Pad sidebar has no per-mode content slot.** It renders the calendar menu as plain rows: the date picker row shows `ToString()` and there are no colour toggles.
   - Adapter: the pad calendar page pushes `PadCalendarSidebarViewController` onto the sidebar column's navigation stack and restores it on release.
   - Verified: switching to Mail restores the shell sidebar (`O1-ipad-10-back-to-mail.png`).
3. **Pad column widths are fixed for Mail (360 list column).**
   - Adapter: the page sets min = max = preferred supplementary width to (split width − tiled sidebar − 340), so the reader column becomes the ~340pt details zone, and restores the shell values on release. Preferred alone was ignored by UIKit.
   - A shell hook ("content spans list + reader minus details width" per mode) would be cleaner.
4. **Reader column idle state** shows the Mail glyph in Calendar mode. This is shell-owned; it needs a per-mode idle glyph.
5. **Toolbar room on 13" portrait** (content ≈430pt):
   - The page switches to a compact row: ‹ › Today, plus a period pull-down titled with the current type. The range title and New event are dropped there; New event stays in the sidebar.
   - UIKit still folds part of the row (the shell's sidebar toggle/sync and the period pull-down) into the "…" overflow. The wide (landscape) row is untested.
   - A shell decision is needed on what the calendar toolbar row keeps at narrow widths.

## Verification (logs and screens in `artifacts/ios-platform/O1-*`, copied to `D:\Wino-Mail\artifacts\ios-platform\`)

| Check | Result |
| --- | --- |
| `Wino.Mail.iOS` Debug `-r iossimulator-arm64 -p:WinoTargetPlatform=iOS` | **0 errors**; the 8 warnings are the existing IL2104 set (`O1-ios-debug-sim.log`, final `O1-build11.log`). No CS86xx in calendar files. The UIKit/Phone/Pad projects are `Nullable=enable`. |
| iPhone: seed + Calendar | `O1-iphone-02-week.png`, `-03-day`, `-04-agenda`, `-05-month`, `-06-header-expanded` |
| iPhone: details push, back, re-tap | `-07-details`. Bridge: pop, then tapping the same item again pushes details (`calselect` after pop). |
| iPhone: composer full-screen sheet | `-08-compose`; `calcompose-close` dismissed it and released the page. |
| iPhone: quick event sheet + save | `-09-quick`; `calquick` saved (items 9 → 10). |
| iPhone: calendars filter sheet | `-10-calendars` |
| iPhone: context menu | `calmenu 1` → "Open \| Show as \| Delete" (`-11-menu`) |
| iPad: week, work week, day, month | `O1-ipad-02-week.png`, `-03-workweek`, `-04-day`, `-05-month`. Calendar sidebar with month picker and calendars. |
| iPad: details in reader column | `-06-details` |
| iPad: composer in place | `-07-compose` (calinfo `composer=True`) |
| iPad: quick event popover + save | `-08-quick`; saved "iPad quick". |
| iPad: edit menu | `-09-menu` |
| iPad: search | `cal-search Design` → 1 result. |
| iPad: back to Mail | `-10-back-to-mail`: shell sidebar and columns restored. |

No managed exceptions appeared in the consoles (`O1-*-console.log`).

**Not exercised (simulator automation cannot drive these, or out of time):**
- Real touch gestures: tap, double-tap, long-press drag to move/resize, range drag, swipe paging, pull-down menus.
- Saving from the composer (Save → create → ForceReload navigation interplay), attendee suggestions, recurrence editor, drop, attachments.
- Details actions: RSVP, Show as and Reminder saves (debug hooks exist), Delete.
- Dark mode, rotation and landscape (headless rotation is ignored), compact iPad split, Dynamic Type, VoiceOver, hardware keyboard.
- Real Outlook/Gmail/CalDAV calendars.
- Windows Debug/Release gate (only the en_US strings are shared) and the Mac head build: not run.

## Other observations (not mine to fix)

- Surface (H): the all-day strip draws a heavy grey fill in light mode.
- Surface (H): narrow week columns truncate tiles to one letter.
- Surface (H): the iPad grid starts at 5:00 rather than working hours.
- `WinoIcons.Image` size defect (K #3) is still present; `CalendarGlyphs` works around it.
- The composer's "More details" quick-event path and the shell's `NewEventCommand` (calendar picker dialog) were not driven.
