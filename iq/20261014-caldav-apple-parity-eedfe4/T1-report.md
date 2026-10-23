# T1 report: CalDAV engine, incremental sync, capability model

Status: done on this host (Linux/WSL). Nothing committed, no app launched, work record not edited.

## Files

Added
- `src/MailCal.Core.Domain/Interfaces/ICalDavClient.Sync.cs`: partial interface with discovery, sync-collection, ETag listing, multiget, GET and expansion
- `src/MailCal.Core.Domain/Interfaces/ICalDavStateStore.cs`, `ICalDavSynchronizationEngine.cs`, `ICalendarCapabilityService.cs`
- `src/MailCal.Core.Domain/Models/Calendar/CalDavSyncModels.cs`: `CalDavResource`, `CalDavSyncPage`, `CalDavAccountDiscovery`, `CalendarIcsShadow`
- `src/MailCal.Core.Domain/Models/Calendar/CalDavStateModels.cs`: `CalDavAccountState`, `CalDavCalendarState`
- `src/MailCal.Core.Domain/Models/Calendar/CalendarCapabilities.cs`: record plus the `CalendarCapabilitySource` enum
- `src/MailCal.Services/CalDavClient.Sync.cs`: partial client
- `src/MailCal.Services/CalDav/CalDavStateStore.cs`: store plus its source-generated `CalDavStateJsonContext`
- `src/MailCal.Services/CalendarCapabilityService.cs`
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.cs`, `CalDavConnection.cs`, `CalendarIcsBuilder.cs` (the ICS builder and item defaults, moved out of `ImapSynchronizer`)
- `tests/MailCal.Core.Tests/Synchronizers/FakeCalDavServer.cs`, `CalDavSynchronizationEngineTests.cs`, `CalDavStateAndCapabilityTests.cs`

Changed
- `ICalDavClient.cs` and `CalDavClient.cs` are now `partial`. Discovery uses one shared collection PROPFIND that also asks for the capability properties, and `Prefer: return=minimal` is sent on every PROPFIND and REPORT. `ParseCalendarCollection` delegates to the new parser and keeps its name and signature, so the reflection tests still work.
- `CalDavCalendar.cs`: new init-only properties (components, reports, sync-collection and multiget support, quota, max-resource-size, max-attendees, default alarms, owner).
- `ICalendarIcsFileService.cs` and `CalendarIcsFileService.cs`: new `GetCalendarIcsShadowsAsync`. It scans the `event.meta.json` files and leaves the file layout unchanged.
- `DefaultChangeProcessor.cs` (`IImapChangeProcessor`) and `ImapChangeProcessor.cs`: `GetCalendarIcsShadowsAsync` now delegates to the ICS service.
- `ImapSynchronizer.cs`: calendar sync becomes about 10 lines that delegate to the engine. `CalDavCalendarOperationHandler` is now a thin router to the engine; `LocalCalendarOperationHandler` stays where it is. There is a new last optional constructor parameter, `ICalDavSynchronizationEngine`; when it is null, the synchronizer builds an engine from its existing dependencies. About 1,000 lines moved out.
- `SynchronizerFactory.cs`: the engine is injected. Registration: `CoreContainerSetup.cs` (engine, transient) and `ServicesContainerSetup.cs` (`ICalDavStateStore` singleton, `ICalendarCapabilityService` transient).
- Tests: `ImapSynchronizerCalDavConfigurationTests.cs` and `CalDavCalendarMetadataTests.cs` now mock the new client members and call `CalDavConnection` and the engine directly instead of private `ImapSynchronizer` methods. Their intent is kept (legacy re-import with repair, re-anchor with pruning, every series row gets the new ETag, routing).

Untouched: no schema, entity, migration, ViewModel, XAML/AppKit, `AutoDiscoveryService`, attachment code or translation change.

## Design

**State file.** The path is `<MimeStorageFolderPath>/CalendarIcs/<accountId:N>/caldav-state.json`, written as indented camelCase JSON with System.Text.Json source generation. Writes go to `*.tmp` and then a move, under a per-file lock.
- Account level: `version`, `principalHref`, `calendarHomeHrefs`, `calendarUserAddresses` (Yahoo's `mailto:null` dropped; principal paths and `urn:uuid:` kept raw), `scheduleInboxHref`, `scheduleOutboxHref`, `davCapabilities` (from the OPTIONS `DAV` header on the first home), the home's default alarms, and `discoveredAtUtc`.
- `calendars{remoteCalendarId}`: display name, `supportedComponents`, `supportedReports` (`{ns}name`), `supportsSyncCollection`, `supportsMultiget`, the refusal flags `syncCollectionRefused` and `multigetRefused`, `isReadOnly`, quota available and used, `maxResourceSize`, `maxAttendeesPerInstance`, default alarms (raw VALARM text), `ownerHref`, `lastSyncToken`, `lastSyncTokenFromReport`, `lastCTag`, `lastSuccessfulSyncUtc`.
- Credentials are never written. A missing file, a corrupt file or a version mismatch reads as an empty state, which leads to rediscovery.
- VTODO-only collections are recorded but never become calendars.

**Token.** `AccountCalendar.SynchronizationDeltaToken` keeps the `win=YYYYMMDD|<syncToken>|<ctag>` format, through the same `CalendarSyncWindowToken.Encode` call. The sync-collection continuation token lives only in the state file.

**Discovery per sync.** While the state is younger than 24 h, a sync sends one Depth-1 PROPFIND on the stored home. A full discovery (current-user-principal, then the principal's home, address set and inbox/outbox, then OPTIONS, then the home listing) runs when the state is missing or stale, when the home answers 4xx, or when the listing has no VEVENT calendar.

**Per-calendar ladder.**
1. If the PROPFIND sync token and ctag are unchanged and no re-anchor is due, the calendar is skipped (no REPORT).
2. Initial sync or a legacy token (stored without a window): ETag-only `calendar-query` over the window, then download everything listed. A full window reconcile follows, which keeps the legacy repair behavior.
3. Delta: `sync-collection` (Depth 0, `getetag`) from the stored token, following 507 truncation pages. The ladder falls through to step 4 in these cases:
   - **Missing token in the answer:** a 207 without `D:sync-token` (Yahoo's answer) sets `syncCollectionRefused`.
   - **Unsupported request:** a 4xx unsupported-request answer also sets `syncCollectionRefused`.
   - **Refused token:** `valid-sync-token`, 403, 409, 410 or 412 does not set the flag. The exception is a token that only ever came from PROPFIND; such a token is not retried.
4. ETag listing (`calendar-query`, `getetag` and a VEVENT time-range, no `calendar-data`), compared with the ICS shadows by resource key (unescaped path) to find changed and deleted resources.
5. Last resort, when the ETag query itself is refused: the old full-window `GetCalendarEventsAsync`.

**Downloads.** `calendar-multiget` in batches of 50 with absolute-path hrefs. GET (4 at a time) covers whatever multiget did not return, and every resource once multiget is refused; that refusal is remembered.

**Applying changes.** Each fetched ICS is expanded with the existing `ParseCalendarData` and stored through `ManageCalendarEventAsync` and `SaveCalendarItemIcsAsync`. All rows of one resource share its href and ETag. Rows of a changed resource that the new version no longer produces are deleted, and a deleted href removes all of its rows.

**Re-anchor.** Unchanged resources are expanded again from their stored `event.ics`, without a request. Only resources whose file is missing are downloaded. Expired occurrences are pruned as before.

**Writes.** Writes keep the same behavior: a PUT with `If-Match`, or `If-None-Match: *` for a create, and the ETag refreshed on every series row. A per-calendar lock now separates writes from a sync of the same calendar.

## Verification (this host)

- `dotnet build tests/MailCal.Core.Tests/... -c Debug -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true`: 0 errors.
- Filtered tests (`CalDav|Imap|CardDav`): **Passed 239, Failed 0** (baseline 221; 18 new: 13 engine tests, 5 state and capability tests).
- Full `MailCal.Core.Tests`: Passed 1073, Failed 13. The same 13 fail on a clean `HEAD` worktree on this host (`MailRecipientHistoryTests`, `ActivationFileImportServiceTests` vCard import, a Gmail date-format test, NFC key derivation, HTML preview, a backslash file name and five `CalendarAccessibilityFormatterTests`). They come from Linux and invariant-globalization differences, not from T1.
- `dotnet build src/MailCal.Mail.ViewModels/...`: 0 errors.
- `dotnet build src/MailCal.Core/MailCal.Core.csproj -c Release -p:MailCalTargetPlatform=MacOS -p:NuGetAudit=false`: 0 errors, no warnings from the new files. Without `NuGetAudit=false`, Release restore fails here on the existing advisories NU1902 and NU1903 for `System.Security.Cryptography.Pkcs` 10.0.0. That is unrelated to T1 and needs a separate dependency bump.
- New tests cover:
  - initial full listing, after which an unchanged sync sends 1 PROPFIND and no calendar-data
  - a sync-collection delta with changed, new and deleted resources
  - 507 paging
  - an expired token recovered through the ETag listing
  - a Yahoo-style empty 207, remembered as a refusal
  - a server without sync-collection that loses a resource
  - multiget refused, falling back to GET
  - a refused ETag query, falling back to the full window
  - re-anchoring from stored ICS with only 1 PROPFIND
  - writes with `If-Match`, where the next sync downloads nothing and a stale ETag gets 412
  - capability extraction from PROPFIND and OPTIONS
  - a server without a DAV header
  - state round trip, and recovery from a corrupt file
  - capability service static sets, CalDAV defaults and per-calendar values

## Live check (`tools/caldav-lab/`, excluded from git; throwaway SQLite with the real `DatabaseService`)

Requests per phase (calendar-data payloads in parentheses). Sync 1 includes discovery.

| Server | Sync 1 (initial) | Sync 2 (nothing changed) | After create | After delete |
| --- | --- | --- | --- | --- |
| iCloud (4 event calendars, 308 resources, 629 rows) | 17 (308) | **1 PROPFIND (0)** | 3 = PROPFIND + sync-collection + multiget (1 href) **(1)** | 2 = PROPFIND + sync-collection (0); row removed |
| Yahoo (no sync-collection advertised) | 5 (0) | **1 PROPFIND (0)** | 3 = PROPFIND + ETag query + multiget (1 href) **(1)** | 2 = PROPFIND + ETag query (0); row removed |
| OVH Zimbra (no sync-collection) | 5 (0) | **1 PROPFIND (0)** | 3 = PROPFIND + ETag query + multiget (1 href) **(1)** | 2 = PROPFIND + ETag query (0); row removed |

Before this change, every sync with a changed token downloaded the whole three-year window with calendar-data. Each lab event was created with a raw client PUT (`If-None-Match: *`) and deleted with `If-Match`. None were left on the servers.

The state file never contained the password. Probes forcing a sync-collection REPORT:
- iCloud: token present, but an empty-token listing returns 65 resources for a calendar with only 6 in the window. That is why the initial sync uses the time-range ETag listing and keeps the PROPFIND `sync-token` as the starting token; iCloud accepted it.
- Yahoo: an empty 207 without a token, which the engine treats as unsupported.
- Zimbra: 403 `supported-report`, which the engine also treats as unsupported.

Server facts that differ from `research.md` (OPTIONS on the calendar home):
- Yahoo returns `DAV: 1, 3, calendar-access`.
- Zimbra returns `1, 2, 3, access-control, calendar-access, calendar-auto-schedule, ...`, so Zimbra reports `SendsInvitations = true`; Yahoo does not.
- Yahoo's PUT returns no ETag, so a resource created by Mail-Cal on Yahoo is downloaded once on the next sync.
- Zimbra's component set is `VEVENT,VFREEBUSY`, and its Tasks collection `VTODO,VFREEBUSY` is skipped.
- iCloud has default alarms on the home and none per calendar.
- iCloud's address set includes relative principal paths.

## Unfinished or uncertain

- Not possible on this host, handoff: Windows Debug x64 build, the Windows Release x64 AOT gate (`dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`), and macOS Debug arm64 and x64 builds. No UI changed, so there is nothing to check manually.
- Behavior changes to be aware of:
  - A failure in one calendar is now reported as a `PartiallyCompleted` issue while the other calendars continue; 401 still fails the whole sync.
  - `ExecuteRequests` and `SingleCalendar` syncs no longer download the window again; they check ETags.
  - A failed endpoint auto-discovery is retried after 30 minutes. Before, it was retried only when the synchronizer was recreated.
- `SendsInvitations` comes only from the `calendar-auto-schedule` DAV token. That matches iCloud and Zimbra; T2 can refine it with `scheduleOutboxHref`.
- Default alarm properties are requested in both the `C:` and `CS:` namespaces; iCloud answered on the home.
- Multiget is sent with `Depth: 1`. All three servers accepted it.
- Servers without both ctag and sync-token still re-import on every sync, as before.

## Extension points for T2–T4

- **New client feature:** add `src/MailCal.Core.Domain/Interfaces/ICalDavClient.<Feature>.cs` (`public partial interface ICalDavClient`) and `src/MailCal.Services/CalDavClient.<Feature>.cs` (`public sealed partial class CalDavClient`).
  - Reuse the private helpers `SendXmlAsync`, `_davTransport`, `CreateAuthentication`, `ValidateConnectionSettings`, `CreateAbsoluteUri`, `GetSuccessProps`, `ResourceKey` and `ParseStatusCode`.
  - Add any new collection property to `CalendarCollectionProperties` in `CalDavClient.Sync.cs`, and map it in `ParseCalendarCollections` and `CalDavSynchronizationEngine.ApplyCalendars`.
  - Existing `Mock<ICalDavClient>` setups keep compiling.
- **State store:** inject `ICalDavStateStore`.
  - Read with `GetAsync(accountId)`, which never returns null and returns a fresh copy. Look up a calendar with `state.FindCalendar(remoteCalendarId)`, which ignores case.
  - Write with `UpdateAsync(accountId, state => ...)`, for example to adjust quota after a PUT. That call reads, changes and writes under the file lock.
  - Add fields to `CalDavAccountState` or `CalDavCalendarState` as `{ get; set; }` with defaults. Leave `CurrentVersion` at 1 unless the old content must be discarded.
- **Capabilities:** `ICalendarCapabilityService.GetAccountCapabilitiesAsync(accountId)` and `GetCalendarCapabilitiesAsync(calendarId)` never return null.
  - Outlook, Gmail and Local return the static sets `CalendarCapabilities.Outlook`, `.Gmail` and `.Local`.
  - CalDAV without discovery returns `CalendarCapabilities.CalDavDefault` (attendees and reminders only).
  - CalDAV with state goes through `CalendarCapabilityService.FromCalDavState(state, calendarState)`, which maps as follows:
    - attachments come from the `calendar-managed-attachments` DAV token
    - `SendsInvitations` comes from `calendar-auto-schedule`
    - default alarms come from `calendar-default-alarms` or any stored default-alarm value
    - `MaxAttendees`, quota and `MaxResourceSize` (> 0) take the strictest value across event calendars at account level
    - `IsReadOnly` combines the state with `AccountCalendar.IsReadOnly`
  - T4 should change the attachment rule there once uploads exist.
- **Engine:** user changes go through `ICalDavSynchronizationEngine.CreateEventAsync`, `UpdateEventAsync`, `DeleteEventAsync` and `RespondToEventAsync`.
  - T2's PARTSTAT change belongs in `RespondToEventAsync`. It currently sets `CalendarItem.Status` as before, and `CalDavCalendarOperationHandler` maps Accept, Decline and Tentative to it.
  - The sender identity is in `state.CalendarUserAddresses`.
  - Downloads for T4 can use `ICalDavClient.GetEventResourceAsync` or a new partial file. The resource href and ETag of an item come from `IImapChangeProcessor.GetCalendarItemIcsAsync` or `GetCalendarIcsShadowsAsync`.
- **Tests:** `FakeCalDavServer` has switches (`SupportsSyncCollection`, `AdvertiseSyncCollection`, `EmptyMultistatusForUnsupportedReports`, `SupportsMultiget`, `RefuseEtagQuery`, `SyncPageSize`, `OldestValidRevision`, `DavHeader`) and records `Requests`, `PreferHeaders` and `CalendarDataResponses`. Add routes for new REPORTs or POSTs there.
