# Wino Mail vNext Release Notes (vs. `main`)

This document summarizes the areas that were merged and improved on `feature/vNext` relative to `main`, based on commit history.

## Major merged/improved areas

### 1) Calendar management and scheduling experience
Calendar support was significantly expanded from foundational UI work to full account-integrated flows. The cycle includes calendar/mail mapping, richer calendar visuals, event details improvements, recurring occurrence summary work, and dedicated event compose/create flows. Calendar reliability also improved through delta-sync fixes, timezone correctness updates, duplicate-operation prevention, and better synchronization state handling across providers. Reminder handling became much more complete with snooze support spanning toast UX, service logic, and database persistence. Overall, calendar now behaves like a first-class product surface rather than an auxiliary feature.

### 2) Contact management and people-centric UX
Contacts moved from incremental UI adjustments to robust management features across account and settings surfaces. The branch includes explicit contact-management commits, contact/settings integration updates, and data-model cleanup that removes legacy base64 contact storage patterns. Visual quality improvements (such as profile-image initials behavior and image preview controls) tightened the identity and people experience. In practice, this reduces friction when browsing and maintaining contact data and makes account-related contact operations more predictable.

### 3) Synchronization architecture, correctness, and resilience
Synchronization paths were deeply refactored to address long-standing reliability issues. The branch adds generic error handling (including 404 and Outlook 429 handling), improves thread mapping across synchronizers, introduces explicit mail/calendar synchronizer state, and hardens CalDAV/IMAP behaviors with targeted fixes. Operation safety improved via better busy-state handling, duplicate-operation avoidance, and execution-error handling in rendering/operation pipelines. These changes collectively reduce failure surfaces and improve consistency under real-world server and network conditions.

### 4) Compose/editor, rendering, and draft pipeline improvements
Message composition and rendering received notable architectural cleanup. Work includes editor and toolbar refactors, message-based compose/render simplifications, Gmail drafting, and large Outlook attachment support through upload sessions. Local-draft behavior was refined with resend logic and grace-period protections, while MIME/header and template work improved message fidelity. Together, these changes make authoring and sending mail more stable for both common and heavy payload workflows.

### 5) Performance, data integrity, and test/build quality
The cycle introduced meaningful backend and tooling quality improvements: batch DB query mail fetching with in-memory caching, SQLite index additions, and foreign-key enforcement. Collection/thread update performance was optimized, and multiple targeted tests were added for calendar, IMAP, CalDAV, sanitization, and view-model behavior. CI support was improved via a dedicated PR workflow for WinUI/Core tests, and the WinUI project moved toward stricter warning discipline (`warnings as errors`). This area improves both runtime responsiveness and development confidence.

### 6) WinUI shell, settings, onboarding, and notification polish
The WinUI shell and app flow were modernized with startup window/onboarding wizard work, settings page refreshes, dialog/title bar improvements, and navigation fixes. Notification behavior was refined with app-entry routing for mail/calendar toasts, redundant-target cleanup, runtime toast dispatch fixes, and tailored image corrections. Additional quality-of-life updates (keyboard shortcuts, global mouse back listener, storage/settings navigability, and startup mode fixes) make daily use more polished and predictable.

---

## Small changes (commit-by-commit, one-line summary)

