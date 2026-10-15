# T13b Android documentation follow-ups (done, 15 Oct 2026)

Docs only; no builds, emulators or commits.

## Files
- **`docs/android-design-decisions.md`:**
  - Material 3 canvas link.
  - New "Material 3 pass and owner overrides" section, with the design-fidelity rule.
  - New "Modes" table: what each mode has, and what is omitted or simplified.
  - Background rows: grace flush, WorkManager, alarms, channels, FCM without `google-services.json`.
  - New "Accessibility and responsiveness" section: hinge split not implemented.
  - Corrections: `w840dp-h480dp`, medium `LinearLayout`, inline search, no intelligence card, no done page, R4 superseded, deferred list, Editor.Android present.
- **`docs/harness/development-android.md`:**
  - NDK r28c.
  - The `_B` AVDs and memory guidance.
  - Editor.Android row.
  - ILLink rule for views used only in XML.
  - Debug-only commands: DebugCommand seeds, SettingsDebug, ControlsGallery.
  - Clean Release note.
- **`docs/releases-android.md`:** NDK r28c, and the descriptor rule limited to views used only in XML.
- **`controls/AGENTS.md`, `controls/README.md`:** the three Android libraries, plus build and verification lines.
- **`AGENTS.md`:** unchanged (nothing wrong).

## Notes
- `seedcontacts`/`seedtasks` are present in `.worktrees/android-t10b` only. They reach main with the T10b merge.

## Link check
Relative links and anchors in the 5 touched files: 40 links, 0 broken. No em dashes.
