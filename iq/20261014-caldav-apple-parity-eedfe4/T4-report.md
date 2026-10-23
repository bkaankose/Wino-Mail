# T4 report: CalDAV event attachments

Status: done on this host (Linux/WSL). Nothing committed, no app launched, work record not edited. No schema change, no entity change, no ViewModel change, no translation key.

## Files

Added
- `src/MailCal.Core.Domain/Models/Calendar/CalDavEventAttachment.cs`: `CalDavEventAttachment` (Uri, FileName, ContentType, Size, ManagedId, InlineData, RemoteAttachmentId, `InlineIdPrefix = "inline:"`) and `CalDavManagedAttachmentResult` (ETag, ManagedId, Location, IcsContent).
- `src/MailCal.Core.Domain/Interfaces/ICalDavClient.Attachments.cs`: partial interface (see below).
- `src/MailCal.Services/CalDavClient.Attachments.cs`: ATTACH parsing, `ReadEventAttachments`, bounded streaming download, `AddManagedAttachmentAsync`, `RemoveManagedAttachmentAsync`, `BuildContentDisposition`; plus the public static `CalDavAttachmentIcs.RemoveManagedAttachments` (text-level removal of ATTACH lines by MANAGED-ID, keeps every other line byte for byte).
- `src/MailCal.Core/Synchronizers/CalDav/CalDavAttachmentRows.cs`: replaces the `CalendarAttachment` rows of one item from parsed attachments (keeps Id and downloaded copy for unchanged RemoteAttachmentId, writes inline data, deletes copies of removed rows under the attachment folder, skips the write when nothing changed). Shared by the processor and the engine.
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.Attachments.cs`: download, upload, remove, origin check.
- `src/MailCal.Core/Integration/Processors/ImapChangeProcessor.Attachments.cs`: `SaveCalDavAttachmentsAsync` (never fails the import).
- `tests/MailCal.Core.Tests/Synchronizers/CalDavAttachmentTests.cs`: 20 tests.

Changed (anchored insertions only)
- `src/MailCal.Services/CalDavClient.cs`, in `CreateCalendarEvent`:
  ```
              Reminders = reminders,
              Attachments = ParseAttachments(sourceEvent)
          };
  ```
- `src/MailCal.Core.Domain/Models/Calendar/CalDavCalendarEvent.cs`: `public IReadOnlyList<CalDavEventAttachment> Attachments { get; init; } = Array.Empty<CalDavEventAttachment>();`
- `src/MailCal.Core.Domain/Enums/DavAuthenticationKind.cs`: new value `Anonymous = 3` (no Authorization header). `DavTransport` is untouched: its switch has no case for it, so nothing is sent.
- `src/MailCal.Core/Integration/Processors/ImapChangeProcessor.cs`: `public class` became `public partial class`; constructor got a last optional parameter `IApplicationConfiguration applicationConfiguration = null` with `_applicationConfiguration = applicationConfiguration;`; at the end of `ManageCalendarEventAsync`, after the reminder block:
  ```
          await SaveCalDavAttachmentsAsync(savingItemId, calendarEvent).ConfigureAwait(false);
  ```
  DI (`AddTransient<IImapChangeProcessor, ImapChangeProcessor>`) resolves the new parameter; existing `new ImapChangeProcessor(...)` calls compile unchanged (without it, inline data is not written at import and is read from the stored ICS on download instead).
- `src/MailCal.Core/Synchronizers/CalDav/CalDavSynchronizationEngine.cs`: signatures got the attachment parameter; one call per method, after the PUT/snapshot save (CreateEventAsync: after `PersistCreatedCalendarEventAsync`, so it runs after T3's default-alarm hook and T2's Upsert logic):
  ```
          await ApplyAttachmentChangesAsync(account, item, attachments, isNewEvent: true, cancellationToken).ConfigureAwait(false);
  ```
  and `UpdateEventAsync` changed from an expression body to
  ```
      {
          await UpsertEventAsync(account, item, attendees, cancellationToken).ConfigureAwait(false);
          await ApplyAttachmentChangesAsync(account, item, attachments, isNewEvent: false, cancellationToken).ConfigureAwait(false);
      }
  ```
  `UpsertEventAsync` and `RespondToEventAsync` are untouched. The attachment step takes the calendar lock itself (Upsert has released it by then).
- `src/MailCal.Core/Synchronizers/ImapSynchronizer.cs`:
  - `DownloadCalendarAttachmentAsync` no longer throws: local accounts copy the stored file (`LocalCalendarOperationHandler.CopyStoredAttachmentAsync`, new internal static helper; throws `FileNotFoundException` when the copy is gone), CalDAV accounts delegate to `_calDavSynchronizationEngine.DownloadAttachmentAsync`.
  - `CalDavCalendarOperationHandler`: `CreateEventAsync(..., request.ComposeResult?.Attachments)` and `UpdateEventAsync(..., request.Attachments)`.
- `src/MailCal.Core/Requests/Calendar/UpdateCalendarEventRequest.cs`: new `public List<CalendarEventComposeAttachmentDraft> Attachments { get; init; }` (null = unchanged).
- `tests/MailCal.Core.Tests/Synchronizers/FakeCalDavServer.cs`: a `RecordAttachmentRequest` call after `Requests.Add`, two `case` lines before the existing `GET` case, and a `#region Attachments (RFC 8607)` block before `HomeListing`: `PutFile(absoluteUrl, bytes)` (any host), `Authentications` log (`"GET host/path Basic|Anonymous"`), `AttachmentActions` (path, query, Content-Type, Content-Disposition, If-Match, body), switches `AttachmentOrigin`, `BareAttachmentResponses` (iCloud-style answer), `IgnoreAttachmentRemove` (iCloud behavior). Existing behavior unchanged.
- `src/MailCal.Services/CalendarCapabilityService.cs`: not changed. `SupportsAttachments` stays "DAV header has `calendar-managed-attachments`" (iCloud true, Yahoo and Zimbra false, verified live). Read-only calendars are already reported through `IsReadOnly`; the UI should check both.

