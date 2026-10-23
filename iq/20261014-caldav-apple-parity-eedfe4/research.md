# CalDAV parity research: Mail-Cal vs. Apple Calendar

Date: 2026-10-14. Probed live with the three test accounts (credentials are not stored here).
Servers: iCloud (`https://caldav.icloud.com/`), Yahoo (`https://caldav.calendar.yahoo.com/`),
OVH Zimbra for `adastria.ws` (`https://zimbra1.mail.ovh.net/`, principal `/principals/users/<address>/`).

## What the servers advertise

| Capability | iCloud | Yahoo | OVH Zimbra (adastria.ws) |
| --- | --- | --- | --- |
| DAV header | calendar-access, calendar-schedule, calendar-auto-schedule, calendar-default-alarms, calendar-managed-attachments, calendarserver-sharing, calendarserver-subscribed | none returned on OPTIONS | none returned on OPTIONS |
| `sync-collection` REPORT | yes (per calendar, home, inbox, notification) | **no** (an unsupported REPORT returns an empty 207, not an error) | **no** |
| `calendar-multiget` | yes | yes | yes |
| `calendar-query` | yes | yes | yes |
| `free-busy-query` REPORT | yes | yes (listed) | yes (on outbox and calendar) |
| `sync-token` / `getctag` | both | ctag only (`1-1`), no sync-token | ctag only (`1-1`) |
| Scheduling inbox / outbox | `/calendars/inbox/`, `/calendars/outbox/` | `/dav/<id>/Inbox/`, `/Outbox/` | `/dav/<addr>/Inbox/`, outbox is `/Sent/` |
| `calendar-user-address-set` | 2 principal hrefs, `urn:uuid:<id>`, `mailto:` for icloud.com and outlook.com addresses | **`mailto:null`** (broken; must be ignored) | `mailto:<address>` + principal href |
| Notification collection (sharing invites) | `/calendars/notification/` | advertised but 404 | not advertised |
| VTODO collections | `tasks` ("Reminders", VTODO only) | the only calendar accepts VEVENT and VTODO | separate `Tasks` collection (VEVENT absent, VTODO) |
| Shared calendars | one calendar with `shared-owner` resourcetype and `CS:invite` sharees; `allowed-sharing-modes` | `allowed-sharing-modes: can-be-shared` | not advertised |
| Proxy delegation (`calendar-proxy-*-for`) | not returned for this principal | no | no |
| Managed attachments (RFC 8607) | yes | no | no |
| Quota | `quota-available-bytes` per collection, `max-attendees-per-instance` 300, no `max-resource-size` | `max-resource-size` 0 (meaning unlimited) | none |
| Default alarms | `calendar-default-alarms` in DAV header; no values on this account | no | no |
| Push | `push-transports` = Apple APNs only (needs an Apple entitlement; unusable by Mail-Cal) | no | no |
| `Prefer: return=minimal` | no `Preference-Applied` | no | no |
| DNS SRV `_caldavs._tcp` | `0 0 443 caldav.icloud.com` | `1 1 443 caldav.calendar.yahoo.com` | none; `/.well-known/caldav` 404; MX points to OVH; endpoint is only reachable through the provider catalog or manual entry |

Mail-Cal today (`src/MailCal.Services/CalDavClient.cs`, `ImapSynchronizer.SynchronizeCalendarEventsInternalAsync`):
PROPFIND home with ctag/sync-token, then one `calendar-query` with `calendar-data` for the whole
three-year window whenever the calendar token changed; VTODO collections are skipped
(`SupportsEvents == false`); events are written with PUT/If-Match; no scheduling, inbox,
free/busy, sharing, attachments, SRV discovery, quota or default-alarm handling.

## Gap list with the chosen approach

1. Incremental sync: `sync-collection` + `calendar-multiget` where advertised (iCloud); otherwise a
   `calendar-query` that asks for `getetag` only and a multiget of the changed hrefs (Yahoo, Zimbra).
   Per-resource href/ETag knowledge already exists in the ICS file store (`event.meta.json`), so no
   database change is needed. Re-anchoring the window re-expands recurrences from the stored ICS files.
2. Scheduling (RFC 6638): all three servers run implicit scheduling. Mail-Cal must (a) use an address
   from `calendar-user-address-set` as ORGANIZER / to find its own ATTENDEE, (b) reply by changing its
   own `PARTSTAT` instead of the event `STATUS`, (c) read and clear the schedule inbox after sync.
3. VTODO: separate collections on iCloud and Zimbra, mixed on Yahoo. Map to the existing task tables
   through `RemoteId`/`RemoteVersion`/`TaskDeltaLink` and a new `TaskSourceKind.CalDav` enum value.
4. Push: only Apple APNs on iCloud; WebDAV-Push is not offered by any of the three. Not feasible
   without a relay service Mail-Cal does not operate. Polling stays, but it becomes a cheap home PROPFIND
   plus deltas after item 1.
5. Free/busy: POST a `VFREEBUSY` request to the schedule outbox (all three advertise one).
6. Sharing and delegation: iCloud exposes sharees (`CS:invite`), sharing modes and a notification
   collection for invitations (`CS:invite-notification`, reply with `CS:invite-reply`). Proxy
   principals (`calendar-proxy-read-for` / `write-for`) are the delegation mechanism on
   CalendarServer-family servers; follow them to additional calendar homes.
7. Attachments: RFC 8607 managed attachments on iCloud only (`POST ?action=attachment-add`,
   `MANAGED-ID`); other servers keep `ATTACH` URIs that can only be downloaded.
8. Discovery: SRV records resolve iCloud and Yahoo without a URL; TXT `path` (RFC 6764) must be
   honoured when present. Zimbra at OVH needs the provider catalog or manual entry.
9. Small items: `Prefer: return=minimal` on REPORT/PROPFIND; `CS:default-alarm-vevent-datetime`
   and `-date`; `quota-available-bytes` / `max-resource-size` / `max-attendees-per-instance` checks
   before PUT.

## Constraints that shape the design

- No database schema change: no new table, column, schema version, or `__MigrationMetadata` entry.
  New CalDAV state (principal, home, inbox/outbox, address set, per-calendar capabilities and sync
  checkpoints, default alarms, quota) lives in a JSON file beside the ICS store
  (`CalendarIcs/<account>/caldav-state.json`) and is a rebuildable cache.
- `AccountCalendar.SynchronizationDeltaToken` keeps the `win=YYYYMMDD|<sync-token>|<ctag>` format so
  existing rows decode and existing calendars continue without re-import.
- Enum additions (`TaskSourceKind.CalDav`) are value additions, not schema changes.
- Existing users keep their settings: CalDAV tasks, scheduling, sharing and attachments switch on
  from server capabilities when the calendar mode is already CalDAV.
