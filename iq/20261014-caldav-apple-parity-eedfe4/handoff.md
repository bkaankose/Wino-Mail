# CalDAV parity work: handoff for Windows and macOS

Work record: `iq/20261014-caldav-apple-parity-eedfe4.md`. Reports: `T1-report.md` (engine, incremental sync, capabilities), `T2-report.md` (ViewModels, scheduling identity and PARTSTAT replies), `T3-report.md` (SRV discovery, default alarms, server limits), `T4-report.md` (attachments). All shared-code work is complete and verified on the Linux host; nothing is committed.

## What a Windows or Mac host still has to run

1. Windows Debug x64 build:
   `dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64`
2. Windows Release x64 gate (Native AOT, trimming, warnings as errors), no launch:
   `dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false`
   Note: on the Linux host the Release restore only succeeds with `-p:NuGetAudit=false` because of the pre-existing advisories NU1902/NU1903 for `System.Security.Cryptography.Pkcs` 10.0.0; that is unrelated to this work.
3. macOS Debug arm64 and x64 builds of `src/MailCal.Mail.MacOS/MailCal.Mail.MacOS.csproj` (see `docs/harness/development-macos.md`).
4. Windows and macOS test projects as usual (`tests/MailCal.Core.Tests`, `tests/MailCal.Mail.ViewModels.Tests`); on Linux the same 13 Core tests fail before and after this work (culture, Unicode normalization and Windows-path tests under invariant globalization).

## Head bindings (no head was edited; ViewModel properties already exist)

Compose page (`Views/Calendar/CalendarEventComposePage.xaml`; Mac `CalendarEventComposePageViewController*.cs`):
- Existing bindings keep working with the new capability-driven meaning: `CanAddAttachments`, `AttachmentsDisabledTooltipText`, `SaveButtonText`, `CanAddOnlineMeeting`.
- New, optional: info bar `HasInvitationsNotSentWarning`/`InvitationsNotSentText`; attendee hint `HasAttendeeLimit`/`AttendeeLimitText`; disable attendee entry with `CanAddAttendees` and `IsAttendeeLimitReached`; warning bar `HasQuotaWarning`/`QuotaWarningText`; `IsCalendarReadOnly`.

Event details (`EventDetailsPage.xaml`; Mac `EventDetailsPageViewController.cs`):
- RSVP buttons `IsEnabled=CanRespondToInvitation`; Save and Delete `!IsCalendarReadOnly`; note `HasResponseNotSentWarning`/`ResponseNotSentText`.

Calendar account settings (`CalendarAccountSettingsPage.xaml`; Mac `CalendarAccountSettingsPageViewController.cs`):
- "Server features" card `HasCapabilities`/`CapabilitySummaryText`; optional rows `SupportsAttachments`, `SendsInvitations`, `AttendeeLimitText`, `QuotaAvailableText`, `QuotaWarningText`.

Denied actions are already refused inside the ViewModels with translated info-bar messages, so the heads only need the bindings above; nothing breaks if they are added later.

## Manual checks after the bindings exist

- iCloud account: Add attachment is enabled on a writable calendar; a file attached in compose appears on the server (`ATTACH;MANAGED-ID=`) and downloads from event details.
- Yahoo account: compose shows "Save" with attendees and the "invitations not sent" note; Add attachment stays disabled with the capability tooltip.
- Zimbra account: attendee limit text absent (server reports none), invitations note absent (server advertises auto-schedule).
- Accept an invitation received on iCloud or Zimbra: only the user's PARTSTAT changes on the server; the organizer receives the reply from the server.
- A read-only (shared) calendar disables RSVP, Save and Delete.
- A new event in a calendar with a server default alarm gets the default reminder; a server over quota or with too many attendees shows the translated DAV error.
- Account setup: entering only an icloud.com or yahoo.com address resolves the CalDAV URL through DNS SRV without a manual URL.

## Behavior changes to know about

- One failing calendar no longer fails the whole calendar sync (partial result); 401 still fails it.
- `ExecuteRequests` and single-calendar syncs compare ETags instead of re-downloading the window.
- Failed endpoint auto-discovery is retried after 30 minutes.
- Local calendars with attendees show "Save" instead of "Send"; Add attachment is disabled until a calendar is selected.
- Imported invitations the user has not answered now show as not responded; ones the user declined elsewhere are hidden (same as a decline made in Mail-Cal).
- Yahoo and Zimbra strip ATTACH on write, so attachments never reach those servers; the capability reports no support there.
- Events imported before this change gain attachment rows only when they next change on the server.

## Known limits

- iCloud answers attachment add/remove without ETag or body; the engine reads the event back. Upload is capped at 50 MB (in-memory body), download at 100 MB.
- Attachment upload or removal failures after the event saved are logged only.
- `SendsInvitations` is derived from the `calendar-auto-schedule` DAV token.
- Servers without both ctag and sync-token still re-import on every sync, as before.
- Push, sharing, notification collection, scheduling inbox/outbox, free/busy and VTODO are out of scope by decision.