## Interface changes

`ICalDavSynchronizationEngine`:
```
Task CreateEventAsync(MailAccount account, CalendarItem item, List<CalendarEventAttendee>? attendees, List<Reminder>? reminders,
    IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null, CancellationToken cancellationToken = default);
Task UpdateEventAsync(MailAccount account, CalendarItem item, List<CalendarEventAttendee>? attendees,
    IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null, CancellationToken cancellationToken = default);
Task DownloadAttachmentAsync(MailAccount account, CalendarItem item, CalendarAttachment attachment, string localFilePath,
    CancellationToken cancellationToken = default);
```
Update semantics: `null` leaves attachments alone; otherwise the list is the complete wanted set. A draft whose `Id` equals a stored `CalendarAttachment.Id` keeps it, a draft with a readable `FilePath` is uploaded, and a stored managed attachment missing from the list is removed. Plain ATTACH URIs written by other clients are never removed by Mail-Cal.

`ICalDavClient` (new partial `ICalDavClient.Attachments.cs`):
```
IReadOnlyList<CalDavEventAttachment> ReadEventAttachments(string icsContent, string remoteEventId);
Task<long> DownloadAttachmentAsync(CalDavConnectionSettings, Uri attachmentUri, bool useAccountCredentials, Stream destination, long maximumBytes, CancellationToken);
Task<CalDavManagedAttachmentResult> AddManagedAttachmentAsync(CalDavConnectionSettings, string exactHref, string eTag, string fileName, string contentType, byte[] content, CancellationToken);
Task<CalDavManagedAttachmentResult> RemoveManagedAttachmentAsync(CalDavConnectionSettings, string exactHref, string eTag, string managedId, CancellationToken);
```

