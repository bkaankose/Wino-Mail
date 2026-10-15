# T10a Calendar mode (done, 15 Oct 2026)

Windows CalendarPage is the content source; the M3 boards set the look.

## Files
- **Controls.Android/Calendar:** `WinoCalendarWeekStripView`, `WinoCalendarMonthGridView` (dots or range band), `WinoCalendarSurfaceView` (Day/Week/Work week/Month over the shared layout calculator and geometry, Windows tile appearance, now line). Tokens in `wino_tokens_calendar.xml` (light/night).
- **Head `Views/Calendar/`:** `CalendarPageFragment` (+ `.Agenda`, `.Surface`), `EventDetailsFragment`, `CalendarEventComposeFragment`, `CalendarFormRows`, `CalendarPaneRows`, `CalendarTiles`, `CalendarPageRoutes`.
- **Layouts:** `fragment_calendar` (compact and `layout-w600dp`), `fragment_calendar_form`, `calendar_agenda_row`, `styles_calendar.xml`.
- **Infrastructure (additive):**
  - Route `Renavigate`: the calendar keeps its entry and view when the shell navigates to a new range.
  - `IShellWideContent`: the content spans the columns and the reader pane becomes the 340/300 dp details pane.
  - ShellMenuAdapter hosts the calendar pane rows (date picker, calendar check boxes); toggles keep the drawer open.
  - Registry: the CalendarPage placeholder is replaced by `RegisterCalendarPages()`.
- **Debug:** `DebugCommand --es command seedcalendar`.

## Screens
- **Compact:** 64 dp bar (menu, month title that switches agenda ↔ month, Today or arrows, overflow), week strip plus agenda with day subheaders, month grid with dots and the chosen day's events, New event FAB, pull to refresh. Details are full screen. The editor is a full-screen dialog (Close, filled Save).
- **Medium/expanded:** range title and New event in the bar; period header (‹ › Today, Day · Week · Work week · Month); Windows surface; details pane; calendar pane (mini month, calendars).

## Shared moves (Mac and iOS pointed at them)
- `CalendarPageActivation`: load message from navigation args and search results (iOS base, Pad and Mac search, Mac page).
- `CalendarAgenda`: day grouping, time, duration, day title, dots (iPhone agenda, UIKit tile mapper).
- `CalendarDebugSeed` (`#if DEBUG`; iOS `seedcalendar` uses it).
- Tests: `CalendarAgendaTests` (5).

## Simplified or omitted (no shared support, or kept simple)
- Not built: quick-event popover (an empty slot opens the composer through the shared range), drag move/resize, long-press context menu, calendar search.
- Editor: custom recurrence editor, attendee suggestions.
- Notes: plain text in the editor, HtmlCompat text in details (no WebView).
- "Edit event": the shared layer only edits Show as and Reminder (saved from details).
- Kept: Teams/Meet switch (shared `IsOnlineMeeting`); Join only with a join link.

## Verification
- Android Debug: 0 errors, only NU1902/1903. Mac Debug arm64 and x64, iOS Simulator Debug: 0 errors.
- ViewModels tests: 233/233 (Core and Controls.Core are unchanged).
- Wino_Tablet_API36_B, demo seed. Checked:
  - Tablet: week, month, details pane, pane toggles, New event with picker, MaterialDatePicker, MaterialTimePicker, save to the local calendar.
  - Phone 1080x2400/420: agenda, month, details, Show as saved, back, editor and time picker.
  - Landscape (medium) with details kept across rotation; light and dark recreation.
- Emulator reset and killed.
- Screenshots: `T10a-*.png`.
- Fixed during checks: a UTC date-picker crash, stale details reopening on back, and details empty after recreation.

## Gaps
- Week-strip and month swipes are not exercised by hand.
- Medium landscape: the Month segment scrolls off.
- The time picker's AM/PM uses Material tertiary colours.
- TalkBack and font scale are unchecked.
- Windows handoff: Debug x64 and Release gate.
- `docs/android-design-decisions.md` is not updated (T13).
