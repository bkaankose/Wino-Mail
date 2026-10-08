# Calendar reliability review — 2026-09-10

## Changes in this patch

- Calendar menus display global shortcuts, including the configured Delete binding.
- Event cards use nearly square corners and top-left titles, with time and location when height permits.
- Shift + left drag highlights an inclusive cell range and opens quick creation on release. End dates survive midnight and month-range selection.
- Calendar item menus now use `WinoContextFlyout` and its shared presenter. Menu definitions distinguish submenus from executable commands.
- Delete and Join online no longer fail the old optional-parameter guard. Commands capture their event before the flyout closes.
- Busy events and changes to read-only calendars have disabled commands.
- Overlapped events have an explicit `Border`, an opaque background, and a themed outline.
- Outlook event creation requests immutable IDs inside the batch step. Later downloads use the same ID format.
- Outlook downloads can match an older mutable-ID create through `TransactionId`, within the same calendar.
- Remote ID lookup now compares case and underscores literally. SQL `LIKE` previously treated underscores as wildcards and ignored ASCII case.
- Both providers retain their stored checkpoint after an event processor throws. Successful events can repeat safely on the next synchronization.
- Outlook no longer requests unsupported `$select` fields on calendar-view delta queries.
- Cancellation during the Outlook semaphore wait no longer releases a permit that the operation did not acquire.

## Duplicates exposed by moving events

The source contains a reproducible identity mismatch. Creation uses a Graph batch, but the immutable-ID middleware applies to the outer HTTP request.
The inner event request lacked that preference. Later event downloads request immutable IDs and can return a different ID for the same event.

`OutlookChangeProcessor.ManageCalendarEventAsync` previously used only the remote ID to find an existing row. A different ID therefore created another row.
The patch adds the header to event creation and uses the original transaction ID as a fallback for single events and series masters.
Occurrences must retain separate identities, so they do not use that fallback.

The regression test creates three local events with mutable IDs, then downloads each twice with immutable IDs. Exactly three original local IDs remain.
Read-only inspection of the local database confirmed three duplicate pairs. Each pair shares one creation tracking ID but has two provider IDs. Some copies have different start times after moving.
The patch now merges these proven local identities when the server event is processed again. It retains the original local ID, reminders, attachments, and series links. It does not delete a provider event or merge by title or date.
A regression test starts with an existing duplicate pair, downloads a moved event twice, and asserts one updated row with both local reminders.
Unchanged duplicate pairs need a subsequent provider update or full download before reconciliation runs.

