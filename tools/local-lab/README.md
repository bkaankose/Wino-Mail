# Mail-Cal local Docker lab

Five IMAP servers with different synchronization capabilities, each with SMTP and CalDAV/CardDAV, running in Docker Desktop on your Windows computer.
Every account holds about 150-180 generated messages (threads, attachments, inline images, calendar invitations, flags, folders), two calendars, and two address books.
Mail flows between all five servers inside Docker. Nothing talks to a real provider.

One command starts everything and generates a Mail-Cal database with the five accounts; a second command installs that database into the Debug package and launches Mail-Cal.

Every password in the lab is `1`: mailboxes, DAV logins, the Stalwart admin console, the Baïkal admin, the James WebAdmin header, and the encrypted backup.

## Start the lab

1. Start Docker Desktop in Linux-container mode.
2. Install the .NET SDK selected by the repository `global.json`.
3. Open PowerShell as the Windows user who runs Mail-Cal and run, from the repository root:

```powershell
.\scripts\lab\windows\local-lab.ps1 up
```

`up` pulls the pinned images, creates the mailboxes, starts nine containers, waits until every endpoint accepts the lab credentials, seeds mail/calendars/contacts, verifies the advertised IMAP capabilities, sends a delivery probe between every pair of accounts, and generates the client artifacts.
The first run takes several minutes (image downloads, James and Stalwart start-up, ~850 messages).

Then install the artifacts into Mail-Cal:

```powershell
.\scripts\lab\windows\local-lab.ps1 install-app
```

`install-app` builds and registers the Debug x64 package through the normal F5 helper, closes it, moves any previous `MailCal210.db` and DAV credentials of that package into `tools/local-lab/app-backups/<timestamp>`, copies the generated database and credentials into the package `LocalState`, copies `MailCal210.db`, `mailcal-lab-backup.mailcalsnap`, and `manifest.json` to `Desktop\mailcal-lab`, and launches Mail-Cal.
It is the only command that touches application data.

## Servers and accounts

| # | Service | Implementation | Account | IMAP | SMTP | CalDAV/CardDAV | Expected Mail-Cal strategy |
|---|---|---|---|---|---|---|---|
| 1 | `dovecot` | docker-mailserver 16.0.1 (Dovecot 2.4 + Postfix), stock capabilities. Also the routing hub. | `alice@dovecot.test` | `127.0.0.1:1143` | `127.0.0.1:1587` | `http://127.0.0.1:8800/dav.php/` (Baïkal, login `alice`) | QRESYNC |
| 2 | `condstore` | docker-mailserver 16.0.1 with `QRESYNC` removed from CAPABILITY | `bob@condstore.test` | `127.0.0.1:2143` | `127.0.0.1:2587` | `http://127.0.0.1:8802/` (Radicale, login `bob`) | CONDSTORE |
| 3 | `stalwart` | Stalwart v0.13 (IMAP4rev2, JMAP, built-in DAV) | `carol@stalwart.test` | `127.0.0.1:3143` | `127.0.0.1:3587` | CalDAV `http://127.0.0.1:8803/dav/cal/`, CardDAV `http://127.0.0.1:8803/dav/card/` (login `carol@stalwart.test`) | QRESYNC |
| 4 | `greenmail` | GreenMail 2.1.8 (`LITERAL+ UIDPLUS SORT IDLE MOVE QUOTA`) | `dave@greenmail.test` | `127.0.0.1:4143` | `127.0.0.1:4025` (hub port 25, no authentication) | `http://127.0.0.1:8804/` (Radicale, login `dave`) | UID only |
| 5 | `james` | Apache James 3.9.1, memory backend | `eve@james.test` | `127.0.0.1:5143` | `127.0.0.1:5587` | `http://127.0.0.1:8805/` (Radicale, login `eve`) | QRESYNC (probed) |

All connections are plaintext (`ImapConnectionSecurity.None`) with password authentication, except the GreenMail account's outgoing server, which is the hub's unauthenticated port.
Published ports bind only to `127.0.0.1`.

Other endpoints:

| Endpoint | Purpose |
|---|---|
| `http://127.0.0.1:8803/` | Stalwart web admin (`admin` / `1`) |
| `http://127.0.0.1:8800/admin/` | Baïkal admin (`admin` / `1`) |
| `http://127.0.0.1:5800/` | James WebAdmin REST (send header `Password: 1`) |
| `http://127.0.0.1:4080/` | GreenMail API |

Capability notes:

- Dovecot replaces its whole CAPABILITY list when `imap_capability` is set, so the `condstore` container reads the stock list from the `dovecot` container at start-up and removes `QRESYNC`. A `uidonly` role (removes `CONDSTORE` too) exists in `provision/mailserver/user-patches.sh` if you want a second UID-only server.
- Mail-Cal picks QRESYNC, then CONDSTORE, then UID-only purely from the advertised capabilities (`UnifiedImapSynchronizer.DetermineSyncStrategy`).
- James advertised `QRESYNC` on the pinned image; the verify step prints the real list every run.
- Folder separators: `.` on Dovecot, GreenMail, and James; `/` on Stalwart. Stalwart and James advertise SPECIAL-USE; GreenMail does not, so Mail-Cal resolves its role folders by name.

## Mail routing

