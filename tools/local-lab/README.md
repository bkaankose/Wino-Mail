# Wino local Docker lab

Five IMAP servers with different synchronization capabilities, each with SMTP and CalDAV/CardDAV, running in Docker Desktop on your Windows computer.
Every account holds about 150-180 generated messages (threads, attachments, inline images, calendar invitations, flags, folders), two calendars, and two address books.
Mail flows between all five servers inside Docker. Nothing talks to a real provider.

One command starts everything and generates a Wino database with the five accounts; a second command installs that database into the Debug package and launches Wino.

Every password in the lab is `1`: mailboxes, DAV logins, the Stalwart admin console, the Baïkal admin, the James WebAdmin header, and the encrypted backup.

## Start the lab

1. Start Docker Desktop in Linux-container mode.
2. Install the .NET SDK selected by the repository `global.json`.
3. Open PowerShell as the Windows user who runs Wino and run, from the repository root:

```powershell
.\scripts\lab\windows\local-lab.ps1 up
```

`up` pulls the pinned images, creates the mailboxes and the lab TLS certificates, starts eleven containers (including the [OAuth server](#oauth-server-stalwart-10) and its CRL server `lab-pki`), waits until every endpoint accepts the lab credentials, seeds mail/calendars/contacts, verifies the advertised IMAP capabilities, sends a delivery probe between every pair of accounts, and generates the client artifacts.
The first run takes several minutes (image downloads, James and Stalwart start-up, ~850 messages).

Then install the artifacts into Wino:

```powershell
.\scripts\lab\windows\local-lab.ps1 install-app
```

`install-app` builds and registers the Debug x64 package through the normal F5 helper, closes it, moves any previous `Wino210.db` and DAV credentials of that package into `tools/local-lab/app-backups/<timestamp>`, copies the generated database and credentials into the package `LocalState`, copies `Wino210.db`, `wino-lab-backup.winosnap`, and `manifest.json` to `Desktop\wino-lab`, and launches Wino.
It is the only command that touches application data.

## Servers and accounts

| # | Service | Implementation | Account | IMAP | SMTP | CalDAV/CardDAV | Expected Wino strategy |
|---|---|---|---|---|---|---|---|
| 1 | `dovecot` | docker-mailserver 16.0.1 (Dovecot 2.4 + Postfix), stock capabilities. Also the routing hub. | `alice@dovecot.test` | `127.0.0.1:1143` | `127.0.0.1:1587` | `http://127.0.0.1:8800/dav.php/` (Baïkal, login `alice`) | QRESYNC |
| 2 | `condstore` | docker-mailserver 16.0.1 with `QRESYNC` removed from CAPABILITY | `bob@condstore.test` | `127.0.0.1:2143` | `127.0.0.1:2587` | `http://127.0.0.1:8802/` (Radicale, login `bob`) | CONDSTORE |
| 3 | `stalwart` | Stalwart v0.13 (IMAP4rev2, JMAP, built-in DAV) | `carol@stalwart.test` | `127.0.0.1:3143` | `127.0.0.1:3587` | CalDAV `http://127.0.0.1:8803/dav/cal/`, CardDAV `http://127.0.0.1:8803/dav/card/` (login `carol@stalwart.test`) | QRESYNC |
| 4 | `greenmail` | GreenMail 2.1.8 (`LITERAL+ UIDPLUS SORT IDLE MOVE QUOTA`) | `dave@greenmail.test` | `127.0.0.1:4143` | `127.0.0.1:4025` (hub port 25, no authentication) | `http://127.0.0.1:8804/` (Radicale, login `dave`) | UID only |
| 5 | `james` | Apache James 3.9.1, memory backend | `eve@james.test` | `127.0.0.1:5143` | `127.0.0.1:5587` | `http://127.0.0.1:8805/` (Radicale, login `eve`) | QRESYNC (probed) |

All connections are plaintext (`ImapConnectionSecurity.None`) with password authentication, except the GreenMail account's outgoing server, which is the hub's unauthenticated port, and the OAuth server below, which is TLS only.
Published ports bind only to `127.0.0.1`.

## OAuth server (Stalwart 1.0)

A sixth mail server, `stalwart-oauth`, runs Stalwart v1.0.1 with its built-in OAuth authorization server for the generic OAuth work ([design](../../docs/generic-oauth-design.md)).
The `stalwart` service stays on v0.13 as the password regression target.
This server is not part of `Wino210.db`, the backup, the delivery probes, or the DAV seed; add it in Wino through the IMAP setup.

| Item | Value |
|---|---|
| Account | `frank@stalwartoauth.test`, password `1` (3 seeded messages in INBOX) |
| Host | `mail.stalwartoauth.test` (needs `trust`, below) |
| IMAP | `mail.stalwartoauth.test:6993`, implicit TLS (container 993) |
| SMTP | `mail.stalwartoauth.test:6465` implicit TLS, `mail.stalwartoauth.test:6587` STARTTLS |
| SASL | IMAP `AUTH=PLAIN AUTH=OAUTHBEARER AUTH=XOAUTH2`; SMTP `PLAIN LOGIN XOAUTH2 OAUTHBEARER`. Stalwart offers OAuth mechanisms only over TLS |
| Issuer | `https://mail.stalwartoauth.test` (port 443 on 127.0.0.1; the issuer has no port, so 443 must be free) |
| Metadata | `https://mail.stalwartoauth.test/.well-known/oauth-authorization-server` and `/.well-known/openid-configuration` |
| Authorization | `https://mail.stalwartoauth.test/login` (PKCE `S256` only; the response carries `iss`) |
| Token, registration, revocation | `/auth/token`, `/auth/register`, `/auth/revoke`, also `/auth/introspect`, `/auth/device` |
| Scopes | `urn:ietf:params:oauth:scope:mail` is required for IMAP/SMTP; add `offline_access` for a refresh token |
| Web admin | `https://mail.stalwartoauth.test/admin` (`admin` / `1`); management API also on `http://127.0.0.1:8806/` |
| CRL and CA certificate | `http://127.0.0.1:8807/lab-ca.crl`, `http://127.0.0.1:8807/lab-ca.crt` (`lab-pki` service) |

### Trust the lab CA once

```powershell
.\scripts\lab\windows\local-lab.ps1 trust
```

`up` creates a lab CA and the server certificate in `tools/local-lab/tls` (ignored by Git, kept across `reset`).
`trust` adds `ca.crt` to the current user's Trusted Root store (Windows asks for confirmation) and adds `127.0.0.1 mail.stalwartoauth.test stalwartoauth.test # wino-local-lab` to the hosts file (UAC prompt).
The CA is name-constrained to `*.test`, `localhost`, and `127.0.0.0/8`, so it cannot vouch for real sites.
`untrust` removes both. Delete `tools/local-lab/tls` only together with `untrust`; the next `up` then issues a new CA that must be trusted again.
Revocation can be checked online: the server certificate names a CRL distribution point `http://127.0.0.1:8807/lab-ca.crl` and AIA caIssuers `http://127.0.0.1:8807/lab-ca.crt`.
The `lab-pki` container serves both from `tls/pub`; the CRL is empty, signed by the lab CA, and valid for ten years.
`up` renews the CRL when less than a year is left and reissues the server certificate when it lacks these URLs; the CA itself is never replaced.
Windows (`X509Chain` with `RevocationMode.Online`, MailKit with `CheckCertificateRevocation = true`, `curl.exe` without `--ssl-no-revoke`) then reports no chain errors once the CA is trusted.
The URLs use `127.0.0.1` so they work without the hosts entry; `lab-pki` must be running when a client checks.

### Register a client and sign in by hand

Anonymous dynamic registration (RFC 7591) is on by default and needs no token:

```powershell
$body = '{"client_name":"Wino Mail","redirect_uris":["http://127.0.0.1/callback"],"token_endpoint_auth_method":"none","grant_types":["authorization_code","refresh_token"],"response_types":["code"],"scope":"urn:ietf:params:oauth:scope:mail offline_access"}'
curl.exe -s -H 'Content-Type: application/json' -d $body https://mail.stalwartoauth.test/auth/register
```

Stalwart rules that clients must follow:

- Redirect URIs must be loopback (`http://127.0.0.1/...`, `http://[::1]/...`) or a private-use scheme containing a dot (`app.winomail:/oauth`). `wino://auth-complete/oauth` is rejected with `invalid_redirect_uri`, and `http://localhost/...` is rejected too.
- The registration must include `scope`. A client registered without it gets `invalid_scope` for `urn:ietf:params:oauth:scope:mail`.
- Client ids are stateless (`swc1.` plus the signed registration); registering the same metadata again returns the same id.
- An authorization request with a redirect URI that was not registered is refused before sign-in (not affected by CVE-2026-81036, which covers versions through 0.16.19).
- Refresh tokens rotate on every refresh. Presenting a superseded refresh token within a few seconds still works (grace period); about 40 seconds later it returns `invalid_grant` and revokes the whole grant, including the newest refresh token.

A person signs in by opening the authorization URL in a browser, entering `frank@stalwartoauth.test` / `1`, and choosing Allow on the consent page:

```text
https://mail.stalwartoauth.test/login?response_type=code&client_id=<client_id>&redirect_uri=http%3A%2F%2F127.0.0.1%2Fcallback&scope=urn%3Aietf%3Aparams%3Aoauth%3Ascope%3Amail%20offline_access&state=<state>&code_challenge=<S256 challenge>&code_challenge_method=S256
```

The browser lands on `http://127.0.0.1/callback?code=...&state=...&iss=https%3A%2F%2Fmail.stalwartoauth.test`; exchange the code at `/auth/token` with `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, and `code_verifier`.
The login page itself posts JSON to `/api/auth` (`{"type":"authCode","accountName":...,"accountSecret":...,"query":"<the query string>"}`, then `{"type":"authConsent","token":...,"query":...,"consent":true}`); `seed/stalwart_oauth.py verify` uses that to run the whole flow without a browser.
IMAP then accepts `AUTHENTICATE OAUTHBEARER` with `n,a=frank@stalwartoauth.test,^Ahost=mail.stalwartoauth.test^Aport=993^Aauth=Bearer <token>^A^A` (`^A` is `0x01`) and `XOAUTH2`.

### How it is provisioned

Stalwart 1.0 keeps its settings in the data store. `provision/stalwart-oauth/config.json` only names the RocksDB store, and `STALWART_RECOVERY_ADMIN=admin:1` gives a fixed administrator.
`seed/stalwart_oauth.py provision` applies the rest through the management API (JMAP `x:*` objects, as `stalwart-cli` does): password policy lowered so `1` is accepted, domain `stalwartoauth.test`, default hostname `mail.stalwartoauth.test` (it becomes the issuer), the certificate from `tls/`, a STARTTLS submission listener on 587, and the account.
Listener, hostname, and certificate changes need a restart; the script reports it and `local-lab.ps1` restarts the container. Every step is idempotent.
`up`, `provision`, `seed`, and `verify` run the matching `stalwart_oauth.py` step; `verify` checks the TLS capabilities, metadata, registration, the unregistered-redirect refusal, a full authorization code + PKCE sign-in, IMAP/SMTP OAUTHBEARER and XOAUTH2, and refresh rotation, and writes `.state/stalwart-oauth.json`.

Other endpoints:

| Endpoint | Purpose |
|---|---|
| `http://127.0.0.1:8803/` | Stalwart web admin (`admin` / `1`) |
| `http://127.0.0.1:8800/admin/` | Baïkal admin (`admin` / `1`) |
| `http://127.0.0.1:5800/` | James WebAdmin REST (send header `Password: 1`) |
| `http://127.0.0.1:4080/` | GreenMail API |

Capability notes:

- Dovecot replaces its whole CAPABILITY list when `imap_capability` is set, so the `condstore` container reads the stock list from the `dovecot` container at start-up and removes `QRESYNC`. A `uidonly` role (removes `CONDSTORE` too) exists in `provision/mailserver/user-patches.sh` if you want a second UID-only server.
- Wino picks QRESYNC, then CONDSTORE, then UID-only purely from the advertised capabilities (`UnifiedImapSynchronizer.DetermineSyncStrategy`).
- James advertised `QRESYNC` on the pinned image; the verify step prints the real list every run.
- Folder separators: `.` on Dovecot, GreenMail, and James; `/` on Stalwart. Stalwart and James advertise SPECIAL-USE; GreenMail does not, so Wino resolves its role folders by name.

## Mail routing

- Each server owns one domain. The hub (`dovecot`'s Postfix) has a transport map that sends `condstore.test`, `stalwart.test`, `greenmail.test`, and `james.test` to the right container. Any other domain is refused.
- `condstore` relays everything non-local to the hub. Stalwart has a relay route to the hub. James's `RemoteDelivery` uses the hub as gateway. GreenMail cannot relay, so its Wino account submits through the hub's port 25 (published as `4025`), which trusts the Docker network.
- `verify` sends 20 probes (every account to every other account) through each account's own submission endpoint and waits for them to arrive, then deletes them.

## Commands

| Command | Result |
|---|---|
| `up` | Start servers, provision, seed, verify, generate client artifacts |
| `status` | Container state, port reachability, artifact location |
| `provision` | Re-create the Stalwart and James domain/account (James forgets them on restart) and re-apply the Stalwart OAuth settings |
| `seed` | Add missing fixtures to running servers; existing edits, moves, and deletions are preserved |
| `verify` | Print capabilities per server and run the delivery probes |
| `generate-db` | Regenerate `Wino210.db`, DAV credentials, and the backup from the saved fixture manifest |
| `install-app` | Install the generated database into the Debug package and launch Wino |
| `trust` / `untrust` | Trust (or remove) the lab CA for the current user and the `mail.stalwartoauth.test` hosts entry |
| `stop` | Stop services, keep volumes |
| `reset -Force` | Delete this lab's volumes and `.state`, then run `up` |
| `logs [-Service <name>]` | Last 100 log lines |
| `help` | Command summary without Docker |

The output directory is `tools/local-lab/generated`:

| Artifact | Purpose |
|---|---|
| `Wino210.db` | Five configured IMAP accounts with CalDAV and CardDAV enabled, no cached mail |
| `credentials/dav/<account-id>.bin` | DAV passwords encrypted with Windows CurrentUser DPAPI |
| `wino-lab-backup.winosnap` | Encrypted backup in Wino's own format (Settings > Backup & restore), password `1`. Backups never carry passwords, so restoring asks for `1` per account |
| `manifest.json` | Account IDs, endpoints, capabilities, counts |

`.state` holds the anchor date, per-account mail ledgers, the seed catalog, `fixtures.json`, and `stalwart-oauth.json`. `generated`, `.state`, `app-backups`, and `tls` are ignored by Git.

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

Message-IDs are `<key.NNNN@wino.lab>`. Invitations use the other lab accounts as organizers and attendees, so replies travel between servers.

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
| Port occupied | Free the port listed in the error (all lab ports are in the tables above; the OAuth server needs `127.0.0.1:443`) |
| `mail.stalwartoauth.test` does not resolve, or its certificate is untrusted | Run `trust` once |
| `RevocationStatusUnknown` / `OfflineRevocation` for `mail.stalwartoauth.test` | Start `lab-pki` (`up`), check `http://127.0.0.1:8807/lab-ca.crl`; a certificate issued before the CRL change is reissued by `up` |
| Readiness timeout | `logs -Service <name>`; James needs about a minute on first start |
| James account or mail missing after a restart | James uses the memory backend. Run `up` (or `provision` then `seed`) again; Wino resynchronizes the account |
| Stalwart warnings about "database key defined in local configuration" | Expected; the lab keeps its settings in `provision/stalwart/config.toml` |
| Generator build error | Install the SDK selected by `global.json` |
| DAV password unavailable in Wino | Run `generate-db` as the Windows user who runs Wino, then `install-app` |
| Old sync state after `reset` | Run `install-app` again so Wino starts from the fresh database |

Server state lives in Compose-owned named volumes (`wino-local-lab_*`). `reset -Force` deletes only those.

## Implementation references

- [Compose configuration](compose.yaml)
- [docker-mailserver provisioning and capability patch](provision/mailserver/user-patches.sh)
- [Stalwart configuration](provision/stalwart/config.toml)
- [Stalwart OAuth store configuration](provision/stalwart-oauth/config.json), [lab CA script](provision/stalwart-oauth/make-certs.sh), [OAuth provisioning and verification](seed/stalwart_oauth.py)
- [James configuration overrides](provision/james/)
- [Radicale configuration](provision/radicale/config), [Baïkal provisioning](provision/baikal/dav.php)
- [Seed, provisioning, and verification tool](seed/seed.py)
- [Database and backup generator](DatabaseGenerator/Program.cs)
- [PowerShell entry point](../../scripts/lab/windows/local-lab.ps1)

The generator uses `DatabaseService`, the current entity mappings, `DatabaseSchemaService`, `DavCredentialStore`, `SyncSnapshotKeyService`, and `SyncSnapshotCryptography` from Wino. No production application code changes.
