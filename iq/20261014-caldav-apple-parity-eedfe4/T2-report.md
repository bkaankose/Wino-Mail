# T2 report: capability adoption in the shared ViewModels, RFC 6638 write path

Status: done on this host (Linux/WSL). Nothing committed, no app launched, work record not edited. No schema, entity, XAML or AppKit change.

## Files

Added
- `src/MailCal.Mail.ViewModels/Calendar/CalendarCapabilityPresenter.cs`: shared wording and fallback.
  - `GetFallback(account, calendar)`: the static provider set used before the service answers. It mirrors `CalendarCapabilityService.ResolveSource`.
  - `HasLowQuota`: true below 5 MB.
  - `GetAttendeeLimitText`, `GetQuotaWarningText`, `BuildSummary` and `FormatStorageSize`.
- `src/MailCal.Services/CalDavUserIdentity.cs`: the user's identity from `calendar-user-address-set`.
  - `Resolve`, `IsUser` (string or Uri), `ResolveOrganizerAddress` and `ExtractEmail`.
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.Scheduling.cs` (partial):
  - `GetUserIdentityAsync`, `ApplyUserIdentityAsync`
  - `TryRespondAsAttendeeAsync`, `PersistOwnResponseAsync`
  - `MapResponseToParticipation`
  - `ResolveImportedResponseStatus`: a helper for T5 (see Unfinished).
- Tests:
  - `tests/MailCal.Core.Tests/Synchronizers/CalDavSchedulingMutatorTests.cs` (14 cases)
  - `tests/MailCal.Core.Tests/Synchronizers/CalDavSchedulingEngineTests.cs` (6 cases, `FakeCalDavServer`; no new routes)
  - `tests/MailCal.Mail.ViewModels.Tests/CalendarCapabilityAdoptionTests.cs` (12 cases)

Changed
- `CalendarEventComposePageViewModel.cs`, `EventDetailsPageViewModel.cs` and `CalendarAccountSettingsPageViewModel.cs`:
  - Each takes a new optional last constructor parameter, `ICalendarCapabilityService calendarCapabilityService = null`.
  - No DI change was needed. T1 registers the service in `ServicesContainerSetup`. Both heads resolve these ViewModels by constructor (`AddTransient`), and MS.DI fills an optional parameter when the service is registered.
- `src/MailCal.Services/CalDavIcsMutator.cs`:
  - new `UpdateEvent(..., CalDavUserIdentity identity)` overload; the old signature delegates to it
  - new `IsOrganizedByOther` and `SetOwnParticipation`
  - new organizer and attendee merge rules
  - one latent bug fixed (see below)
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.cs`, three anchored edits only:
  - `RespondToEventAsync` is now async. It tries the attendee path first.
  - `UpsertEventAsync` gets one inserted line after `EnsureCalendarItemDefaults`: `var identity = await ApplyUserIdentityAsync(...)`.
  - `UpsertEventAsync` also passes `identity` to `CalDavIcsMutator.UpdateEvent`.
- `src/MailCal.Core.Domain/Translations/en_US/resources.json`: 17 keys added right after `CalendarEventCompose_AttachmentsNotSupportedForCalDav`.
- Not changed: `CalendarIcsBuilder.cs`. The ORGANIZER of a new event is fixed by setting `item.OrganizerEmail` in the engine before the builder runs. The item is persisted after the PUT, so the database row matches the server.

## New bindable properties

All of them raise `PropertyChanged` when capabilities load. They are refreshed on attendee changes where that matters.

**`CalendarEventComposePageViewModel`**
- `CalendarCapabilities`: the loaded set. It has a private setter.
  - Calendar selected but not loaded yet: the provider's fixed set (`CalDavDefault` for CalDAV).
  - No calendar selected: `None`.
- `CapabilityLoadTask`: the load for the current selection.
- `CanAddAttachments`: `SupportsAttachments && !IsCalendarReadOnly`. It no longer depends on `IsSelectedCalendarCalDav`, which is kept for compatibility only.
- `AttachmentsDisabledTooltipText`: empty when allowed, the read-only text for a read-only calendar, otherwise `CalendarCapability_AttachmentsNotSupported`.
- `CanAddAttendees`, `SendsInvitations`
- `HasInvitationsNotSentWarning` and `InvitationsNotSentText`: set when attendees exist and the server does not schedule.
- `MaxAttendees` (`int?`), `HasAttendeeLimit`, `AttendeeLimitText`, `IsAttendeeLimitReached`
- `IsCalendarReadOnly`
- `HasQuotaWarning` and `QuotaWarningText`: set when `QuotaAvailableBytes` is below 5 MB.
- `CanAddOnlineMeeting`: now `SupportsOnlineMeeting`. Outlook and Gmail are still true; the rest are still false.
- `SaveButtonText`: Send only when there are attendees and `SendsInvitations`; otherwise Save.