- **44be3eb** — Final settings UI tweaks polished spacing/behavior for the latest shell experience.
- **3e73196** — Removed edit-account-details page to simplify account maintenance flows.
- **8548257** — Corrected update-notes behavior/content for clearer release messaging.
- **d9da326** — Renamed the database artifact to align naming with updated app data conventions.
- **d43e2b2** — Fixed tailored notification image handling so visuals render reliably.
- **9d94bad** — Fixed storage page navigation so users can reach storage settings consistently.
- **e4a224b** — Added/updated email templates to improve default composition output.
- **15400d4** — Improved keyboard shortcuts for faster power-user navigation and commands.
- **c1568d3** — Added live store update notifications to surface app-update availability.
- **a8f9b2d** — Delivered broad calendar quality improvements across UX and behavior.
- **1da3408** — Refactored HTML editor toolbar for cleaner structure and easier extensibility.
- **ebc35c3** — Added event creation capabilities to the calendar workflow.
- **d1f8163** — Refactored web editor internals and improved calendar occurrence summaries.
- **09f1cee** — Removed sqlite base64 contact storage from `AccountContact` to modernize data handling.
- **8e8b123** — Updated NuGet dependencies for compatibility, fixes, and maintenance.
- **9ec7b32** — Merged `feature/EventCompose` into `feature/vNext` to unify event compose work.
- **e94cce4** — Implemented main event compose functionality for calendar authoring.
- **6608bae** — Added initial scaffolding for event composition.
- **5904272** — Added specific handling for Outlook 429 responses to improve throttling resilience.
- **e1be644** — Delivered contact/settings integration updates for smoother account configuration.
- **51f6446** — Updated core title bar to reflect new menu-item structure.
- **24f7c26** — Refreshed dialog visuals for a more modern WinUI look.
- **1aaf4e8** — Expanded settings UI foundation for broader configuration coverage.
- **3d67637** — Consolidated intermediate branch work through merge integration.
- **aaa6e8a** — Removed migrations and introduced wizard-like onboarding steps.
- **db5ecd6** — Added a new startup window to improve initial app entry flow.
- **d45d3fa** — Implemented “What’s New” surface for post-update feature communication.
- **5b3739c** — Added snooze support for calendar reminders across UI/service/database layers.
- **e816e87** — Added/expanded contact management capabilities.
- **bdd3278** — Reworked folder structure organization for maintainability.
- **f35a433** — Fixed profile-image transparency edge case causing unwanted initials background.
- **2c9351f** — Fixed edge cases in `IsBusy` handling to prevent inconsistent UI state.
- **211faff** — Improved bulk mail operations by using property-change-driven updates.
- **11158fe** — Removed redundant notification target configuration.
- **76e3b72** — Fixed issues around mode switching and notification behavior.
- **2040d4a** — Optimized mail fetch pipeline with batched DB queries and memory caching.
- **0e742c7** — Resolved warnings and enforced warning-as-error discipline in WinUI.
- **d2fce5e** — Added PR GitHub Actions workflow for WinUI build + Core test validation.
- **5c510fd** — Removed single-entry/mode-launch behavior tied to Ctrl key startup.
- **e1ce856** — Fixed additional startup mode issues for more predictable launches.
- **4b22608** — Fixed badge creation behavior so badges are consistently generated for Wino Mail.
- **3a39266** — Simplified compose/rendering logic using clearer message-driven flows.
- **5d46ea7** — Routed mail/calendar toasts to correct app entries.
- **d51f4a7** — Added SQLite indexes and enforced foreign keys for performance/integrity.
- **79a8171** — Improved thread mapping logic across all synchronizer implementations.
- **c5a631d** — Added grace-period logic for local drafts to reduce accidental loss/conflicts.
- **33672ab** — Added local draft resend behavior and default app mode settings.
- **311b3c7** — Added dedicated Wino Calendar app entry point.
- **17ca32c** — Enabled large Outlook attachment sending via upload sessions.
- **9d3f0bd** — Added manual live coverage tests for `ImapSynchronizer`.
- **7f198ba** — Implemented explicit synchronizer state for mail and calendar items.
- **a912ada** — Fixed messaging issues tied to calendar add/delete operations.
- **317113a** — Fixed CalDAV timezone handling issues.
- **564cb0b** — Fixed double-initialization issue in calendar day views.
- **ab0810f** — Fixed CalDAV delta sync behavior.
- **7a13ae0** — Added manual live CalDAV workflow tests.
- **c8e1678** — Fixed `HtmlPreviewVisitor` regressions and added sanitization tests.
- **f49d276** — Added focused ViewModel tests for `WinoMailCollection`.
- **05112d6** — Ensured WebView2 runtime toast notifications are dispatched on UI thread.
- **fec49ce** — Improved UI-side cleanup when deleting an account.
- **31a7fae** — Added operation-execution error handling in rendering page flow.
- **dae7d04** — Added calendar metadata fetch after account creation.
- **d428a6c** — Ignored local calendar apply-changes in specific paths to prevent duplicates.
- **ff25db3** — Added busy-state support to calendar item view models.
- **2baa87d** — Added IMAP local calendar operation tests with in-memory DB.
- **42e5157** — Landed broad calendar implementation work across multiple components.
- **acf0f64** — Added CalDAV synchronizer and new IMAP setup/edit page.
- **64b9bfc** — Added flag changes to support UID-based IMAP synchronization.
- **744145b** — Refactored IMAP synchronization internals for stability.
- **4a0dcd2** — Removed obsolete project files.
- **92df726** — Batched flip-view date-range updates for programmatic calendar navigation.
- **dbd5812** — Fixed null handling in `WinoCalendarView` date-range updates.
- **884f000** — Added additional calendar feature plumbing and behaviors.
- **e936c43** — Improved search behavior and relevance/UX.
- **b01fa4e** — Improved event details page and calendar item update source handling.
- **96dcdc8** — Added auto-sync triggering and cancellation support.
- **96d2efb** — Removed semantic zoom support to simplify calendar interaction model.
- **37199d8** — Fixed cache bug preventing mail removal and improved drag/drop behavior.
- **52ee5f1** — Added/improved visuals for mail-calendar items and reminders.
- **870a5e2** — Added calendar-to-mail mapping integration.
- **10dd42b** — Fixed thread UI issues for better consistency.
- **0999c71** — Improved contacts UX, thread animations, and image preview controls.
- **e559a79** — Added generic 404 handling for synchronizer operations.
- **1747ed8** — Disabled Sentry logging for synchronizer exceptions.
- **22c6452** — Delivered editor optimizations for better responsiveness.
- **ad9b94d** — Removed INC registrations for list view items to reduce overhead.
- **9f13bcd** — Applied collection-level performance optimizations.
- **5bfa61a** — Added folder create/delete, storage settings, and thread UI adjustments.
- **2cd03d5** — Fixed thread selection issue involving unrealized containers.
- **c7fb648** — Improved thread selection interactions.
- **331b966** — Added synchronizer info panel in shell for visibility/diagnostics.
- **d28de50** — Fixed Outlook attachments, compose-page reuse, and MIME header details.
- **1ec8d5b** — Added Gmail draft support.
- **4374d19** — Improved threading behavior and related interaction logic.
- **071f1c9** — Refactored synchronizers broadly to address chronic reliability issues.
- **d1425ca** — Updated Claude permissions ignore configuration.
- **2fd600d** — Added partial busy-state handling for mark-as-read requests.
- **0eba778** — Added/updated mail update-source tracking.
- **b343152** — Landed exploratory internal experiments that informed later improvements.
- **31097e4** — Added reactions to calendar changes for better real-time UI updates.
- **319b0af** — Added global mouse back-button listener for app navigation.
- **f105c2f** — Added settings/manage-accounts navigation options.
- **7cc201f** — Added `ShowAs` stripe in calendar control template.
- **a23a99c** — Added quick “join online” affordance for meetings.
- **be6b23c** — Made panel usage AOT-safe to improve compatibility.
