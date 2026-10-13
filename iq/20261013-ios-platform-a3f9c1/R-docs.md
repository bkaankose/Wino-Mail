# R: iOS documentation (T19)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-aaf4aa37b4126be0d` (branch `worktree-agent-aaf4aa37b4126be0d`, HEAD 80ca7f38), seeded with `seed-after-NM2O2.patch` (staged by `git apply --3way`; 668 entries, `src/Wino.Mail.iOS` present). My edits are unstaged; new files are untracked. Nothing is committed. No source code changed.
This report file is also in the worktree as `iq/20261013-ios-platform-a3f9c1/R-docs.md` (untracked): leave it out when you apply the diff.

## Files

New:
- `docs/ios-design-decisions.md`: settled decisions, differences from Mac Revision 2, mode layouts per idiom, project map, implementation status (13 Oct 2026), verification approach.
- `docs/harness/development-ios.md`: requirements, platform selection, RID and TFM rules, build commands, identity and signing, entitlements, Info.plist keys, storage and Keychain, Simulator run, Debug bridge (command names checked against `Register(...)` calls in the seed), Windows-mirror notes, VS Code, verification table, pending external actions, release.
- `WinoMail.iOS.code-workspace`: `WinoMail.iOS.slnx` as default solution; tasks **Build Wino Mail (iOS Simulator, arm64)** (default) and **Build Wino Mail iOS solution**. No launch configuration (not verifiable).

Edited:
- `AGENTS.md`: three applications; iOS row in the platform table; platform sentence naming `Wino.Platform.iOS` and the Apple shared layer; iOS and Apple-shared routing bullets; shared change names all three apps; AOT exemption by `-macos`/`-ios` TFM; `WinoTargetPlatform` values `Windows`, `MacOS`, `iOS`; reference rows for the iOS guide and iOS design decisions; dev-loop note; verification rows for UIKit, the Apple shared layer, and iOS signing.
- `docs/README.md`: iOS guide and iOS design rows; platform column corrected.
- `docs/harness/implementation-rules.md`: three apps, architecture tree (Platform.iOS, UIKit, Phone, Pad, iOS head, controls line), three composition roots all calling `RegisterWinoViewModels()`, UIKit in the threading rule, iOS data path.
- `docs/harness/development-macos.md`: `ios` workload now needed on Mac hosts; RID narrowing of the multi-targeted Apple libraries; Apple-shared-layer verification row.
- `docs/macos-shared-logic-roadmap.md`: new section "Status after the iOS Phase 0" (entries #25, #12/#37, B's unnumbered moves, #50, #8, #30, #52/#55, #36, #31/#43, #44, #11, Controls.Core/Presentation.Core moves), each with what landed, what remains, and the report letter.
- `controls/AGENTS.md`, `controls/README.md`: four groups, iOS guide links, complete descriptions of `Wino.Presentation.Core`, `Wino.Editor.WebKit`, `Wino.Presentation.UIKit`, `Wino.Mail.Controls.UIKit`, UIKit build command and verification rules.
- `src/Wino.Core.Apple.Bindings/README.md` (F already covered slices): iOS bundle name `Wino.Mail.iOS.app/Frameworks`, `RequestReview`, App Store client registered in every iOS build.
- `docs/appkit-platform-migration-plan.md`: one cell notes the rename to `Wino.Editor.WebKit` (historical text kept).
- `.gitignore` (outside the listed scope, needed): `/docs/*`, `/docs/harness/*` and `*.code-workspace` ignored the three new files. Added `!/WinoMail.iOS.code-workspace`, `!/docs/harness/development-ios.md`, `!/docs/ios-design-decisions.md`, next to the existing Mac entries. Without them the new docs drop out of any patch.

Not edited:
- `docs/android-port-investigation.md`: not in the worktree (untracked in the main checkout only, and `/docs/*` ignores it). Rename references to apply there by hand: line 50 `controls/Wino.Editor.AppKit` (now `controls/Wino.Editor.WebKit`), line 51 `src/Wino.Core.MacOS.Bindings` (now `src/Wino.Core.Apple.Bindings`).
- `scripts/README.md`: no script changed. `docs/harness/development.md` still lists two platforms; it was outside scope (candidate follow-up: iOS row and workspace line).

## Verification

Link check (scratchpad `tools/linkcheck.py`: relative links and `#anchors`, code blocks excluded):

| File | Relative links | Broken |
| --- | --- | --- |
| `AGENTS.md` | 26 | 0 |
| `docs/README.md` | 18 | 0 |
| `docs/harness/implementation-rules.md` | 5 | 0 |
| `docs/harness/development-macos.md` | 12 | 0 |
| `docs/harness/development-ios.md` | 10 | 0 |
| `docs/ios-design-decisions.md` | 4 | 0 |
| `docs/macos-shared-logic-roadmap.md` | 3 | 0 |
| `controls/AGENTS.md` | 9 | 0 |
| `controls/README.md` | 1 | 0 |
| `src/Wino.Core.Apple.Bindings/README.md` | 0 | 0 |
| `docs/appkit-platform-migration-plan.md` | 15 | 4, all pre-existing at HEAD in lines I did not touch (`windows-abstraction-and-macos-implementation-plan.md` and three `thread://` links) |

- `WinoMail.iOS.code-workspace`: valid JSON (`json.load`).
- No em-dashes in added lines.
- `git diff --stat` (unstaged, on top of the staged seed): `AGENTS.md` 28, `controls/AGENTS.md` 26, `controls/README.md` 9, `docs/README.md` 18, `docs/appkit-platform-migration-plan.md` 2, `docs/harness/development-macos.md` 6, `docs/harness/implementation-rules.md` 19, `docs/macos-shared-logic-roadmap.md` 19, `src/Wino.Core.Apple.Bindings/README.md` 7, `.gitignore` 3 (about 93 insertions, 44 deletions); plus three new untracked files.

## Left out or marked pending

- **Calendar is not in this seed.** `seed-after-NM2O2` contains N, M2 and O2 but not O1 (`src/Wino.Mail.UIKit/Views/Calendar` is absent, `seedcalendar` is not registered). The status table says Calendar ran in its own tree and is not merged; the Debug bridge table omits calendar commands.
- **iPad Settings host is unresolved.** User decision: full-screen sheet. M2 code: form sheet on iPad. N placeholder: full-screen on both. Documented as "to be confirmed".
- **Possible integration gap in the seed (not fixed; source is out of scope):** `UIKitSettingsPageRoutes.RegisterSettingsPageRoutes` has no implementation in the tree, so `RegisterSettingsPages` registers `SettingsRootPlaceholderViewController` as `SettingsPage` in the registry constructors. `SettingsLifecycleHook` then sees `SettingsPage` mapped and skips `SettingsPageRoutes.Register`, so the real M2 Settings root may never be routed. Needs a build and a `settings` bridge check.
- **Not verified by any report, so marked pending or omitted:** iOS Release and device builds (commands given, marked not run); Mono runtime mode details (stated as SDK defaults only); `xcrun simctl launch --console-pty` and the install path come from standard tooling and J's bundle name, not a logged command; App ID capabilities (Keychain sharing, push) are inferred from the entitlements.
- External actions listed as pending: App ID and **Wino Mail iOS Development** profile, App Store record (and the placeholder App ID in `IOSMicrosoftStoreService`), Azure redirect `msauth.com.winomail.ios://auth`, Google iOS OAuth client plus the `Info.plist` scheme and `IAuthenticatorConfig`.