**`EventDetailsPageViewModel`**
- `CalendarCapabilities` and `CapabilityLoadTask`. Loading is keyed by `CurrentEvent.CalendarItem.CalendarId` and reloads only when the calendar changes.
- `IsCalendarReadOnly`: the capability or `AssignedCalendar.IsReadOnly`.
- `CanRespondToInvitation`
- `SendsInvitations`
- `HasResponseNotSentWarning` and `ResponseNotSentText`: CalDAV without `calendar-auto-schedule`.

**`CalendarAccountSettingsPageViewModel`** (from `GetAccountCapabilitiesAsync`)
- `AccountCapabilities`, `HasCapabilities`
- `IsCalendarReadOnly`, `SupportsAttachments`, `SupportsAttendees`, `SendsInvitations`
- `MaxAttendees`, `AttendeeLimitText`
- `QuotaAvailableBytes`, `QuotaAvailableText`, `HasQuotaWarning`, `QuotaWarningText`
- `IsServerReported`
- `CapabilitySummaryText`: one paragraph made of these parts:
  - read-only
  - attachments yes or no
  - invitations sent or not, or no attendees
  - attendee limit
  - storage left
  - "confirmed after the next synchronization" before the first discovery

**Async safety.** Each selection or event change increments a version. A late answer applies only if the version, the selection and the calendar are still the same. The test `Compose_LateAnswerForAnEarlierSelection_IsIgnored` covers this. A `None` answer (service failure) keeps the provider fallback. Attachments are cleared only after the final answer. Clearing on the fallback would drop the user's files when an iCloud calendar is picked.

**Refusals in the ViewModel.** Every refusal is an InfoBar warning titled `CalendarCapability_ActionNotAvailableTitle`, or the existing `ShowReadOnlyCalendarMessage()`:

| Action | Refusal |
| --- | --- |
| Add attachment (`AddAttachmentsCommand`, public `TryAddAttachment`) when unsupported | Message with the tooltip text |
| Add attendee when unsupported (`AddAttendee`, the path both heads call) | Message, or the read-only message |
| Add attendee beyond `MaxAttendees` | `CalendarCapability_AttendeeLimitReached` |
| Create with too many or unsupported attendees | Same messages |
| Create on a read-only calendar | Read-only message |
| Save or delete on a read-only calendar (details page) | Read-only message |
| Respond on a read-only calendar (details page) | Read-only message |

Create, Save, Delete and Respond first await `CapabilityLoadTask`, so they decide on the real capabilities.

## Head binding handoff (no head was edited)

Compose page (WinUI `Views/Calendar/CalendarEventComposePage.xaml(.cs)`; Mac `CalendarEventComposePageViewController*.cs`):
- These existing bindings keep working with the new meaning: `CanAddAttachments`, `AttachmentsDisabledTooltipText`, `SaveButtonText`, `CanAddOnlineMeeting`.
- New:
  - an info bar under the attendee box, `Visibility=HasInvitationsNotSentWarning`, `Text=InvitationsNotSentText`
  - an attendee hint, `Visibility=HasAttendeeLimit`, `Text=AttendeeLimitText`
  - disable attendee entry with `CanAddAttendees`, and optionally with `!IsAttendeeLimitReached`
  - a warning bar, `Visibility=HasQuotaWarning`, `Text=QuotaWarningText`
  - optionally show `IsCalendarReadOnly`
- WinUI uses `x:Bind ... Mode=OneWay`. The Mac uses `Bind(nameof(ViewModel.X), ...)` as it does today.

Event details (WinUI `EventDetailsPage.xaml`; Mac `EventDetailsPageViewController.cs`):
- RSVP buttons: `IsEnabled=CanRespondToInvitation`.
- Save and Delete: `!IsCalendarReadOnly`.
- RSVP panel note: `Visibility=HasResponseNotSentWarning`, `Text=ResponseNotSentText`.

