# Wino local Docker lab

This lab runs Dovecot/Postfix and Baïkal on your Windows computer through Docker Desktop.
It creates three users and a repeatable collection of mail, calendar, and contact fixtures.
It also generates a fresh Wino account database and DAV credential files for your current Windows user.

Wino downloads the content from the servers through normal synchronization.
The generated database contains account configuration, preferences, signatures, aliases, and DAV discovery state.
It contains no cached mail, events, contacts, or synchronization tokens.
The scripts never copy files into an installed Wino application.

## Start the lab

1. Start Docker Desktop in Linux-container mode.
2. Install the .NET SDK selected by the repository `global.json`.
3. Open Windows PowerShell 5.1 or PowerShell 7 as the Windows user who runs Wino.
4. From the repository root, run this command:

```powershell
.\scripts\local-lab.ps1 up
```

The first run downloads the pinned container images and restores the generator dependencies.
Mail and DAV provisioning run inside containers. The Windows generator uses the current Wino entities and database initialization.
The command waits for authenticated IMAP, SMTP, and DAV connections before it creates the client artifacts.
Readiness has a 180-second limit after image downloads and server startup.

The output directory is `tools/local-lab/generated`:

| Artifact | Purpose |
|---|---|
| `Wino210.db` | Three configured IMAP accounts with CalDAV and CardDAV enabled |
| `credentials/dav/<account-id>.bin` | DAV passwords encrypted with Windows CurrentUser DPAPI |
| `manifest.json` | Account IDs, endpoints, fixture credentials, seed date, and fixture catalog |

Generated artifacts and `.state` are ignored by Git. They are intended for your own local installations.
The database and manifest contain the known local fixture passwords.
The credential files work for the same Windows user on the same computer.
No administrator privileges or Python installation are required on Windows.
Commands that change the lab run one at a time. A concurrent command stops with an actionable error.

## Accounts and endpoints

All fixture accounts use the password `WinoLab123!`.

| Account | Mail login | DAV login | Content |
|---|---|---|---|
| Alice Lab | `alice@wino.test` | `alice` | 25 messages, 19 calendar resources, 6 contact resources |
| Bob Lab | `bob@wino.test` | `bob` | 25 messages, 19 calendar resources, 6 contact resources |
| Empty Lab | `empty@wino.test` | `empty` | Empty mail folders, calendar, and address book |

Alice and Bob each have `personal` and `work` calendars, plus `contacts` and `team` address books.
The empty account has a `personal` calendar and a `contacts` address book.
These counts describe the named fixtures. Recurring resources contain multiple occurrences.

| Service | Windows endpoint | Connection |
|---|---|---|
| IMAP | `127.0.0.1:1143` | Password authentication, no TLS |
| SMTP submission | `127.0.0.1:1587` | Password authentication, no TLS |
| CalDAV/CardDAV | `http://127.0.0.1:8800/dav.php/` | HTTP Basic authentication |
| Baïkal administration | `http://127.0.0.1:8800/admin/` | User `admin`, fixture password |

Calendar homes use `/dav.php/calendars/<dav-login>/`.
Address book homes use `/dav.php/addressbooks/<dav-login>/`.
The generated configuration uses the DAV discovery endpoint instead of hard-coded collection IDs.

Published ports bind only to `127.0.0.1`. Plaintext connections are intentional for this local lab.
SMTP delivers between the local accounts. External delivery and relay are disabled.
Baïkal invitation email delivery is disabled. Event fixtures still contain organizers and attendees.
Seeded attendees use `SCHEDULE-AGENT=CLIENT` to prevent automatic invitation copies between the local calendars.

## Commands

Run these commands from the repository root. The script resolves its own paths, so another working directory also works.

| Command | Result |
|---|---|
| `.\scripts\local-lab.ps1 up` | Start servers, create missing users and fixtures, generate fresh client artifacts |
| `.\scripts\local-lab.ps1 status` | Show container state, port reachability, and artifact location |
| `.\scripts\local-lab.ps1 seed` | Add missing fixtures to running servers |
| `.\scripts\local-lab.ps1 generate-db` | Generate fresh client artifacts from the saved fixture manifest |
| `.\scripts\local-lab.ps1 stop` | Stop both servers and preserve their volumes |
| `.\scripts\local-lab.ps1 reset -Force` | Delete this lab's server volumes and seed state, then rebuild the baseline |
| `.\scripts\local-lab.ps1 logs` | Show the last 100 log lines from both servers |
| `.\scripts\local-lab.ps1 logs -Service mail` | Show mail logs only |
| `.\scripts\local-lab.ps1 logs -Service dav` | Show DAV logs only |
| `.\scripts\local-lab.ps1 help` | Show command help without requiring Docker |

