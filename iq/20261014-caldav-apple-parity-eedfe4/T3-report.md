# T3 report: DNS SRV discovery, default alarms, server limits

Status: done on this host (Linux/WSL). Nothing committed, no app launched, work record not edited. No schema change, no state-model field added.

## Files

Added
- `src/MailCal.Core/Synchronizers/CalDav/CalendarIcsBuilder.DefaultAlarms.cs`: `CalDavDefaultAlarm` record; `ParseDefaultAlarms`, `AddDefaultAlarms`, `TryParseIcsDuration` (partial of `CalendarIcsBuilder`)
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.DefaultAlarms.cs`: import seeding, create hook, public read API `GetDefaultReminders`
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.Limits.cs`: pre-PUT check, post-PUT quota bookkeeping
- `tests/MailCal.Core.Tests/Services/AutoDiscoveryCalDavTests.cs` (15 tests)
- `tests/MailCal.Core.Tests/Synchronizers/CalDavDefaultAlarmTests.cs` (29 tests). This file also defines `CalDavEngineTestHost`: the real engine over `FakeCalDavServer`, with an `InterceptingTransport` that can rewrite the home listing and answer PUTs, so `FakeCalDavServer.cs` was not edited.
- `tests/MailCal.Core.Tests/Synchronizers/CalDavLimitTests.cs` (18 tests)

Changed
- `src/MailCal.Core/Services/AutoDiscoveryService.cs`: RFC 6764 discovery (details below)
- `src/MailCal.Core.Domain/Interfaces/IAutoDiscoveryService.cs`: new `DiscoverCardDavServiceUriAsync`. The only implementer is `AutoDiscoveryService`; mocks keep compiling.
- `src/MailCal.Services/Dav/DavResponseHandler.cs`: `GetPreconditionMessage` runs before the status mapping
- `src/MailCal.Core.Domain/Translations/en_US/resources.json`: 3 keys after `DavError_InvalidResponse`
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.cs`: 4 one-line hooks (below). `CalendarIcsBuilder.cs` was **not** edited (see the default-alarm design).

## Hook lines inserted into `CalDavSynchronizationEngine.cs` (for conflict resolution)

1. `ApplyExpandedEventsAsync`, directly after `if (localItem == null) continue;` and before `SaveCalendarItemIcsAsync`:
   ```csharp
   await SeedDefaultRemindersAsync(context, existingLocalItem, localItem, remoteEvent, payload).ConfigureAwait(false);
   ```
2. `CreateEventAsync`, first statement:
   ```csharp
   reminders = await ApplyDefaultAlarmOnCreateAsync(account, item, reminders, cancellationToken).ConfigureAwait(false);
   ```
3. `UpsertEventAsync`, after the `var icsContent = snapshot == null ? ... : ...;` expression and before `_client.UpsertCalendarEventAsync`:
   ```csharp
   icsContent = await PrepareOutgoingIcsAsync(account, item, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);
   ```
4. `UpsertEventAsync`, after the PUT and directly before `SaveSeriesSnapshotAsync`:
   ```csharp
   await RecordQuotaUsageAsync(account, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);
   ```

All four were still in place after the T2/T4 edits at the time of writing (lines 737, 1113, 1216, 1229).

**Integrator note:** two other PUT paths bypass hooks 3 and 4:
- T2: `CalDavSynchronizationEngine.Scheduling.cs`, about line 95 (PARTSTAT reply)
- T4: `CalDavSynchronizationEngine.Attachments.cs`, about line 273

To give them the same pre-check and quota bookkeeping, add the same two lines around their `UpsertCalendarEventAsync` call (`PrepareOutgoingIcsAsync` with their `snapshot`, then `RecordQuotaUsageAsync`). The T4 path matters most, because an inline attachment can hit `max-resource-size` and the quota. For a PARTSTAT reply the change is harmless: an event that already exceeds the attendee limit may still change as long as no attendee is added.

## A. DNS SRV/TXT discovery (RFC 6764)

`DiscoverCalDavServiceUriAsync` and the new `DiscoverCardDavServiceUriAsync` share `DiscoverDavServiceUriAsync(DavService, ...)`. `DavService` is a private record carrying the SRV labels, the well-known path, the host prefix and the cache-key prefix. Candidates are probed in this order through the existing `TryResolveCalDavEndpointAsync` (GET, then OPTIONS; follows redirects; accepts 401, 403, 207 or a DAV header). The first candidate that answers wins.

1. **TLS SRV on the mail domain:** `_caldavs._tcp.<d>` SRV for each `BuildDomainCandidatesFromHost(domain)` (the domain, then its parent).
   - Targets are ordered by priority, then by higher weight. Every target is tried in that order.
   - The matching TXT record is read for `path=` (quoted or unquoted, several strings). Without one, `/.well-known/caldav` is used.
   - The URI is `https://<target>[:port]<path>`; port 443 is omitted.