Calendar account settings (WinUI `CalendarAccountSettingsPage.xaml`; Mac `CalendarAccountSettingsPageViewController.cs`):
- Add a "Server features" card: `Visibility=HasCapabilities`, `Text=CapabilitySummaryText`.
- Optional detail rows: `SupportsAttachments`, `SendsInvitations`, `AttendeeLimitText`, `QuotaAvailableText`, and `QuotaWarningText` when `HasQuotaWarning`.

Manual checks pending on each platform:
- An iCloud calendar enables Add attachment once T4 uploads work (see the T4 dependency below).
- On Yahoo, the button reads "Save" with attendees and shows the invitations warning.
- The attendee limit message appears.
- RSVP is disabled on a read-only (shared) calendar.

## New translation keys (en_US only)

`CalendarCapability_` + `ActionNotAvailableTitle`, `AttachmentsNotSupported`, `AttendeesNotSupported`, `AttendeeLimit`, `AttendeeLimitReached`, `InvitationsNotSent`, `ResponseNotSent`, `QuotaLow`, `SummaryReadOnly`, `SummaryAttachmentsSupported`, `SummaryAttachmentsNotSupported`, `SummaryInvitationsSent`, `SummaryInvitationsNotSent`, `SummaryAttendeesNotSupported`, `SummaryAttendeeLimit`, `SummaryQuota`, `SummaryPendingDiscovery`.

`CalendarEventCompose_AttachmentsNotSupportedForCalDav` is no longer read. It is kept so other locales are untouched, and T5 may remove it.

## Write-path design

**Identity (`CalDavUserIdentity.Resolve(state.CalendarUserAddresses, account.Address)`)**
- The ORGANIZER address is chosen in this order:
  1. the `mailto:` entry equal to the account address
  2. the first `mailto:` entry
  3. the account address
- `mailto:null`, principal paths and `urn:uuid:` are never used as ORGANIZER.
- `IsUser` matches, ignoring case, any raw entry of the set (principal path, `urn:uuid:`), any `mailto:` address in it, or the account address.
- The state is read through `ICalDavStateStore.GetAsync`. A failed read falls back to the account address.

**Create.**
- `ApplyUserIdentityAsync` changes `item.OrganizerEmail` to the identity address only when it is already the user (or a user alias).
- An empty organizer stays empty.
- Someone else's organizer is unchanged.

**Organizer edit (`UpdateEvent` with identity).**
- SEQUENCE is still bumped, as before.
- ORGANIZER:
  - The stored ORGANIZER is kept, with its parameters, when it is the same address or another address of the user.
  - The user's identity is written when the user organizes, or when attendees are added to an event that had no organizer.
- ATTENDEE:
  - Lines are rebuilt from the database rows.
  - A line already on the stored event keeps its PARTSTAT and every parameter (RSVP, SCHEDULE-STATUS, CUTYPE). Only ROLE (except CHAIR) and CN follow the row.
  - New attendees get the status of their row, as before.

**Response (`RespondToEventAsync`).**
1. If the stored copy's ORGANIZER is not the user, the attendee path runs. For an occurrence without its own exception, the master's ORGANIZER decides.
2. The attendee path takes the calendar lock and re-reads the snapshot.
3. `SetOwnParticipation` sets the user's own ATTENDEE PARTSTAT to ACCEPTED, TENTATIVE or DECLINED.
   - If the user's line is missing, it adds one with the ORGANIZER identity address.
   - It refreshes DTSTAMP and changes nothing else: no STATUS, no SEQUENCE, no LAST-MODIFIED.
   - Answering the series also answers every exception that lists the user.
   - Answering an occurrence answers its existing exception. If there is none, it adds one: RRULE, RDATE, EXRULE and EXDATE are stripped, RECURRENCE-ID and DTSTART are set to the instance (a DATE for all-day series), DTEND is the master length, and the attendee lines are cloned.
4. The PUT carries `If-Match`. The series snapshot gets the new ETag.
5. The local state is updated:
   - `CalendarItem.Status` becomes the response, as the UI expects.
   - A decline also sets `IsHidden = true`. This keeps today's "decline hides the event".
   - The user's own `CalendarEventAttendee` row gets the PARTSTAT, and is added if missing.
   - A series answer is applied to its occurrence rows too.
   - All of this goes through `ICalendarService.UpdateCalendarItemAsync`.