## Behavior

- **Import (all servers).** Every ATTACH of the VEVENT that produced the row (overriding instance, else master) becomes a row. RemoteAttachmentId = MANAGED-ID, else the URI, else `inline:<sha256 prefix>`. FileName = `FILENAME` / `X-APPLE-FILENAME` / `X-FILENAME`, else the unescaped last URI segment, else `attachment`. ContentType = `FMTTYPE`, else `MimeTypes.GetMimeType(fileName)`. Size = `SIZE`, else 0 (inline: decoded length). Each import replaces the item's rows; a row whose RemoteAttachmentId is unchanged and whose downloaded file still exists keeps its Id, `IsDownloaded` and `LocalFilePath`. Inline data of a single event or master is written to `<CalendarAttachmentsFolderPath>/<itemId:N>/<safe name>` and marked downloaded; occurrences keep it in their ICS copy and get it on download.
- **Download.** The URL is read from the item's stored ICS copy by RemoteAttachmentId (so rotating iCloud URLs and MANAGED-ID rows work), falling back to RemoteAttachmentId when it is itself an http(s) URL. Credentials are sent only when the URL's scheme, host and port match the service URL, a calendar home, the principal, the calendar or the resource. Any other origin is fetched anonymously and only over HTTPS. The body is streamed to `<path>.part`, bounded at 100 MB (`InvalidDataException` above it; the Content-Length is checked first), moved into place, then `MarkAttachmentDownloadedAsync`. Already-downloaded rows are copied.
- **Upload (managed token only).** After the event write: for each new file (≤ 50 MB, because `DavTransport` buffers request bodies), POST `<href>?action=attachment-add` with the file's MIME type, `Content-Disposition: attachment; filename="…"` (non-ASCII names get an ASCII fallback plus `filename*=UTF-8''…`), and `If-Match` with the current ETag. A response that carries an ETag and `text/calendar` body is used as is; otherwise the resource is read back with GET. The managed ID comes from `Cal-Managed-ID`, else from the MANAGED-IDs that are new in the read-back ICS. Then `SaveSeriesSnapshotAsync` (every series row gets the new ICS and ETag) and the attachment rows of every series row are rebuilt.
- **Remove.** POST `?action=attachment-remove&managed-id=…` with `If-Match`; if the read-back still contains that MANAGED-ID, PUT the resource without that ATTACH property (RFC 8607 section 3.6), with `If-Match`.
- **No token (Yahoo, Zimbra, any server without it).** Nothing is uploaded and no local copy is made. The engine logs a warning ("… does not support managed attachments; N added and M removed attachments … are ignored"), and the event write itself still succeeds.
- **Failures** of a single upload or remove after the event write are logged at Error and skipped; the event write is not rolled back and no exception reaches the request (the event already exists on the server).

## Verification (this host)

