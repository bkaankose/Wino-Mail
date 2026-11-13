# The lab on macOS

1. The mail servers are the remote lab described in [the lab README](../README.md). Fetch its account manifest with
   `mkdir -p ~/.wino-lab && ssh wino-lab wino-lab/lab.sh manifest > ~/.wino-lab/fixtures.json` (it holds the lab password; keep it out of the repository).
2. Build the macOS Debug app, then `make-lab-app.sh` copies it to `~/Desktop/wino-lab-app/Wino Lab.app` with the bundle id
   `com.winomail.macos.lab`: its own sandbox container, apart from the installed Wino Mail, signed ad hoc. Debug lab
   bundles keep secrets in a file in that container (`LabSecureItemStore`) because ad hoc builds cannot use the keychain.
3. Start it with `WINO_API_URL=http://localhost:5199 ".../Wino Lab.app/Contents/MacOS/Wino.Mail.MacOS"` to use a local
   API (Debug builds only, loopback only).
4. Drive it through the debug bridge (`{container}/Data/tmp/wino-debug/cmd`): copy `~/.wino-lab/fixtures.json`
   there as `lab-fixtures.json`, then `onboarding-imap`, `imap-step servers`, `imap-fill dovecot`, `imap-save`; turn the
   account's calendar/contact/task sources to DAV in the wizard (or the database) and run `lab-sync`. `lab-signin EMAIL`
   signs in to the local API with the password in `lab-password` (deleted after use). `assistant-ask TEXT`,
   `assistant-pick N`, `assistant-run`, `bubble-type [sel=0,1] TEXT`, `bubble-run` exercise the assistant.
5. `labshot.sh NAME` captures only the lab app's windows (the bridge's `snap` renders web views too); `labclick.sh TITLE`
   clicks a button by title. Bring the lab app to the front before any click by position: other apps' windows overlap it.
