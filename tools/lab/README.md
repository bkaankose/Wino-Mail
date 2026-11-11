# Wino mail lab

The lab is five mail servers with different IMAP capability sets, each with CalDAV and CardDAV, seeded with generated mail, calendars and contacts. It runs permanently on a remote Linux host and is **not part of this repository**. This folder holds only what connects a development machine to it.

## Servers

Every server answers on its own name under the lab's DNS suffix (`lab.winomail.app`), over TLS with public certificates:

| Key | Account | Server name | IMAP | SMTP | CalDAV / CardDAV | Capability set |
| --- | --- | --- | --- | --- | --- | --- |
| `dovecot` | `alice@dovecot.test` | `dovecot.lab.winomail.app` | 993 | 465 | `https://dovecot.lab.winomail.app/dav.php/` (login `alice`) | QRESYNC, delimiter `.`, Baikal |
| `condstore` | `bob@condstore.test` | `condstore.lab.winomail.app` | 993 | 465 | `https://condstore.lab.winomail.app/` (login `bob`) | CONDSTORE only, Radicale |
| `stalwart` | `carol@stalwart.test` | `stalwart.lab.winomail.app` | 993 | 465 | `https://stalwart.lab.winomail.app/dav/cal/` and `/dav/card/` | QRESYNC, IMAP4rev2, delimiter `/` |
| `greenmail` | `dave@greenmail.test` | `greenmail.lab.winomail.app` | 993 | none | `https://greenmail.lab.winomail.app/` (login `dave`) | UID only, in memory, Radicale |
| `james` | `eve@james.test` | `james.lab.winomail.app` | 993 | 465 | `https://james.lab.winomail.app/` (login `eve`) | QRESYNC advertised, in memory, Radicale |

- Security is implicit TLS on every public port. Plaintext and STARTTLS are not published.
- The IMAP and SMTP login is the full address. All accounts share one password; it is in the manifest and is never written into this repository.
- GreenMail has no authenticated submission. Its Wino account sends through `dovecot.lab.winomail.app:465` with the Dovecot account's login.
- Mail never leaves the lab: the hub delivers only to the five lab domains.
- GreenMail and James keep everything in memory. The lab host reseeds them when it starts, so their content can reset.
- The servers' admin consoles are not published.

## Client script

`scripts/lab/windows/lab.ps1` (PowerShell 7):

| Command | What it does |
| --- | --- |
| `status` | Checks that the public endpoints answer and shows which local artifacts exist |
| `manifest` | Fetches the account manifest from the lab host over SSH into `~/.wino-lab/fixtures.json` |
| `generate-db` | Runs [DatabaseGenerator](DatabaseGenerator) on the manifest: `Wino210.db` with one IMAP account per server, plus the DAV credentials, in `~/.wino-lab/generated` |
| `install-app` | Replaces the registered Debug package's database with the generated one and launches Wino. The previous data moves to `~/.wino-lab/app-backups`. Wino must be closed |
| `tunnel` | Forwards the lab's plaintext loopback ports to `127.0.0.1`, for tools that need the unauthenticated hub port or the certificate test endpoints |

`manifest` and `tunnel` need SSH access to the lab host. The script uses the SSH alias `wino-lab`; set `WINO_LAB_SSH_HOST` for another one. `WINO_LAB_HOST` overrides the DNS suffix.

Everything the script writes goes to `~/.wino-lab` in the user profile (override with `WINO_LAB_STATE`), outside the repository, because it contains the lab password or application data. All worktrees and tools on the machine share it.

## The lab password

The password exists in two places only: the lab host, and `~/.wino-lab/fixtures.json` on a machine that ran `lab.ps1 manifest`. SSH access to the lab host is what grants it.

- Tools read it from the manifest (`password`) or from `WINO_LAB_PASSWORD`. Never write it into a file in this repository, a commit, a log, or a chat message.
- A machine without the manifest is not set up for the lab. Lab tests are refused there; they do not look for the password anywhere else.
- When the lab password changes, run `lab.ps1 manifest` again.

Adding an account by hand works in any Wino app, on any device: enter the server name, ports and login from the table above in the IMAP setup. The addresses end in `.test`, so automatic discovery does not apply.

## macOS

See [macos/README.md](macos/README.md) for the separate lab copy of the Mac app and its debug bridge.