- `dotnet build tests/MailCal.Core.Tests/MailCal.Core.Tests.csproj -c Debug -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true`: 0 errors, no warnings from T4 files.
- `dotnet test … --no-build --filter "FullyQualifiedName~CalDav|FullyQualifiedName~Imap|FullyQualifiedName~CardDav"`: **Passed 341, Failed 0**. That includes 20 new T4 tests and T2/T3 tests; the T1 baseline was 239. Earlier runs during this task showed 3 T2 scheduling tests failing while T2 was still editing them; they passed on the final run.
- `--filter FullyQualifiedName~CalDavAttachment`: 20/20.
- `dotnet build src/MailCal.Core/MailCal.Core.csproj -c Release -p:MailCalTargetPlatform=MacOS -p:NuGetAudit=false`: 0 errors. The only warnings are the existing SDK NETSDK1188 locale warnings.
- `dotnet build src/MailCal.Mail.ViewModels/…` (Debug, MacOS): 0 errors.
- Tests cover:
  - Parsing: a URI with every parameter; inline BASE64; a URI with no parameters; an overriding occurrence versus the master; garbage input.
  - Text removal keeps folded lines; the Content-Disposition format.
  - Import creates rows, keeps the downloaded copy, and removes dropped rows; inline data is written and marked downloaded.
  - An update through the ICS mutator keeps the `ATTACH;…MANAGED-ID` line.
  - Downloads: same origin sends Basic; a foreign origin sends Anonymous; foreign plain http is refused; the size limit throws; a local account copies the stored file.
  - Create:
    - attachment-add shape: the path; query `action=attachment-add`; Content-Type `text/plain`; disposition `attachment` with filename `note.txt`; a non-empty If-Match; the body.
    - A full answer causes no GET; the snapshot ETag equals the server's; the row is `mid-1`; the next sync downloads nothing.
    - An iCloud-style bare answer: GET read-back, the managed ID found by diff, an anonymous download from the foreign attachment host.
  - Update:
    - remove through attachment-remove: the query carries `managed-id`; no ATTACH and no rows remain; the ETag is stored.
    - a server that ignores remove: PUT fallback, and the summary is kept.
    - keeping every attachment sends no action.
  - A server without the token: event created, no POST, no ATTACH, no rows, capability false.

## Live evidence (scratchpad lab `t4lab/`, outside the repository; passwords and user names redacted, never printed)

**iCloud** (DAV header has `calendar-managed-attachments`, `SupportsAttachments = True`):
- Create through the engine with a picked 52-byte text file:
  ```
  PUT  p40-caldav.icloud.com/…/calendars/<cal>/mailcal-t4-….ics auth=Basic -> 201 ETag
  POST …/mailcal-t4-….ics?action=attachment-add auth=Basic If-Match [attachment; filename="mailcal-t4-note.txt"] -> 200 Location-host=gateway.icloud.com
  GET  …/mailcal-t4-….ics auth=Basic -> 200 ETag
  ```
  iCloud returns neither an ETag, `Cal-Managed-ID` nor a body; the engine reads the resource back. Server ETag == stored ETag: True.
- Server resource after upload: `ATTACH;FMTTYPE=text/plain;SIZE=52;FILENAME=mailcal-t4-note.txt;MANAGED-ID=bd94730c-363b-49fa-9097-14db3d508152:https://gateway.icloud.com/…(opaque 499-char path)`. Row: RemoteAttachmentId = that MANAGED-ID, FileName `mailcal-t4-note.txt`, Size 52, `text/plain`.
- Download through the engine: `GET gateway.icloud.com/caldav/… auth=Anonymous -> 200`. The attachment host is a different origin from the CalDAV host, so no credentials are sent; the URL is a capability URL and works without them (also checked with curl). 52 bytes, **byte-equal to the uploaded file: True**; row marked downloaded.
- Remove (update with an empty list):
  ```
  PUT  … If-Match -> 204 ETag                       (the event update itself; ATTACH survives the mutator)
  POST …?action=attachment-remove&managed-id=bd94730c-… If-Match -> 200
  GET  … -> 200 ETag                                (attachment still there)
  PUT  … If-Match -> 204 ETag                       (resource without the ATTACH line)
  ```
  Afterwards the server has 0 ATTACH lines and there are 0 rows; the following sync succeeds. **Server fact: iCloud answers attachment-remove with 200 but keeps the attachment** (reproduced with curl three times, with and without If-Match). It removes it when the ATTACH line is dropped by PUT. iCloud also ignores If-Match on attachment-add (a bogus ETag was accepted).
- Import on iCloud: an event PUT with a plain `ATTACH;FMTTYPE=text/plain;FILENAME=rfc8607.txt:https://www.rfc-editor.org/rfc/rfc8607.txt` keeps the line. After sync there is 1 row (RemoteAttachmentId = the URI, `text/plain`, Size 0). The download is `GET www.rfc-editor.org/rfc/rfc8607.txt auth=Anonymous -> 200`: 66,117 bytes, and the text contains "Managed Attachments".
- All lab events were deleted (engine `DeleteEventAsync` / If-Match DELETE). A curl probe event (`mailcal-t4-probe-*`) was deleted too: 204, then GET 404. A final PROPFIND sweep of all 4 iCloud event calendars found no `mailcal-t4` resource.