`generate-db` requires a successful previous seed. It does not require running containers.
It builds the generator in Debug/x64. The resulting database also works with compatible Release and Store builds.
The artifact directory is replaced only after database and credential checks succeed.

Repeated `up` and `seed` calls preserve existing resources and do not append duplicate fixture messages.
A mail ledger preserves flag changes, moves, and deletions after successful seeding.
DAV creation uses `If-None-Match: *`, so existing event and contact edits remain intact.
Deleted DAV fixture resources are recreated by `seed`. Use `up` without another reset to retain the saved date anchor.
Repeated `up` regenerates the client database. It does not regenerate server content that already exists.

`reset` requires `-Force`. It deletes only the Compose project `wino-local-lab` volumes and this lab's `.state` directory.
It does not delete installed Wino data. Reset also replaces the generated baseline after successful generation.
The new date anchor uses the current date in `Europe/Warsaw`.

## Use the generated database in Wino

Database compatibility follows the generating checkout's schema and entity definitions.
An older release can have different fields or behavior, even if its database filename matches.
Debug, Release, and Store installations can use these artifacts if their database schema is compatible.

1. Close Wino and its background process.
2. Find the package family name of the installation that you want to use:

```powershell
Get-AppxPackage *Wino* | Select-Object Name, PackageFamilyName, Version
```

3. Open `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState`.
4. Make sure that this installation has no accounts or content that you need to preserve.
5. If a fresh-install database exists, move it and its `-wal`/`-shm` files to a backup directory.
6. Copy `generated/Wino210.db` into `LocalState`.
7. Copy `generated/credentials/dav` into `LocalState/credentials/dav`.
8. Start the Docker servers with `up` if they are stopped.
9. Open Wino.
10. Start normal synchronization for mail, calendars, and contacts.

For an unpackaged installation, use its actual `ApplicationData.Current.LocalFolder` directory instead of a package path.
The current application stores the main database in its own local data directory.
The legacy publisher-cache database is not the destination for these artifacts.

The first account is Alice. All accounts use the full mail synchronization range.
Folder roles, calendars, and address books are discovered during synchronization.
Windows application preferences remain those of the target installation.
The generator does not create an offline-demo marker or change Wino activation behavior.

After a server reset, replace the client artifacts in a fresh installation again.
Old client caches can reference IMAP UIDs and DAV tokens from the previous server baseline.

## Fixture catalog

`generated/manifest.json` lists every named fixture, its owner, and its folder or collection.
Mail subjects contain `[Lab <key>]`. The thread fixtures share the subject `Lab conversation`.
Calendar titles contain `[Lab]`. Contact names start with `Lab`.

### Mail

Each populated account has these folders:
`INBOX`, `Sent`, `Drafts`, `Trash`, `Junk`, `Archive`, `Projects`, `Projects.Release`, `Empty`, and `Zażółć-日本語`.
The pinned Dovecot configuration uses `.` as the folder hierarchy separator.
`Projects.Release` is a child of `Projects`. `Empty` contains no messages.

| Fixture keys | Content |
|---|---|
| `unread`, `read` | Unread plain text and read HTML with a plain-text alternative |
| `flagged`, `flagged-read` | Flagged unread and flagged read messages |
| `answered`, `all-flags` | Answered/read and answered/flagged/read combinations |
| `deleted` | `\Deleted` and `\Seen`, retained in Trash without expunge |
| `draft`, `sent` | Draft and sent messages, sender identities and Cc. The draft also contains Bcc |
| `junk`, `archived`, `nested` | Messages in Junk, Archive, and the nested folder |
| `unicode` | Polish, Japanese, Arabic, and RTL HTML |
| `inline` | An embedded PNG referenced by Content-ID |
| `attachments` | Multiple attachments, duplicate filenames, and a Unicode filename |
| `large` | An attachment of approximately 5 MiB |
| `high`, `low` | High and low priority headers |
| `thread-1`, `thread-2`, `thread-3` | Three related messages with Message-ID, References, and In-Reply-To |
| `invite`, `update`, `cancel`, `rsvp` | Calendar attachments with REQUEST, updated sequence, CANCEL, and REPLY |

