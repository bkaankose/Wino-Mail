# Wino Mail vNext Improvements

This document summarizes the major improvements on `feature/vNext` compared to `main`, based on the commit history between the current branch and the merge-base with `main`.

## Wino Calendar

Calendar has grown from an early implementation into a much more complete product area on this branch.

### A full Wino Calendar experience

- Added a dedicated Wino Calendar app entry, making calendar a first-class experience instead of a secondary add-on.
- Built out the calendar rendering experience with multiple rounds of rendering improvements, updated calendar view styling, calendar buttons, and better event visuals.
- Added event creation and full event compose flows, including follow-up improvements for attachments, attendees, recurrence summaries, RSVP actions, reminders, and event details.
- Improved support for all-day events, better display dates, occurrence handling, and mail-to-calendar mapping so calendar actions connect more naturally with messages and invitations.

### Local calendar support

- Added local calendar operation coverage and supporting behavior for IMAP-backed/local calendar scenarios.
- Prevented duplicate operations by ignoring local calendar apply-changes in the wrong paths.
- Added busy-state support and metadata fetch flows so newly created accounts can initialize calendar data more reliably.

### CalDAV sync

- Introduced a dedicated CalDAV synchronizer and supporting service/client work.
- Fixed CalDAV delta sync issues.
- Fixed CalDAV timezone issues.
- Added manual live CalDAV workflow tests to validate real-world sync behavior.

This means local and self-hosted calendar scenarios are much better represented on this branch than on `main`.

### API calendar sync for Outlook and Gmail

- Expanded Outlook calendar sync behavior, including broader sync windows and fixes around date/time handling.
- Improved Gmail drafting and mail/calendar integration so event-related actions work better across providers.
- Added mail and calendar synchronizer state tracking to make sync progress and error handling more reliable.
- Added auto calendar sync on account creation and broader auto-sync trigger and cancellation support.

### Calendar polish and reliability

- Fixed calendar crashes and null-handling issues in calendar view date range updates.
- Fixed double initialization in calendar day views.
- Improved reaction to calendar changes and calendar item update-source handling.
- Added reminder snooze support across toast UI, services, and database storage.

Overall, Wino Calendar is one of the biggest themes of this branch: richer UI, more complete event workflows, and real sync support across local, CalDAV, Outlook, and Gmail-backed scenarios.

## Wino Accounts

Wino Accounts was significantly expanded and polished on this branch.

### Account flows and identity

- Added sign in, sign out, and registration flows.
- Redesigned login and registration dialogs.
- Added privacy policy presentation during registration.
- Added forgot password and email confirmation flows.
- Pointed the app to the real API and improved profile caching.

### Account management and settings

- Added Wino account settings and a dedicated management page.
- Added a special navigation item for Wino Accounts.
- Added import functionality for Wino Accounts.
- Added a preference to hide the title bar Wino account button.
- Improved the top-shell account icon and signed-out identity visuals.

### Purchases and add-ons

- Added handling for Paddle purchases and add-ons.
- Added purchase-success deep linking.
- Added support for AI pack handling through the Microsoft Store.

### User-facing polish

- Redesigned the Wino Account flyout and menu with a more polished Fluent-style presentation.
- Improved account cleanup behavior when an account is deleted.
- Added account attention handling and better account details/settings behavior.

Compared to `main`, this branch turns Wino Accounts into a much more complete platform feature rather than a minimal sign-in surface.

## Improved Stability and Reliability

A large part of this branch is about making the app more dependable in everyday use.

### Synchronization stability

- Refactored synchronizers to address long-standing reliability issues.
- Improved thread mapping across synchronizers.
- Added generic 404 handling for synchronizers.
- Added specific Outlook 429 handling for rate-limit scenarios.
- Improved Outlook authentication and Outlook sync reliability.
- Improved Gmail synchronizer behavior.
- Added explicit mail and calendar synchronizer state support.

### Mail and data reliability

- Optimized mail fetching with batched database queries and in-memory caching.
- Added SQLite indexes and enabled foreign key enforcement.
- Switched away from the old mail item queue approach and returned to a simpler initial sync strategy.
- Improved local draft resend behavior and added grace-period handling for local drafts.
- Added better handling for large Outlook attachments via upload sessions.
- Fixed issues with sent/draft placement, loading mails with infinite scroll, selection cleanup, and deleted-object scenarios.

### UI and lifecycle stability

- Fixed mail rendering page disposal issues.
- Fixed WebView2 runtime toast dispatching on the UI thread.
- Fixed startup mode issues, single-instancing problems, and shell/navigation regressions.
- Fixed multiple thread selection, container, flicker, and context-menu issues.
- Fixed crashes and null-reference style issues in several calendar and shell flows.

### Engineering quality

- Added more tests across calendar, CalDAV, IMAP, view-model, sanitization, and account sync scenarios.
- Added a GitHub Actions workflow to build WinUI and run Core tests on pull requests.
- Resolved warnings and moved the WinUI project toward warnings-as-errors discipline.
- Added AOT compatibility work and related cleanup across the app.

The branch is not just adding features; it is also clearly reducing failure points throughout sync, rendering, navigation, and storage.

## Contacts, Settings, and General UX

This branch also improves the everyday product experience outside mail and calendar core flows.

### Contacts

- Added contacts management.
- Improved contacts UI and related thread/image preview behavior.
- Removed legacy SQLite base64 contact storage from `AccountContact`.
- Added contact picture handling support and supporting contact service improvements.

### Settings

- Added a dedicated settings shell and refactored settings home/navigation.
- Expanded settings UI and introduced new setting options.
- Added calendar settings into the settings experience.
- Improved account details/settings pages and storage settings navigation.
- Refined settings visuals, shell integration, and menu behavior.

### Onboarding and app experience

- Added a new startup window and a more guided onboarding flow with wizard-like steps.
- Added a "What's New" implementation for feature communication.
- Improved dialogs, title bar behavior, shell content, navigation, and shell polish across multiple iterations.
- Added live store update notifications.
- Improved keyboard shortcuts and related dialogs.
- Added tray icon support and better toast routing between mail and calendar app entries.

## Summary

Compared to `main`, `feature/vNext` delivers four major leaps:

1. Wino Calendar becomes a substantially more complete feature set, including local calendar support, CalDAV sync, and stronger Outlook and Gmail calendar integration.
2. Wino Accounts becomes a real product surface with better authentication flows, management, imports, purchases, and polish.
3. The app is more stable thanks to synchronization refactors, storage improvements, test expansion, and many crash and lifecycle fixes.
4. Contacts, settings, onboarding, and shell/navigation experience all feel more mature and more consistent.

In short, this branch is a broad product maturation release rather than a narrow feature drop.