**Yahoo** and **OVH Zimbra**:
- DAV header has no `calendar-managed-attachments`; `CalendarCapabilities(calendar).SupportsAttachments = False` on both.
- **Server fact: both strip ATTACH entirely on PUT.** A URI with parameters, a bare URI and inline `VALUE=BINARY;ENCODING=BASE64` were each PUT (201) and then read back with GET: no ATTACH line remained. So "import of an ATTACH URI produces a row and a download" cannot happen on these servers through CalDAV: the lab sync produced 0 rows. That path was verified live on iCloud instead (above) and on the fake server in tests. Zimbra may still emit ATTACH for attachments added in its own web UI; that is unverified, because the web UI cannot be used from here.
- An update through the engine carrying a kept list plus one new file sends exactly one request (`PUT … If-Match -> 204`) and logs `CalDAV server of "t4-YAHOO|t4-ZIMBRA" does not support managed attachments; 1 added and 0 removed attachments of "mailcal-t4-…" are ignored`. No POST is sent and nothing is uploaded or copied locally.
- Lab events deleted with If-Match; the final PROPFIND sweep found no `mailcal-t4` resource on either calendar.
- During the first sweep a `mailcal-t2-lab-*` resource (T2's lab) was visible. It was gone in the next sweep; I did not touch it.

## Unfinished / handoff

- Windows Debug x64 build, the Windows Release x64 AOT gate (`dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`), macOS Debug arm64/x64 builds, and manual UI checks (compose with attachment on iCloud, open the attachment in event details on iCloud, local and imported-URI events) are pending.
- Events imported before this change get attachment rows only when their resource changes (unchanged ETags skip `ManageCalendarEventAsync`); a re-import of the calendar fills them at once.
- Upload/remove failures after a successful event write are only logged; there is no user-facing message (no translation key added). A later item can surface them if wanted.
- Upload limit 50 MB (request bodies are buffered in memory by `DavTransport`), download limit 100 MB; RFC 8607 `max-attachment-size` / `max-attachments-per-resource` are not discovered (would need `CalDavClient.Sync.cs`, owned by T1/T3).
- `DavTransport` refuses cross-origin redirects; an attachment URL that redirects to another host fails with `DavRequestException` (not seen on any of the three servers).
- Rows rebuilt by the engine after upload/remove pass no attachment folder, so a downloaded copy of a removed managed attachment stays on disk (import-side removal does delete copies under the attachment folder).

## ViewModel hooks for T2 / a later item

1. `CalendarEventComposePageViewModel`: allow attachments when `CalendarCapabilities.SupportsAttachments && !IsReadOnly` for the selected calendar. The picked files already flow `ComposeResult.Attachments → CreateCalendarEventRequest → CalDavCalendarOperationHandler → engine`; no further plumbing is needed for create. The old `Attachments = CanAddAttachments ? … : []` stays correct.
2. Editing attachments of an existing CalDAV event (no UI does this today): whoever builds `UpdateCalendarEventRequest` (`MailCalRequestDelegator`, line ~192) must set the new `Attachments` init property to the complete wanted list. Kept attachments are drafts with `Id = CalendarAttachment.Id`; new ones have a `FilePath`. Leaving it null keeps today's behavior.
3. `EventDetailsPageViewModel.DownloadAttachmentAsync` works unchanged for CalDAV and local accounts. The engine also marks the row downloaded, so the VM's own `MarkAttachmentDownloadedAsync` is now redundant but harmless. The VM's download folder uses `item.Id.ToString()` (D format) while import writes inline data under `itemId:N`; this is cosmetic only.