6. When the user organizes the event, or no stored copy exists, today's behavior stays: STATUS changes and SEQUENCE is bumped.

**Latent bug fixed in the mutator.**
- Ical.Net's `CalendarEvent.Attendees = list` replaces only the first ATTENDEE property. Before this change, `UpdateEvent` on a stored event with two or more ATTENDEE lines wrote the old second-and-later lines a second time.
- Two related problems:
  - `master.Copy<CalendarEvent>()` shares attendee objects with the master.
  - `Attendee.Copy<Attendee>()` added `RSVP=FALSE`.
- Lines are now removed with `Properties.Clear("ATTENDEE")` and added one by one, and clones are fresh `Attendee` objects with copied parameters.

## Verification (this host)

Baseline: the ViewModel tests were run on a clean `HEAD` worktree (`git worktree add --detach <scratchpad>/t2-baseline HEAD`, since removed) and gave **157/157 passed**, with no failures on Linux.

| Command | Result |
| --- | --- |
| `dotnet build tests/MailCal.Core.Tests/... -c Debug -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true` | 0 errors, no warnings in T2 files |
| `dotnet test tests/MailCal.Core.Tests/... --no-build --filter "FullyQualifiedName~CalDav\|...~Imap\|...~CardDav"` | **Passed 341, Failed 0** (239 baseline + T2's 20 + T3/T4 tests in flight) |
| `dotnet test ... --filter "FullyQualifiedName~CalDavScheduling"` | 20/20 |
| Core tests that touch these ViewModels (`CalendarAccountSettings`, `CalendarReadOnly`) | 6/6 |
| `dotnet build tests/MailCal.Mail.ViewModels.Tests/...` | 0 errors, no warnings in T2 files |
| `dotnet test tests/MailCal.Mail.ViewModels.Tests/... --no-build` | **Passed 169, Failed 0** (157 + 12 new) |
| `dotnet build src/MailCal.Mail.ViewModels/... -c Release -p:MailCalTargetPlatform=MacOS -p:NuGetAudit=false` | 0 errors; only the existing NETSDK1188 package-locale warnings |

During the run, T3's and T4's in-progress files briefly broke the shared build. I waited for them and did not edit their files.

New tests cover:
- **Identity:** account-mailto preference, first-mailto fallback, the Yahoo `mailto:null` set, and principal or `urn:uuid` matching.
- **`IsOrganizedByOther`.**
- **`SetOwnParticipation` cases:**
  - master; Tentative and Decline; user line missing
  - series with exceptions; existing exception; new exception for an occurrence
  - mixed-case and alias address matching
  - no SEQUENCE or STATUS change; X- property, ORGANIZER parameters, RSVP and CN kept
- **Organizer edit:** keeps other attendees' PARTSTAT, bumps SEQUENCE, and writes no duplicate lines. A new attendee gets NEEDS-ACTION.
- **Organizer resolution:** identity organizer for an organizer-less event with attendees. When an attendee edits an invitation, ORGANIZER and its parameters are kept.
- **Engine against `FakeCalDavServer`:**
  - Accept: one PUT, STATUS and SEQUENCE unchanged, local status and attendee row updated, and the next sync downloads nothing.
  - Decline hides the event locally and leaves STATUS unchanged on the wire.
  - Alias address-set matching.
  - The organizer path keeps STATUS/SEQUENCE behavior and leaves other attendees' answers alone.
  - Create writes ORGANIZER from the address set.
  - `ResolveImportedResponseStatus`.
- **ViewModels:**
  - loaded capabilities drive attachments, attendees, limit text and Send/Save
  - conservative defaults before the answer
  - Yahoo-like servers save, warn and refuse attachments
  - attendee limit refusal; stale answer ignored; read-only create refusal; low quota warning
  - Outlook without the service is unchanged
  - details refuses a response on a read-only calendar
  - details sends the response and shows the "not delivered" note
  - account summary text and its pending-discovery variant

## Live check (throwaway lab at `<scratchpad>/t2-lab`, outside the repository; output redacted, no password printed or stored, scan of outputs clean)

The lab was kept out of `tools/caldav-lab/Program.cs` to avoid colliding with T3 and T4. It uses the real `CalDavClient`, `CalDavSynchronizationEngine`, SQLite `DatabaseService`, ICS store and state store.

**iCloud** (attendee = iCloud account, ORGANIZER = Zimbra test address, written by a direct PUT):
- The setup PUT with `If-None-Match` returned 201, so iCloud accepted an event whose ORGANIZER is not the user.
- The address set has 2 principal paths, `urn:uuid:`, a `mailto:` outlook.com address and a `mailto:` icloud.com address. The identity resolved to the account's address.
- Before: `STATUS=CONFIRMED SEQUENCE=0`, own line `PARTSTAT=NEEDS-ACTION;RSVP=TRUE`.
- `RespondToEventAsync(Accept)` sent exactly 1 request: `PUT .../mailcal-t2-lab-....ics If-Match -> 204 (ETag returned)`.
- After: `STATUS=CONFIRMED SEQUENCE=0`, own line `PARTSTAT=ACCEPTED;RSVP=TRUE`, organizer line unchanged.
- Local state: Status=Accepted, own attendee row Accepted. The next sync sent 2 requests and downloaded nothing.
- Cleanup: `DELETE If-Match -> 204`, then `GET -> 404`. The lab ran twice on iCloud; both events were deleted, the first with a DELETE that returned 204.

**Zimbra (OVH)**, roles reversed (attendee = Zimbra account, ORGANIZER = iCloud address):
- The setup PUT returned 201.
- Before: `STATUS=CONFIRMED SEQUENCE=0`. Zimbra had already dropped the organizer's own ATTENDEE line from the stored copy.
- The response was 1 request, `PUT If-Match -> 204 (ETag returned)`.
- After: `STATUS=CONFIRMED SEQUENCE=0`, own line `PARTSTAT=ACCEPTED`. Zimbra removed `RSVP=TRUE` itself after processing the reply; on iCloud the same mutator output kept it.
- The next sync downloaded nothing.
- Cleanup: `DELETE -> 204`, then `GET -> 404`.

Side effect: both servers schedule implicitly, so each test reply (and each attendee-side delete) probably sent an iTIP message to the other test mailbox.

## Unfinished / for T5

1. **Import still maps STATUS, not the user's PARTSTAT.** This was seen live: an unanswered invitation showed `Status=Accepted` locally before any response, because `CalDavClient.CreateCalendarEvent` maps CONFIRMED to Accepted. After a reply, the local status is right until the resource is downloaded again; then a decline would become visible again.
   - Neither server re-downloaded after the reply (both returned an ETag on PUT), so this did not show in the lab.
   - Fix (outside T2's allowed files): in `CalDavSynchronizationEngine.ApplyResourceAsync`/`ApplyExpandedEventsAsync`, or in `CalDavClient.CreateCalendarEvent`, call `CalDavSynchronizationEngine.ResolveImportedResponseStatus(remoteEvent, identity)` (tested).
   - When it is non-null, use it as `Status` and set `IsHidden = (status == Cancelled)`.
   - `CalDavCalendarEvent` is init-only, so the projection needs a copy, or the mapping belongs in the client parser with an identity argument.
2. **T4 dependency.** Compose now enables attachments wherever `SupportsAttachments` is true. Today that means servers that advertise `calendar-managed-attachments` (iCloud and the fake server). `BuildResultAsync` sends the drafts, so T4's upload path must exist before release, or T4's capability rule must keep it false.
3. **Behavior changes to note:**
   - Local calendars with attendees now show "Save" instead of "Send", because `CalendarCapabilities.Local.SendsInvitations` is false.
   - With no calendar selected, Add attachment is disabled.
   - Organizer edits no longer duplicate attendee lines (the latent bug above).
4. **Handoff (not possible here):**
   - Windows Debug x64 build
   - Windows Release x64 gate (`dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`)
   - macOS Debug arm64 and x64 builds
   - the manual UI checks listed above, after the head bindings are added

## Suggested `CalendarCapabilityService` changes (not made)

- Make `ResolveSource` and `GetStaticCapabilities` public, or add `ICalendarCapabilityService.GetFallbackCapabilities(MailAccount, AccountCalendar)`. `CalendarCapabilityPresenter.GetFallback` could then drop its copy of the mapping.
- `SendsInvitations` could also require a non-empty `ScheduleOutboxHref`. Both servers that advertise `calendar-auto-schedule` (iCloud, Zimbra) have one, so nothing changes today. It would guard against servers that advertise the token without scheduling.
- Account-level `IsReadOnly` is true only when every event calendar is read-only. That suits the settings summary. Per-calendar checks already go through `GetCalendarCapabilitiesAsync`.