Microsoft requires the immutable-ID preference on each request and documents case-sensitive identifiers. See [immutable identifiers](https://learn.microsoft.com/en-us/graph/outlook-immutable-id).

## Remaining findings

### P1: Calendar pagination can delete valid local calendars

Both `SynchronizeCalendarsAsync` implementations fetch one response and compare that response with every local calendar.
They delete local calendars absent from that first page. Neither implementation follows the calendar-list continuation token before deletion.

Evidence: `src/Wino.Core/Synchronizers/OutlookSynchronizer.cs:4367` and `src/Wino.Core/Synchronizers/GmailSynchronizer.cs:1726`.
The next change needs complete pagination before comparison, plus tests for second-page calendars and a failed continuation request.

### P1: Recurring events can lose their series relationship

Outlook sorts delta entries by event type, but calendar-view responses contain occurrences and exceptions without necessarily including their series master.
The processor saves children without a parent and only logs a warning. Series commands then lack the required relationship.

Evidence: `src/Wino.Core/Integration/Processors/OutlookChangeProcessor.cs:98` and `src/Wino.Core/Synchronizers/OutlookSynchronizer.cs:4266`.
The next change needs explicit parent retrieval and persistence before child processing.

Gmail retrieves missing parents, but catches retrieval errors and returns normally. Its processor can then skip the child without throwing.
That path still permits checkpoint advancement despite the exception-based checkpoint guard in this patch.

Evidence: `src/Wino.Core/Synchronizers/GmailSynchronizer.cs:1676` and `src/Wino.Core/Integration/Processors/GmailChangeProcessor.cs:84`.
The next change needs an explicit processing result, including separate handling for deleted series and retryable parent failures.

### P1: Full synchronization does not reconcile stale local events

Gmail clears an expired token and downloads again, but does not compare the complete result with local events.
Events absent from the new snapshot can remain locally. Google requires local-state replacement after an invalid token.
See [Google synchronization guidance](https://developers.google.com/workspace/calendar/api/guides/sync).

Evidence: `src/Wino.Core/Synchronizers/GmailSynchronizer.cs:1595`.
The next change needs a complete staged snapshot and reconciliation that preserves pending local writes and local reminders.

### P2: Outlook synchronization has a fixed date window and incomplete reset behavior

Initial synchronization covers two years before and after its start date. Stored deltas retain that original window as time passes.
The code also has no calendar-specific expired-token recovery. It extracts the token instead of storing the full continuation URL.

Evidence: `src/Wino.Core/Synchronizers/OutlookSynchronizer.cs:4219` and `src/Wino.Core/Synchronizers/OutlookSynchronizer.cs:4326`.
The next change needs stored window boundaries, controlled window expansion, and expired-token snapshot reconciliation.
Microsoft documents opaque continuation URLs and fixed calendar-view ranges in [calendar delta guidance](https://learn.microsoft.com/en-us/graph/delta-query-events).

### P2: Create-response failures can disappear from the user-visible result

Outlook catches errors from local create persistence and attachment upload, logs at Debug level, and returns normally.
The remote event can exist while its local mapping or attachments remain incomplete.

Evidence: `src/Wino.Core/Synchronizers/OutlookSynchronizer.cs:3835`.
The next change needs separate event-creation and attachment outcomes. Retrying attachment failure must not create another remote event.

### P2: Join online opens the event page

Outlook stores `WebLink` as `HtmlLink`. Gmail stores the event's `HtmlLink`.
The Join online command opens this value, which is not necessarily a Teams or Meet conference URL.

Evidence: `src/Wino.Core/Integration/Processors/OutlookChangeProcessor.cs:126`, `src/Wino.Core/Integration/Processors/GmailChangeProcessor.cs:164`, and `src/Wino.Calendar.ViewModels/CalendarPageViewModel.cs:1299`.
The next change needs a separate conference URL or a label that accurately describes opening the provider event page.

## Checks

- Targeted core run: 215 passed, zero failed, two live CalDAV tests skipped.
- Coverage includes create payloads, three-event identity reconciliation, literal remote-ID lookup, calendar commands, layout, and provider response handling.
- XAML Styler: all three changed files pass passive checking.
- Automation-ID audit: passed.
- Debug application build: passed with zero warnings and zero errors.
- WinApp 0.6 deployed the existing manifest identity. Process: `24324`. Window: `1640718`. Theme: Dark.
- Calendar navigation: activated the Calendar item and checked that the calendar toolbar appeared.
- Context menu: opened an existing event, entered Show as, and found `CalendarContextShowAsSingleBusy`.
- Quick creation: saved `Wino calendar QA 20260910` and found exactly one visible title.
- Deletion: invoked `CalendarContextDelete`, waited for the QA title to disappear, requested synchronization, and found zero matching titles afterward.
- The QA event had no attendees. Existing calendar events were not deleted.
- Visual evidence: `artifacts/calendar-review-current.png` and `artifacts/calendar-review-flyout.png` show the overlap outlines and shared presenter.

### Verification after restart

- Current source compiled with zero warnings and errors; 215 targeted tests passed and two live CalDAV tests were skipped.
- Range tests cover forward and backward selection, inclusive cell endpoints, midnight boundaries, and multi-day all-day ranges.
- Existing-duplicate move regression preserves one original local event and its reminders after repeated downloads.
- WinApp 0.6 updated the existing Debug identity and launched PID `37952`, HWND `592320`.
- Light theme: activated Calendar through keyboard focus and Enter; verified top-left card titles and times.
- Opened `CalendarContextDelete` menu without invoking it; inspected its separate `Delete` shortcut label.
- Tapped an empty cell and asserted the quick popup date label plus start/end values of `2:00 PM` and `2:30 PM`.
- Screenshots: `artifacts/calendar-resume-current.png` and `artifacts/calendar-resume-shortcuts.png`.
- No QA event was created after restart; the popup closed before the attempted Save interaction.

The Shift + mouse-drag gesture still needs interactive verification: installed WinApp 0.6 has no modifier-held drag option. Its range calculations and quick-dialog state passed unit tests.
High Contrast, live move reconciliation, Gmail provider faults, token expiry, pagination, and recurrence deletion remain unverified.