2. **Plain SRV, upgraded:** if a domain has no `_caldavs` record, `_caldav._tcp.<d>` (and its TXT) is read. Its targets are tried over **HTTPS** first.
3. **Existing candidates:** `https://<d>/.well-known/caldav` and `https://caldav.<d>/`, unchanged.
4. **SRV on MX domains:** the MX-derived domains (`GetMxSearchDomainsAsync`, then `BuildDomainCandidatesFromHost` on each MX host) are tried as above, as the last secure resort.
5. **Plain HTTP:** the HTTP form of plain `_caldav` targets is tried only when nothing secure answered, with a Serilog warning.

Other behavior:
- A successful result is cached per domain. CardDAV results are cached under the key `carddav:<domain>`.
- Failures are not cached; the engine already retries after 30 min.
- A URI is never probed twice in one discovery.
- `ParseTxtPath` is `internal static` for tests. It rejects relative paths and `//host` paths.

**CardDAV:** the sibling method exists and has a test. It has **no consumer yet**: `CardDavClient` builds its own `/.well-known/carddav` URI inside `MailCal.Services`, which does not reference `MailCal.Core`. Wiring it in needs a change in `CardDavAddressBookService` or in the account setup, outside T3.

## B. Default alarms

**Parsing:** `CalendarIcsBuilder.ParseDefaultAlarms` reads the raw `CS:default-alarm-vevent-*` text.
- Line breaks are normalized and folded lines are joined. A value whose every line is indented (pretty-printed XML) is only trimmed.
- Every `VALARM` block is read.
- Blocks with an absolute trigger (`VALUE=DATE-TIME`) are ignored. So is `ACTION:NONE`, which is Apple's "no default" sentinel. iCloud stores exactly that on this test account's home datetime default.
- **Local reminder:**
  - A relative trigger at or before the start becomes `CalDavEventReminder` (seconds before start; `EMAIL` maps to Email, anything else to Popup).
  - A trigger after the start (Apple's all-day "on the day at 09:00", `PT9H`) or `RELATED=END` is still written to the server but gets no local reminder, because Mail-Cal reminders count seconds before the start.
- **Block written to the server:** the alarm-instance properties (`UID`, `X-WR-ALARMUID`, `ACKNOWLEDGED`, `RELATED-TO`, and any existing marker) are dropped, `X-APPLE-DEFAULT-ALARM:TRUE` is added, and long lines are folded again.

**Which default applies:** a value on the calendar wins over the home value, even when it means "none". The home value is the fallback. Timed events use `-datetime`; all-day events (`CalendarItem.IsAllDayEvent`) use `-date`.

**Import (hook 1):** an item that is new to Mail-Cal, has no parsed reminders, and whose resource text contains no `BEGIN:VALARM` gets the default reminder rows through `ICalendarService.SaveRemindersAsync`.
- Existing items keep their local reminder choice, the same rule as `ImapChangeProcessor`.
- Defaults are parsed once per `CalendarContext` (a `ConditionalWeakTable`), which costs one state-store read per calendar per sync.
- This approach avoids copying `CalDavCalendarEvent`, which T4 extended with `Attachments`.

**Create (hooks 2 and 3):**
1. `ApplyDefaultAlarmOnCreateAsync` resolves the calendar's default, but only when the user chose no reminder.
2. It stores the alarms for that `CalendarItem` in a `ConditionalWeakTable` and returns the matching `Reminder` rows, which `PersistCreatedCalendarEventAsync` saves with the final item ID.
3. Hook 3 calls `ApplyPendingDefaultAlarm`, which inserts the blocks before `END:VEVENT` of each VEVENT that has no VALARM, for creates only.

The insertion happens after `CalDavIcsMutator.Normalize`, so the size and quota checks include it. Updates never add a default. Failures to resolve a default are logged and never block the create.

**Read API for ViewModels (T2/T5):** `CalDavSynchronizationEngine.GetDefaultReminders(CalDavAccountState state, string? remoteCalendarId, bool isAllDay)` is `public static`. A ViewModel or service reads the state through `ICalDavStateStore.GetAsync(accountId)` and calls it, for example to preselect a reminder in compose. `CalendarCapabilities` and `CalendarCapabilityService` are untouched.

## C. Quota, resource-size and attendee limits

**Pre-check:** `EnsureWithinServerLimits(CalDavCalendarState, string ics, string? previousIcs)` is `internal static`. It reads the calendar state from `ICalDavStateStore` and checks:
1. **Size:** the UTF-8 byte length against `MaxResourceSize`, when it is greater than 0.
2. **Attendees:** the largest number of `ATTENDEE` properties on any VEVENT against `MaxAttendeesPerInstance`, when it is greater than 0. Attendees inside `VALARM` (e-mail alarm recipients) are not counted, and neither are folded continuation lines. A write is refused only when it also **adds** attendees compared with the stored copy.
3. **Quota:** growth (new size minus the stored `event.ics` size, or the full size for a create) against `QuotaAvailableBytes`, when that value is known.

A violation throws `DavRequestException` in the same shape as the server's own refusal:

| Violation | Status | Error element | Message |
| --- | --- | --- | --- |
| Size | 403 | `{urn:ietf:params:xml:ns:caldav}max-resource-size` | `DavError_ResourceTooLarge` |
| Attendees | 403 | `{...caldav}max-attendees-per-instance` | `DavError_TooManyAttendees` |
| Quota | 507 | `{DAV:}quota-not-exceeded` | `DavError_QuotaExceeded` |

Callers can use `HasError(...)` for both pre-checks and server answers. No PUT is sent, and nothing is persisted locally, because `PersistCreatedCalendarEventAsync` runs after the PUT.

**After a successful PUT (hook 4):** `QuotaAvailableBytes` drops by the growth and `QuotaUsedBytes` rises by it (both floored at 0) through `ICalDavStateStore.UpdateAsync`. A failure is logged only. The next home listing (`ApplyCalendars`) replaces both values with the server's figures.

**Server-side mapping (`DavResponseHandler`):**
- Before the status switch, the error elements `max-resource-size` and `max-attendees-per-instance` map to their messages.
- HTTP 507, `quota-not-exceeded` or `sufficient-disk-space` map to `DavError_QuotaExceeded`.
- 507 used to fall into `DavError_ServerUnavailable`. Multistatus-internal 507s (sync truncation) are unaffected.
- This is shared with CardDAV, where it is equally correct.

**New translation keys** (en_US only, inserted after `DavError_InvalidResponse`):
- `DavError_QuotaExceeded`: "There is not enough storage left on the calendar server for this change."
- `DavError_ResourceTooLarge`: "This event is larger than the calendar server accepts."
- `DavError_TooManyAttendees`: "This event has more attendees than the calendar server allows."

## Verification (this host)

- **Debug build:** `dotnet build tests/MailCal.Core.Tests/... -c Debug -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true` gives 0 errors and no warnings from T3 files.
- **Filtered tests** (`CalDav|Imap|CardDav|AutoDiscovery`): **Passed 343, Failed 0**. That is the 239 baseline, 62 new T3 tests, and the rest from T2/T4. An earlier run had 3 failures in T2's in-progress `CalDavScheduling*Tests`; they passed on the final run.
- **T3 tests and existing engine tests:** `AutoDiscoveryCalDavTests|CalDavDefaultAlarmTests|CalDavLimitTests|CalDavSynchronizationEngineTests` gives 75/75 (62 new plus 13 existing engine tests).
- **Release build:** `dotnet build src/MailCal.Core/MailCal.Core.csproj -c Release -p:MailCalTargetPlatform=MacOS -p:NuGetAudit=false` currently fails on **T4's** `src/MailCal.Services/CalDavClient.Attachments.cs(60,66)` CS8604, which is not mine.
  - With `-p:WarningsAsErrors= -p:TreatWarningsAsErrors=false` it builds. The only warning is that T4 line; T3 files have none.
  - Re-run the plain Release command after T4 fixes it.
- **Test coverage:**
  - Discovery: SRV with a TXT path; SRV without TXT plus a redirect; priority and weight order with a non-default port; a plain record upgraded to HTTPS, with HTTP last; a TLS record hiding the plain one; the well-known fallback and the cache; SRV on an MX domain; nothing published gives null; CardDAV labels; TXT parsing.
  - Default alarms: DISPLAY `-PT15M`; EMAIL `-P1D`; absolute triggers and `ACTION:NONE` ignored; folded lines; indented XML; a trigger after the start; duration parsing (valid and invalid); insertion only into events without an alarm; calendar default over home default, including "none"; import seeding for a new event without a VALARM; no seeding when the event has its own (or an absolute) VALARM; an existing item keeps the user's empty choice; all-day events use the calendar date default; no server default means no seeding; a create writes the VALARM with `X-APPLE-DEFAULT-ALARM:TRUE` and the result expands back to a 900 s reminder; a chosen reminder means no default; no default means no VALARM; an update never adds one.
  - Limits: each limit; zero or missing limits mean unlimited; the attendee grandfathering rule; per-instance counting that ignores alarm attendees; growth counted for updates; quota delta bookkeeping; the engine refuses an over-quota create and a create with too many attendees with **no PUT**; the quota is adjusted after a create and an update; a server 507 on PUT surfaces as QuotaExceeded; `DavResponseHandler` mapping (507, `quota-not-exceeded`, `sufficient-disk-space`, `max-resource-size` on 403 and 413, `max-attendees-per-instance`, plain 403, 503).

## Live evidence

The lab ran from the scratchpad (`.../scratchpad/t3lab`, not in the repository). No credentials were printed or persisted.

| Address | Result | DoH queries | Probes |
| --- | --- | --- | --- |
| `x@icloud.com` | `https://caldav.icloud.com/.well-known/caldav` | 2 (SRV + TXT `_caldavs._tcp.icloud.com`) | GET 400, OPTIONS 200 with DAV |
| `x@yahoo.com` | `https://caldav.calendar.yahoo.com/principals/` | 2 | 1 GET → 401 after redirect |
| `x@adastria.ws` | **null** | 13 (SRV and plain SRV on the domain, MX, then SRV on `mx0..3.mail.ovh.net` and `mail.ovh.net`) | well-known 404/405, `caldav.adastria.ws` does not resolve; 1.2 s total |
| `x@fastmail.com` (extra) | `https://d277161.caldav.fastmail.com/dav/calendars` | 2 | 1 |
| CardDAV `x@icloud.com` | `https://contacts.icloud.com/.well-known/carddav` | 2 | 1 |
| CardDAV `x@fastmail.com` | `https://d277161.carddav.fastmail.com/dav/addressbooks` | 2 | 1 |

- **Discovered URIs work with real credentials** (`CalDavClient.DiscoverAccountAsync`):
  - iCloud: principal found, 1 home, 4 event calendars
  - Yahoo: principal found, 1 home, 1 event calendar
  - Zimbra (`adastria.ws`): not discoverable, as expected
- **iCloud default alarms (real values):**
  - The home **datetime** default is the Apple `ACTION:NONE` + `TRIGGER;VALUE=DATE-TIME:19760401T005545Z` sentinel, which correctly parses as no alarm.
  - The home **date** default is `TRIGGER:-PT15H` / `ACTION:AUDIO` / `ATTACH;VALUE=URI:Basso`, which parses to a 54,000 s reminder plus a block carrying `X-APPLE-DEFAULT-ALARM:TRUE` with its UIDs dropped.
  - No per-calendar values.
- **Limit dry run with real values** (no writes): iCloud calendars report `quota-available-bytes` of about 1.07 GB, `max-attendees-per-instance` 300 and no `max-resource-size`. A 479 B sample with 5 attendees was allowed; the same sample with 405 attendees was refused (403, TooManyAttendees). Yahoo (`max-resource-size` 0, nothing else) allowed both.

## Unfinished, limits and handoff

- **PUT paths:** add hooks 3 and 4 to the T2 Scheduling and T4 Attachments PUT paths during integration (see above).
- **Release gate:** re-run the plain Release build once T4's CS8604 is fixed.
- **Seeding scope:** seeding only checks for `BEGIN:VALARM` anywhere in the resource. An override instance without its own alarm in a series whose master has one is not seeded, which is deliberate.
- **Past events:** past events in a first import also get the default reminder row, as Apple does. They never notify, because their trigger time is in the past.
- **Reminder types:** an `EMAIL` default becomes an Email reminder row. Mail-Cal only notifies Popup rows, as before.
- **Discovery URI choice:** for iCloud the probe returns the `.well-known` URI itself, because GET gives 400 and OPTIONS gives 200, so there is no redirect to follow. `DavTransport` follows the PROPFIND redirect at sync time, and the live discovery confirmed this.
- **Handoff:** Windows Debug x64 build, Windows Release x64 AOT gate (`dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`), macOS Debug arm64 and x64 builds. No UI changed. The new messages reach users wherever write errors already show `Exception.Message`, which needs a manual check once T2's ViewModel refusal handling is in.
