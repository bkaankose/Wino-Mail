# T0 · Android design canvas · missing screens

Canvas: https://claude.ai/artifact/NDKJ6H5EJ8FR8Qi8CqnBTc (live version 1791992982-275c, unchanged).

## Result
All boards were built, but the publish was refused by the platform: the canvas was created from the Design artifact type and this session may not publish to it (`deploy 422: manifest entry "index.html" belongs to the artifact's type … publishing its own files is not available`). Nothing on the canvas changed. The finished files are ready for the owner's session to send:

- Files: `/private/tmp/claude-501/-Users-bkaankose-Desktop-Wino-Mail/89e67e9c-4074-4644-ab38-b7828ce0eb58/scratchpad/canvas/project/` (53 `.dc.html` + `canvas.json`), zipped with the generator at `…/scratchpad/android-canvas-T0.zip`.
- Publish from the owner's session: Artifact publish with `root` = `…/scratchpad/canvas`, `file_path` = `project/Main.dc.html`, `files` = every other `project/*` path (map form). `canvas.json` already carries the new `boards`, `order` and titles.

## Boards added (light, phone 412×915 unless noted)
Onboarding: `Welcome`, `Providers`, `SignInGoogle` (system account chooser over the Step 2 identity page: account name, colour, Mail/Calendar/Contacts/To Do switches, sync range), `SignInOutlook` (partial Custom Tab over the same page), `ImapSetup` (basic), `ImapAdvanced` (servers, security, auth, DAV), `SetupProgress` (AccountCreationDialog steps), `SetupDone` (+ Android 13 notification permission), `OnboardingTablet` (1280×800 centred wizard dialog).
Editors: `EventEditor`, `ContactEditor`, `TaskEditor`; tablet `EventEditorTablet`, `ContactEditorTablet`, `TaskEditorTablet` (detail zone in edit mode). Date/time chips carry MaterialDatePicker/TimePicker hints.
Month: `CalendarMonth`, `CalendarMonthTablet`.
Sheets: `MoveSheet`, `FilterSheet`, `BriefingSheet`, `BriefingTablet` (right side sheet).
`Notifications`: shade with single mail (Archive/Delete/Mark read), grouped per account, calendar reminder (Join/Snooze/Dismiss), sign-in required, silent sync progress.
Dark (`*Dark`, Nighty palette mapped from the light files as InboxDark): Mailboxes, Reader, Compose, Settings, SettingsMessageList, Calendar, Contacts, ToDo, Landscape, Tablet, CalendarTablet, ContactsTablet, ToDoTablet and all 16 new phone boards.

## Sections changed
New title rows: Onboarding, Editors, Month view, Sheets, Notifications, Dark (below the To Do row); one sticky noting the additions. Cover gains six "On this canvas" chips; Decisions "Not on this canvas yet" now lists widgets, theme gallery, IMAP provider catalog, recurrence editor, dark tablet editors/sheets.

## For the owner's review
1. Gmail via the system account chooser (drawn) vs. the Custom Tab only — recommend chooser when Play services exist, Custom Tab fallback.
2. Outlook/Yahoo as a partial-height Custom Tab (Android 13+) over Wino, full-screen on older APIs — recommend as drawn.
3. Welcome copy adapts the Windows string to "…same look on your Windows PC and Mac" — confirm wording.
4. Notification permission asked at the end of setup, not at first launch — recommend as drawn.

References used: Mac parity canvas (Welcome, Providers, ImapServerSetup, Briefing, Dialogs) and iOS canvas (Onboarding, MoveFolder, Briefing, ManageAccounts), read-only. No instruction-like text was found in either artifact.
