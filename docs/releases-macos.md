# macOS release packages

This guide explains how to build the macOS DMG and Mac App Store package on a Mac.
The [release index](releases.md) lists the shared steps and the Windows guide.
Development setup, signing identities, and entitlements are in the [macOS development guide](harness/development-macos.md).

## Translations

Both scripts first ask whether to run `scripts/localization/translate_resources.py --apply` with `python3`.
Yes translates every missing key, then continues. The run requires `WINO_OPENAI_API_KEY`; if it is missing or translation fails, the script stops before compilation.
Translated files remain uncommitted changes, and the build packages them. Uncommitted changes do not block a release.
Pass `--translate` or `--skip-translation` to answer without the prompt. With `--non-interactive` and neither flag, translation is skipped.

## macOS DMG

`scripts/release/macos/build-macos-release.sh` builds the macOS app as a signed, notarized, and stapled DMG.
It runs on a Mac and uses the same manifest version as Windows: `2.0.55.0` becomes app version `2.0.55`.
It does not change `Info.plist` or any other source file.

One-time setup on the Mac:

1. Install Xcode and the .NET SDK selected by `global.json`, then run `dotnet workload install macos` (see the [development guide](harness/development-macos.md#requirements)).
2. Import the **Developer ID Application: Burak Kaan Kose (4VB7YWRAQ9)** certificate with its private key into the login keychain.
3. Set the notarization variables in [Local script environment](local-script-environment.md).
4. Have the Sparkle update signing key in the login keychain (account `com.winomail.macos`).
   It was created on the release Mac on 2026-10-10. Keep an offline backup:
   `generate_keys --account com.winomail.macos -x <file>`, then store the file securely.
   On another Mac, import it with `generate_keys --account com.winomail.macos -f <file>`.
   The tools are in `~/Library/Caches/WinoMail/sparkle-<version>/bin` after the first run.
   If the key is lost, installed DMG builds can no longer verify updates, so keep the backup.

Run from the repository root in Terminal:

```bash
bash scripts/release/macos/build-macos-release.sh
```

After the translation question, select **Apple silicon only** or **Universal**. Universal is the default and also runs on Intel Macs.
For unattended runs, use `--non-interactive --arch universal` or `--arch arm64`, plus `--translate` or `--skip-translation`.

The script checks the tools, signing identity, and credentials before it compiles.
It publishes Release with the Developer ID identity, the hardened runtime, and the app's sandbox entitlements, without a provisioning profile.
Debug builds keep the Apple Development identity and profile.
It verifies the signature, entitlements, bundle ID, version, and architectures, then creates and signs the DMG.
It also verifies Sparkle: the framework and its helpers carry the Developer ID signature, and the feed URL and public key match `Sparkle.plist`.
Notarization usually takes a few minutes. When Apple rejects the DMG, the script prints the issues from the notarization log.
After acceptance, it staples the ticket and requires Gatekeeper to report `source=Notarized Developer ID` for the DMG and the app.

Outputs appear under `~/Wino Releases`, or `WINO_RELEASES_ROOT` or `--output-root`:

```text
2.0.55.0/
  macOS/
    WinoMail_2.0.55_universal.dmg
    WinoMail_2.0.55_universal.dmg.sha256
    appcast.xml                         (Sparkle feed for this DMG)
    WinoMail_2.0.55_universal.notarization.json
    Symbols/universal/                  (dSYM and PDB files, when produced)
```

### DMG updates (Sparkle)

DMG builds update through [Sparkle](https://sparkle-project.org) 2. Mac App Store builds do not contain it.
The app checks `https://download.winomail.app/macos/appcast.xml` daily, and users can choose **Check for Updates…** in the app menu.
Settings › General turns automatic checks and automatic installation on or off.

After a successful run, the script signs the stapled DMG with the Sparkle key and writes `appcast.xml`.
It lists only this version and points at `https://download.winomail.app/macos/<DMG name>`.
To publish an update:

1. Upload the DMG to `macos/` in the `wino-downloads` bucket.
2. Check that the DMG URL downloads.
3. Upload `appcast.xml` to `macos/appcast.xml` with `Cache-Control: no-cache`.

Publish the universal build's appcast. An arm64 build's appcast carries `arm64` hardware requirements, so Intel Macs ignore it.
Do not modify the DMG after the script signs it; Sparkle rejects a changed file.
See `src/Wino.Core.Apple.Bindings/README.md` for the integration and for testing an update locally.

The script stops if the DMG already exists. Failed runs keep their logs under `.staging/macos-<run>` in the output root.
The DMG opens a standard window with the app and an Applications link.

Codesign needs the login keychain, which is unavailable to SSH sessions (`errSecInternalComponent`).
To build over SSH, start `screen -dmS winosign` in Terminal on the Mac and run the script inside that session.
This is not required when the script runs in Terminal.

## Mac App Store package

`scripts/release/macos/build-macos-appstore.sh` builds the Mac App Store `.pkg`.
It uses the manifest version like the DMG, so `2.2.0.0` becomes app version `2.2.0`.
It signs with `Entitlements.AppStore.plist` and fails if the app contains Sparkle or its settings, because App Store apps update only through the App Store.
It defines `WINO_APPSTORE` through `-p:WinoMacDistribution=AppStore`.

App Store Connect rejects a build number it has already received, so the build number (`CFBundleVersion`) doesn't follow the version.
The default build number is the UTC time as `yyDDDHHMM`, for example `262821953`, which increases with every run. Pass `--build-number <n>` to choose one.

One-time setup on the Mac, in addition to the DMG setup:

1. The **Apple Distribution** and **Mac Installer Distribution** certificates of team 4VB7YWRAQ9, with private keys, in the login keychain.
   The Mac Installer Distribution certificate appears in the keychain as **3rd Party Mac Developer Installer**.
2. The **Wino Mail macOS App Store** provisioning profile (type Mac App Store Connect, App ID `com.winomail.macos`) installed.
   The script picks the newest unexpired App Store profile for the bundle ID, so a renewed profile needs no script change.
3. The App Store Connect API variables in [Local script environment](local-script-environment.md). Validation and upload use the same key as notarization.

Run from the repository root in Terminal:

```bash
bash scripts/release/macos/build-macos-appstore.sh
bash scripts/release/macos/build-macos-appstore.sh --upload
```

The script publishes Release with the Apple Distribution identity, the App Store profile, and the sandbox entitlements.
It then verifies the signature, the application and team identifier entitlements, the embedded profile, the signature on each framework, and the bundle values.
It wraps the app in a package signed with the installer certificate and validates the package with App Store Connect.
`--upload` uploads after validation. The build appears in App Store Connect after Apple processes it.
`--skip-validation` builds without contacting App Store Connect.

Outputs appear beside the DMG:

```text
2.2.0.0/
  macOS-AppStore/
    WinoMail_2.2.0_262821953_universal.pkg
    WinoMail_2.2.0_262821953_universal.pkg.sha256
    WinoMail_2.2.0_262821953_universal.validate.log
    Symbols/262821953-universal/
```

## Failures and repeated builds

The scripts stop before compilation when the output already exists. Failed runs keep their logs under `.staging/macos-<run>` in the output root.
Do not notarize or publish only to validate a development change.
