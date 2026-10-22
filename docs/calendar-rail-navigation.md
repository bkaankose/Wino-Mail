# Calendar rail navigation

The calendar surface pans over an infinite rail instead of switching whole periods. Day, work
week and week views pan horizontally over day columns; the month view pans vertically over week
rows. The rail snaps to period boundaries, loads events only once it settles, and keeps a bounded
window of events in memory. This note describes the shared model so the macOS and iOS surfaces
behave exactly like the Windows one.

## Shared model (`Wino.Core.Domain`)

| Type | Role |
| --- | --- |
| `CalendarRailSpace` | Maps a display type onto integer **units**: `DateOnly.DayNumber` for Day and Week, work-day indices for WorkWeek, week-row indices for Month. Gives the dates behind a unit, the snap rule per mode, period stepping and the anchor date a snap unit resolves to. |
| `CalendarRailMotion` | Chooses the unit a moving rail settles on: nearest snap with a direction bias for wheel input, at least one period for a flick, clamped glide starts for long jumps, and the shared ease-out curve. |
| `CalendarLoadWindow` | The loaded window around a visible range (`PeriodsEachSide` periods on each side) and the parts of a new window that the current one does not cover. |

A **position** is a fractional unit index: `3.4` means the viewport starts 40% into unit 3.
`VisibleUnitCount` units are visible (1, the work-week day count, 7, or 6 rows).

Snap units per mode:

| Mode | Unit | Snap rule | Period step |
| --- | --- | --- | --- |
| Day | day column | every unit | ±1 |
| Week | day column | first day of week | ±7 |
| WorkWeek | work-day column | first work day | ±day count |
| Month | week row | row containing the 1st of a month | next/previous such row |

`CalendarRailSpace.ToVisibleRange(snapUnit, today)` resolves the same `VisibleDateRange` the view
model produces for the anchor date, so a settled unit and a loaded range always agree.

## Loading (`CalendarPageViewModel`)

- `CalendarItems` is an `ObservableRangeCollection<CalendarItemViewModel>` holding every item in
  `LoadedDateWindow`: the visible period plus two periods on each side.
- A settled period is published first (`CurrentVisibleRange`, `VisibleDateRangeText`), then the
  missing ranges load off the UI thread, the diff runs in the background, and the UI thread applies
  one range removal (items outside the new window) and one range insertion (sorted, at one end of
  the collection when the window shifted; a merge resets once).
- Single edits keep their one-item insert, replace and remove paths.
- The surface stays enabled while a window loads. Queued period requests collapse to the latest.

## Surface contract (every platform)

1. Render units `floor(position) - 1 … ceil(position) + VisibleUnitCount`; recycle units and their
   item views when the window shifts. Items not in `CalendarItems` are simply not drawn.
2. Move the rail with a transform every frame; lay out only when the rendered window changes or
   the collection reports a change that touches a rendered unit.
3. Inputs: wheel along the rail axis (Shift+wheel for the horizontal rail with a vertical wheel),
   drag anywhere except on an event, arrow buttons and keyboard by whole periods, Home for today.
   A press becomes a pan after 6 px of dominant movement along the axis.
4. On settle, raise the anchor date (`GetAnchorDate(unit)`) when it differs from the current
   `VisibleRange`; the page sends a `LoadCalendarMessage` and the view model answers through
   `VisibleRange`. An external `VisibleRange` change (today, date picker) glides to its unit.
5. Dim month cells outside `GetDominantMonth(round(position))`.
6. While the rail moves, announce the snap unit nearest to the viewport whenever it changes
   (`CalendarRailMotion.ChooseSnapUnit(space, position, 0)`); the page shows its title through
   `CalendarPageViewModel.FormatRangeText` before the settle.
7. Handle the wheel on the elements that raise it, inside any scroll container, so a pan marks
   the event handled before the container scrolls (Shift+wheel in the timed views).

The Windows implementation lives in `src/Wino.Mail.WinUI/Controls/Calendar/CalendarPeriodControl*`.

## Platform notes

- macOS: `controls/Wino.Mail.Controls.AppKit/Calendar/WinoCalendarSurfaceView*` (`.Rail`, `.Motion`).
- iOS and iPadOS: `controls/Wino.Mail.Controls.UIKit/Calendar/WinoCalendarSurfaceView*` (`.Rail`, `.Motion`). One
  `UIPanGestureRecognizer` along the rail axis (finger and trackpad scrolling) that begins only when the movement runs
  along the axis; the hour grid's vertical scroll waits for it to fail. Reduce Motion lands without the glide. The iPhone
  agenda list keeps its own horizontal pager.
- Android: `controls/Wino.Mail.Controls.Android/Calendar/WinoCalendarSurfaceView*` (`.Rail`). Day, work week and week rail
  horizontally over day columns as above. The month view rails **horizontally over whole month pages** instead of vertical
  week rows (owner decision, 22 Oct 2026), so a vertical pull stays pull to refresh, the only sync gesture. A month page
  is a unit that snaps one at a time; its anchor is the first of the month. A press becomes a pan after 6 dp of dominant
  horizontal movement; glides run on a `ValueAnimator`, so the animator duration scale applies (0 jumps). Each strip
  records its realized content into a `Picture` once per layout and replays it at the rail offset every frame.