Unflagged messages provide the counterpart to the flagged fixtures.
The deleted fixture stays on the server. The Wino UI can hide deleted messages according to its synchronization behavior.

### Calendar

| Fixture keys | Content |
|---|---|
| `meeting` | Organizer, accepted/declined/tentative/needs-action attendees, required/optional roles, and an RSVP request |
| `meeting` details | Display alarm, location, multiline description, meeting URL, private visibility, and busy availability |
| `past`, `today`, `future` | UTC events seven days before, on, and sixty days after the anchor |
| `all-day`, `multiday` | One-day and three-day date events with exclusive end dates |
| `overlap` | An overlapping, public, tentative, free event |
| `floating`, `zoned` | Floating times and explicit `Europe/Warsaw` times |
| `daily`, `weekly`, `monthly`, `yearly` | Recurrence frequencies, selected weekdays, COUNT, and UNTIL |
| `exceptions` | Recurrence with EXDATE and RDATE |
| `overrides` | A moved occurrence and a cancelled occurrence with RECURRENCE-ID |
| `old-series` | An ongoing weekly series that starts two years before the anchor |
| `spring-dst`, `autumn-dst` | Zoned recurrences across both Warsaw DST changes in the anchor year |
| `cancelled` | A cancelled standalone event |

The calendar data includes a Warsaw VTIMEZONE definition.
The DST fixtures stay tied to the anchor year. One transition can fall outside the current Wino calendar display window.
Invitation fixtures describe protocol content. They do not automate attendee workflows in Wino.

### Contacts

| Fixture keys | Content |
|---|---|
| `minimal` | Minimal vCard 3.0 contact |
| `rich` | Work/home emails, cell/work phones, home/work addresses, organization, title, birthday, notes, URL, and photo |
| `unicode` | vCard 4.0, Polish/Japanese/Arabic name, telephone URI, and birthday without a year |
| `duplicate-a`, `duplicate-b` | Identical display names and a shared email, in separate address books |
| `group` | vCard 4.0 group referencing the minimal and rich contacts in the same address book |

These records are accepted by the pinned Baïkal server.
Their display in Wino depends on the capabilities of the target build.

## Troubleshooting and setup checks

| Error | Action |
|---|---|
| Docker engine unavailable | Start Docker Desktop. Run `status` again |
| Wrong container mode | Switch Docker Desktop to Linux containers |
| Port occupied | Stop the other service on port 1143, 1587, or 8800. Run `up` again |
| Readiness timeout | Run `logs -Service mail` and `logs -Service dav`. Correct the reported startup error |
| Missing fixture manifest | Run `up` before `generate-db` |
| Generator build error | Install the SDK selected by `global.json`. Read the restore/build error |
| Generated directory access error | Close programs that read the generated artifacts. Run `generate-db` again |
| DAV password unavailable | Copy the companion credential folder into the same installation as the database |
| DAV credential decryption error | Run `generate-db` as the Windows user who runs Wino. Copy the new artifacts |
| Unexpected server data | Run `reset -Force` to recreate the baseline |

For a baseline setup check, run the internal container command:

```powershell
docker compose -f tools/local-lab/compose.yaml run --rm --no-deps seed verify
```

This command checks all users, named mail fixtures and flags, calendar/contact resources, and local SMTP delivery.
It sends one probe from Alice to Bob, then deletes that probe from Bob's Inbox.
It expects an unmodified baseline. It is not a Wino test suite.

The database generator checks SQLite integrity, foreign keys, schema metadata, account counts, and current-user DAV password decryption.
It closes SQLite and checkpoints WAL before publishing the artifact directory.
No automated Wino tests or reconnect scenarios are included.

## Implementation references

- [Compose configuration](compose.yaml)
- [Server fixture generator](seed/seed.py)
- [Database generator](DatabaseGenerator/Program.cs)
- [PowerShell entry point](../../scripts/local-lab.ps1)
- [Docker Mailserver documentation](https://docker-mailserver.github.io/docker-mailserver/latest/)
- [Baïkal Docker image source](https://github.com/ckulka/baikal-docker)

Server state lives in five Compose-owned named volumes.
The seed date, mail ledger, and fixture manifest live in `.state`.
The generator uses `DatabaseService`, current entity mappings, `DatabaseSchemaService`, and `DavCredentialStore` from Wino.
There is no separate client schema snapshot and no change to production application code.
