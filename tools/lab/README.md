# Wino mail lab

The lab is a set of mail servers with different IMAP capability sets, most with CalDAV and CardDAV, seeded with generated mail, calendars and contacts, plus a POP3 server, fault injection, certificate test endpoints and a control API. It runs permanently on a remote Linux host. Its source and server internals live in the private lab repository (`bkaankose/wino-mail-lab`); its `docs/contract.md` is the binding contract between the lab and the clients here. This folder and `scripts/lab/windows/lab.ps1` hold only the client side.

## What a machine needs

Only the manifest `~/.wino-lab/fixtures.json` (on Windows `%USERPROFILE%\.wino-lab\fixtures.json`). It holds the lab password, the control API token and every public endpoint. The owner fetches it with `lab.ps1 manifest`, which is the only command besides `tunnel` that uses SSH.

- A machine without the manifest is not set up for the lab. Lab tests are refused there; they do not fetch or create the manifest and do not look for the password anywhere else.
- Never write the password or the control token into this repository, a commit, a log, or a reply. The client redacts both from everything it prints.
- `lab.ps1 status` says when the manifest is older than schema 2 (no `control` section); the owner then runs `lab.ps1 manifest` again. The same applies when the API answers 401.

Everything the client writes goes to `~/.wino-lab` (override with `WINO_LAB_STATE`), outside the repository: the manifest, lease files, the generated database and application backups. All worktrees on the machine share it.

## Servers

Every server answers on its own name under `lab.winomail.app`, over implicit TLS with public certificates (plaintext and STARTTLS are not published). The IMAP and SMTP login is the full address; every account has the lab password.

| Key | Baseline account | Pool accounts | Host | Ports | DAV | Character |
| --- | --- | --- | --- | --- | --- | --- |
| `dovecot` | `alice@dovecot.test` | `alice1`–`alice4@dovecot.test` | `dovecot.lab.winomail.app` | IMAP 993, SMTP 465, DAV 443 | Baikal, `https://dovecot.lab.winomail.app/dav.php/` (login is the user name) | QRESYNC, delimiter `.` |
| `condstore` | `bob@condstore.test` | `bob1`–`bob4@condstore.test` | `condstore.lab.winomail.app` | 993, 465, 443 | Radicale, `https://condstore.lab.winomail.app/` | CONDSTORE only |
| `stalwart` | `carol@stalwart.test` | `carol1`–`carol4@stalwart.test` | `stalwart.lab.winomail.app` | 993, 465, 443 | `/dav/cal/` and `/dav/card/` | QRESYNC, IMAP4rev2, delimiter `/` |
| `greenmail` | `dave@greenmail.test` | `dave1`–`dave4@greenmail.test` | `greenmail.lab.winomail.app` | 993, 443 (no SMTP) | Radicale | UID only, in memory |
| `james` | `eve@james.test` | `eve1`–`eve4@james.test` | `james.lab.winomail.app` | 993, 465, 443 | Radicale | QRESYNC advertised, in memory |
| `cyrus` | none | `judy1`–`judy4@cyrus.test` | `cyrus.lab.winomail.app` | 993, 465, 443 | where the lease file has `davUrl` | Cyrus IMAP |
| `pop3` | `frank@pop3.test` | `frank1`–`frank4@pop3.test` | `pop3.lab.winomail.app` | POP3 995, IMAP 993 (inspection), SMTP 465 | none | Dovecot POP3: UIDL, TOP, SASL PLAIN/LOGIN |

Other names:

| Host | Ports | Purpose |
| --- | --- | --- |
| `control.lab.winomail.app` | 443 | Control API (`https://control.lab.winomail.app/v1`), used by `lab.ps1` |
| `flaky.lab.winomail.app` | 993, 465, 443 | The Dovecot server through Toxiproxy; every `dovecot.test` login works here. `lab.ps1 fault` adds latency, timeouts, resets and outages |
| `cert-valid`, `cert-wronghost`, `cert-expired`, `cert-revoked` `.lab.winomail.app` | 993, 465, 443 | TLS endpoints signed by the lab CA (`lab.ps1 ca`) for certificate trust tests |
| `oauth.lab.winomail.app` | 993, 465, 443 | Stalwart with OAuth (OIDC, dynamic registration, OAUTHBEARER/XOAUTH2); accounts in the manifest's `oauthAccounts` |
| `auto.lab.winomail.app` and subdomains | 993, 465, 443 | Real-domain accounts for autoconfig, SRV and well-known discovery tests; accounts in `discoveryAccounts` |

