# T13 Android documentation (done, 14 Oct 2026)

Docs only; no source, project or build file changed. Nothing committed.

## Files

New:
- `docs/harness/development-android.md`: toolchain (workload set pin 10.0.401.1), SDK install, AVDs, Windows hosts, platform selection, projects, build/install/run, adb checks, ControlsGallery, data paths, sign-in registrations (keytool commands), VS Code, verification, troubleshooting.
- `docs/releases-android.md`: version source, Release APK/AAB commands, upload-key signing, 16 KB check, deferred items, status.
- `docs/android-design-decisions.md`: settled decisions, size classes, control mapping, background model, deferred, owner review items R1-R6, project map.
- `WinoMail.Android.code-workspace`: three tasks (build default, `-t:Run`, solution). Valid JSON.

Edited: `AGENTS.md` (table, platform services, routing, AOT exemption, `WinoTargetPlatform`, discovery rows, dev loop, verification rows), `docs/harness/development.md`, `docs/harness/implementation-rules.md`, `docs/releases.md`, `docs/README.md`, `docs/android-port-investigation.md` (supersede note; fixed a broken link to a local-only doc), `README.md` (Platforms table), `scripts/README.md`, `.gitignore` (allow lines for the four docs and the workspace; `*.keystore`, `*.jks`).

## Verification

- Link check (relative links and anchors, code blocks skipped) over the 11 touched Markdown files: 119 links, 0 broken.
- `git check-ignore`: all new files are trackable.
- No em-dashes in new docs.

## Update after T7-T12

- `controls/Wino.Editor.Android` is marked "Planned" (absent now): change to present after T8.
- T12: run and record the Release APK/AAB publish, `zipalign -c -P 16`, 16 KB and API 23 launches; update "Status" in `releases-android.md`. Remove the `NuGetAudit=false` notes once Pkcs is fixed.
- T11: confirm the background table (WorkManager, AlarmManager, channels, 10-minute grace).
- T0: canvas boards still unpublished.
- Out of scope (controls/): `controls/AGENTS.md` and `controls/README.md` need the Android libraries.
- Workspace has no debugger launch configuration.