- Each server owns one domain. The hub (`dovecot`'s Postfix) has a transport map that sends `condstore.test`, `stalwart.test`, `greenmail.test`, and `james.test` to the right container. Any other domain is refused.
- `condstore` relays everything non-local to the hub. Stalwart has a relay route to the hub. James's `RemoteDelivery` uses the hub as gateway. GreenMail cannot relay, so its Mail-Cal account submits through the hub's port 25 (published as `4025`), which trusts the Docker network.
- `verify` sends 20 probes (every account to every other account) through each account's own submission endpoint and waits for them to arrive, then deletes them.

## Commands

| Command | Result |
|---|---|
| `up` | Start servers, provision, seed, verify, generate client artifacts |
| `status` | Container state, port reachability, artifact location |
| `provision` | Re-create the Stalwart and James domain/account (James forgets them on restart) |
| `seed` | Add missing fixtures to running servers; existing edits, moves, and deletions are preserved |
| `verify` | Print capabilities per server and run the delivery probes |
| `generate-db` | Regenerate `MailCal210.db`, DAV credentials, and the backup from the saved fixture manifest |
| `install-app` | Install the generated database into the Debug package and launch Mail-Cal |
| `stop` | Stop services, keep volumes |
| `reset -Force` | Delete this lab's volumes and `.state`, then run `up` |
| `logs [-Service <name>]` | Last 100 log lines |
| `help` | Command summary without Docker |

The output directory is `tools/local-lab/generated`:

| Artifact | Purpose |
|---|---|
| `MailCal210.db` | Five configured IMAP accounts with CalDAV and CardDAV enabled, no cached mail |
| `credentials/dav/<account-id>.bin` | DAV passwords encrypted with Windows CurrentUser DPAPI |
| `mailcal-lab-backup.mailcalsnap` | Encrypted backup in Mail-Cal's own format (Settings > Backup & restore), password `1`. Backups never carry passwords, so restoring asks for `1` per account |
| `manifest.json` | Account IDs, endpoints, capabilities, counts |

`.state` holds the anchor date, per-account mail ledgers, the seed catalog, and `fixtures.json`. `generated`, `.state`, and `app-backups` are ignored by Git.

## Generated content

Each account gets a deterministic set (seeded by account name, so reruns are identical):

- 24 conversation threads of 2-6 messages with `In-Reply-To`/`References`, quoted replies, some from the account itself (filed in Sent, `\Answered` on the replied message).
- 30 single messages: notifications (GitHub, Jira, Sentry, ...), personal mail, random attachments, inline images, high/low priority.
- 10 newsletters (`Newsletters`), 5 junk, 10 archived, 4 in Trash, 6 in `Projects/Release`, 3 Unicode-subject messages in `Zażółć-日本語`, 3 drafts with Bcc.
- 8 calendar messages: five `REQUEST` invitations, one updated `REQUEST`, one `CANCEL`, one `REPLY`; half as a `text/calendar` alternative part, half as an `.ics` attachment.
- One 2-5 MiB attachment and one message carrying every attachment type (txt, csv, json, png, zip, pdf, Unicode and duplicate names).
- Plain singles to reach at least 150 messages. Dates are spread over the last 120 days.
- Calendars `personal` and `work`: meeting with attendees and alarm, past/today/future, all-day, multi-day, floating and zoned times, daily/weekly/monthly/yearly recurrences, EXDATE/RDATE, moved and cancelled occurrences, a two-year-old weekly series, DST cases, a cancelled event, and 12 random appointments.
- Address books `contacts` (the other four lab people, a rich vCard with photo, Unicode, duplicates, a group) and `team` (eight external people).

Message-IDs are `<key.NNNN@mailcal.lab>`. Invitations use the other lab accounts as organizers and attendees, so replies travel between servers.

## Verification and troubleshooting

`up` fails loudly on a capability mismatch or an undelivered probe. Useful checks:

```powershell
.\scripts\lab\windows\local-lab.ps1 status
.\scripts\lab\windows\local-lab.ps1 verify
.\scripts\lab\windows\local-lab.ps1 logs -Service james
```

| Symptom | Action |
|---|---|
| Docker engine unavailable or wrong mode | Start Docker Desktop in Linux-container mode; the script also looks for Docker under `%LOCALAPPDATA%\Programs\DockerDesktop` |
| Port occupied | Free the port listed in the error (all lab ports are in the table above) |
| Readiness timeout | `logs -Service <name>`; James needs about a minute on first start |
| James account or mail missing after a restart | James uses the memory backend. Run `up` (or `provision` then `seed`) again; Mail-Cal resynchronizes the account |
| Stalwart warnings about "database key defined in local configuration" | Expected; the lab keeps its settings in `provision/stalwart/config.toml` |
| Generator build error | Install the SDK selected by `global.json` |
| DAV password unavailable in Mail-Cal | Run `generate-db` as the Windows user who runs Mail-Cal, then `install-app` |
| Old sync state after `reset` | Run `install-app` again so Mail-Cal starts from the fresh database |

Server state lives in Compose-owned named volumes (`mailcal-local-lab_*`). `reset -Force` deletes only those.

## Implementation references

- [Compose configuration](compose.yaml)
- [docker-mailserver provisioning and capability patch](provision/mailserver/user-patches.sh)
- [Stalwart configuration](provision/stalwart/config.toml)
- [James configuration overrides](provision/james/)
- [Radicale configuration](provision/radicale/config), [Baïkal provisioning](provision/baikal/dav.php)
- [Seed, provisioning, and verification tool](seed/seed.py)
- [Database and backup generator](DatabaseGenerator/Program.cs)
- [PowerShell entry point](../../scripts/lab/windows/local-lab.ps1)

The generator uses `DatabaseService`, the current entity mappings, `DatabaseSchemaService`, `DavCredentialStore`, `SyncSnapshotKeyService`, and `SyncSnapshotCryptography` from Mail-Cal. No production application code changes.