- The exact endpoints of every account are in the manifest (`accounts`, `pop3Accounts`, `scaleAccounts`, `discoveryAccounts`, `oauthAccounts`) and in each lease file.
- GreenMail has no authenticated submission. Its Wino account sends through `dovecot.lab.winomail.app:465` with the Dovecot account's login.
- Mail never leaves the lab: the hub delivers only to the lab domains.
- `scaleAccounts` are large read-only mailboxes; `reset` refuses them.
- Every baseline and pool account has a `Corpus` folder with the pathological message collection (`lab.ps1 corpus`).

## Leases

Agents and worktrees lease pool accounts instead of using the shared baseline accounts. A lease picks a free pool account on every requested server, resets it to the baseline seed, and is yours until you release it or it expires (24 hours by default; expired leases return automatically). `lab.ps1 lease` saves it to `~/.wino-lab/leases/<id>.json` in the manifest's shape, so `DatabaseGenerator`, the Sandbox helper and the Mac debug bridge read it like the manifest.

The holder name identifies who holds a lease. It defaults to `<computer>/<worktree folder>`; the Sandbox helper uses `<computer>/sandbox`. `install-app` reuses a still-valid lease of the same holder (renewing it), so repeated installs keep the same mailboxes; `release` first to get freshly reset ones.

The baseline accounts stay for older clients and `-Shared` installs. Other agents may use them at the same time, so do not rely on their state, and `reset` them only with `-Force` when you know nobody else is using them.

## Client script

`scripts/lab/windows/lab.ps1` (PowerShell 7.2+; the API commands also run in `pwsh` on macOS). `lab.ps1 help` lists every option.

