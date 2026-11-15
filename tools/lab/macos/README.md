# The lab on macOS

1. The mail servers are the remote lab described in [the lab README](../README.md). The Mac needs only the manifest
   `~/.wino-lab/fixtures.json` (it holds the lab password and the control token; keep it out of the repository). The
   owner fetches it with `lab.ps1 manifest` or `mkdir -p ~/.wino-lab && ssh wino-lab wino-lab/lab.sh manifest > ~/.wino-lab/fixtures.json`.
2. Lease accounts instead of using the shared baseline ones. With PowerShell 7 (`brew install powershell`), the
   client runs on the Mac too (everything except `generate-db` and `install-app`):
   `pwsh scripts/lab/windows/lab.ps1 lease -Holder "$(hostname -s)/mac"` saves `~/.wino-lab/leases/<id>.json`.
   Release it with `pwsh scripts/lab/windows/lab.ps1 release -Holder "$(hostname -s)/mac"` when done.
3. Build the macOS Debug app, then `make-lab-app.sh` copies it to `~/Desktop/wino-lab-app/Wino Lab.app` with the bundle id
   `com.winomail.macos.lab`: its own sandbox container, apart from the installed Wino Mail, signed ad hoc. Debug lab
   bundles keep secrets in a file in that container (`LabSecureItemStore`) because ad hoc builds cannot use the keychain.
4. Start it with `WINO_API_URL=http://localhost:5199 ".../Wino Lab.app/Contents/MacOS/Wino.Mail.MacOS"` to use a local
   API (Debug builds only, loopback only).
5. Drive it through the debug bridge (`{container}/Data/tmp/wino-debug/cmd`): copy the lease file there as
   `lab-fixtures.json` (it has the manifest's shape; `~/.wino-lab/fixtures.json` works too for the shared accounts),
   then `onboarding-imap`, `imap-step servers`, `imap-fill KEY` (the account `key` from the lease summary, for example
   `dovecot`), `imap-save`; turn the account's calendar/contact/task sources to DAV in the wizard (or the database) and
   run `lab-sync`. `lab-signin EMAIL` signs in to the local API with the password in `lab-password` (deleted after use).
   `assistant-ask TEXT`, `assistant-pick N`, `assistant-run`, `bubble-type [sel=0,1] TEXT`, `bubble-run` exercise the
   assistant. Delete `lab-fixtures.json` from the container when done; it holds the lab password.
6. `labshot.sh NAME` captures only the lab app's windows (the bridge's `snap` renders web views too); `labclick.sh TITLE`
   clicks a button by title. Bring the lab app to the front before any click by position: other apps' windows overlap it.

## Control API without PowerShell

The API is plain HTTPS with the manifest's bearer token (contract: `docs/contract.md` in the lab repository). With
`jq`, keep the token in a variable so it never appears in the command line history or output:

```sh
LAB=$(jq -r .control.url ~/.wino-lab/fixtures.json)
TOKEN=$(jq -r .control.token ~/.wino-lab/fixtures.json)
auth() { curl -sS -H "Authorization: Bearer $TOKEN" "$@"; }

curl -sS "$LAB/ping"
auth "$LAB/health" | jq '.servers[] | {key, ok}'
auth -X POST "$LAB/leases" -H 'Content-Type: application/json' \
  -d "{\"holder\":\"$(hostname -s)/mac\",\"servers\":[\"dovecot\"]}" > /tmp/lease.json
jq '.accounts[] | {key, address, imapHost}' /tmp/lease.json
auth "$LAB/accounts/alice1%40dovecot.test/folders" | jq
auth -X POST "$LAB/accounts/alice1%40dovecot.test/actions" -H 'Content-Type: application/json' \
  -d '{"type":"deliver","subject":"Lab check","count":2}'
auth "$LAB/accounts/alice1%40dovecot.test/trace?since=10m"
auth -X DELETE "$LAB/leases/$(jq -r .id /tmp/lease.json)" && rm /tmp/lease.json
```

A lease answer from `curl` is not in the lease-file shape `lab.ps1` writes (no `anchorDate` or `labHost`), but it has
`accounts` and `password`, which is all `imap-fill` needs.