| Command | What it does |
| --- | --- |
| `status` (`doctor`) | TCP reachability of every lab host, the manifest's schema, `/ping` and `/health` of the control API (per server checks), and the local leases |
| `manifest` | Owner only: fetches the manifest over SSH into `~/.wino-lab/fixtures.json` |
| `lease [-Servers dovecot,stalwart] [-Pop3] [-Holder name] [-TtlHours 24]` | Leases one pool account per server (default every IMAP server), saves the lease file and prints a summary without the password |
| `leases` | Lists every lease on the lab and removes local files of leases that ended |
| `renew [-Id id]` / `release [-Id id \| -All]` | Extends or returns this holder's lease, a given one, or (`-All`) every local lease |
| `generate-db [-Lease id \| -Shared] [-Pop3]` | Runs [DatabaseGenerator](DatabaseGenerator) on a lease file (or the manifest's shared accounts) into `~/.wino-lab/generated`, laid out like the app's LocalState: `Wino210.db` and the passwords in the app's DPAPI secure item store. Windows only |
| `install-app [-Shared] [-Servers ...] [-Pop3]` | Leases (or reuses this holder's lease), generates, replaces the registered Debug package's database and credentials, and launches Wino. The previous data moves to `~/.wino-lab/app-backups`. Wino must be closed. Windows only |
| `inspect <address>` | The server's truth: folders with UIDVALIDITY, UIDNEXT, HIGHESTMODSEQ and counts; `-Folder X` lists messages with flags and MODSEQ; `-Folder X -Uid n [-Out file]` fetches the raw message; `-Calendars [-Calendar href]`, `-Contacts [-AddressBook href]`, `-Pop3`; `-Json` for raw output |
| `act <address> <type>` | Acts as another device: `deliver`, `flag`, `move`, `copy`, `delete`, `create-folder`, `delete-folder`, `rename-folder`, `bump-uidvalidity`, `event-*`, `contact-*`, `send-invite`. Switches fill the body (`-Folder`, `-Uid 3,4`, `-MessageId`, `-To`, `-Add '\Seen'`, `-Remove`, `-Subject`, `-Text`, `-Html`, `-Count`, `-Attach`, `-Method`, `-Style`, `-Summary`, `-Start`, `-End`); `-Body '<json>'` or `-Body @file.json` passes anything else |
| `import <address> <file.eml> [-Folder] [-Flags]` | Appends a raw message (IMAP APPEND) and prints its UID |
| `reset <address> [-Force]` | Restores the baseline seed of a pool account (baseline accounts need `-Force`) |
| `corpus [name] [-To address \| -Out file]` | Lists the pathological message collection, fetches one message, or delivers it to an account |
| `fault <imap\|smtp\|dav> <kind> [params] [-Minutes 15]` / `fault -Clear` | Injects a fault on `flaky.lab.winomail.app`: `latency`, `bandwidth`, `timeout`, `reset`, `slice`, `down`, with parameters as JSON or `latency=2000,jitter=200`. Faults remove themselves after the given minutes (at most 120) |
| `trace <address> [-Since 10m] [-Protocol imap]` | The server-side protocol log of the account, passwords redacted |
| `ca [-Out path]` | Saves the lab CA certificate (default `~/.wino-lab/lab-ca.crt`) |
| `tunnel` | Forwards the lab's plaintext loopback ports to `127.0.0.1` over SSH, for tools that need the unauthenticated hub port |

`generate-db` and `install-app` accept `-LegacyPlaintext`, which writes the format of releases before the secure item store (plaintext password columns and `credentials\dav` files) to test the upgrade migration. With `-Pop3` (or a lease that has POP3 accounts) the generator also adds the POP3 accounts with their local system folders.

Trusting the lab CA changes the machine's certificate store, so it is a manual step for the person at the machine (Windows: `Import-Certificate -FilePath ~/.wino-lab/lab-ca.crt -CertStoreLocation Cert:\CurrentUser\Root`; macOS: add it to the login keychain in Keychain Access). Without it, the `cert-*` hosts exercise Wino's untrusted-certificate flow.

`WINO_LAB_HOST` overrides the DNS suffix, `WINO_LAB_SSH_HOST` the SSH alias (default `wino-lab`).

## Agent workflow

1. `lab.ps1 status` — the lab is up and the manifest is schema 2. If the manifest is missing, stop: the lab is not set up on this machine.
2. `lab.ps1 install-app` (Windows) — leases accounts for this worktree and starts the Debug app with them. On other platforms, `lab.ps1 lease` and add a leased account by hand (below).
3. Reproduce: change the mailbox from the server side with `act` (new mail, flags from another device, moves, folder renames, UIDVALIDITY changes, invitations), `import` a specific `.eml`, `corpus ... -To` for pathological messages, or `fault` to slow down or break the connection on the flaky host.
4. Verify: compare what Wino shows with `inspect` (flags, UIDs, folder state, events, contacts), and read `trace` to see the commands Wino sent.
5. `lab.ps1 release` when done, so the accounts return to the pool.

```powershell
.\scripts\lab\windows\lab.ps1 install-app -Servers dovecot,stalwart
.\scripts\lab\windows\lab.ps1 act alice1@dovecot.test deliver -Subject 'Lab check' -Count 3
.\scripts\lab\windows\lab.ps1 act alice1@dovecot.test flag -Uid 5 -Add '\Seen'
.\scripts\lab\windows\lab.ps1 inspect alice1@dovecot.test -Folder INBOX
.\scripts\lab\windows\lab.ps1 trace alice1@dovecot.test -Since 5m
.\scripts\lab\windows\lab.ps1 release
```

The addresses come from the lease summary; the pool account you get depends on what is free.

## Adding a leased account by hand

Any Wino app on any device can use a leased account: in the IMAP (or POP3) setup enter the address, the lab password, and the server names and ports from the lease summary or file. The addresses end in `.test`, so automatic discovery does not apply (except the `auto.lab.winomail.app` accounts, which exist to test it). For calendars and contacts, turn on CalDAV/CardDAV with the account's `davUrl`, `cardDavUrl` and `davUsername`.

## macOS

See [macos/README.md](macos/README.md) for the separate lab copy of the Mac app, its debug bridge, and the control API from a Mac.
