# Local release packages

This guide explains how to build release artifacts from a Wino Mail checkout and prepare them for distribution.
The release script creates packages locally. Publication to Microsoft Store or the download website is a separate maintainer task.

## Channels

| Channel | Distribution | Required access |
| --- | --- | --- |
| Store | Partner Center upload and signed local-update bundle | Package sources and a local Store-subject test certificate. Partner Center access is required for publication. |
| Beta | Signed bundle with the beta App Installer feed | Wino Artifact Signing account; download-site access for publication. |
| Stable sideload | Separate signed bundle with the stable App Installer feed | Same signing account; download-site access for publication. |

All channels use the same source and compiled binaries. Selecting Beta does not enable a separate compiler configuration or feature set.
Each distribution has its own package identity and runtime profile. All three can run at the same time.
Release maintainers decide which version to publish to each feed.

## Before you run the script

1. Set the new version in `src/Wino.Mail.WinUI/Package.appxmanifest`.
2. Add the What's New notes for that version with the [whats-new skill](../.claude/skills/whats-new/SKILL.md).
   The skill writes `src/Wino.Mail.WinUI/Assets/WhatsNew/<major.minor.build>.json` and its PNG illustrations.
3. Run `pwsh -NoProfile -File .\scripts\release\whats-new\validate.ps1`.

The release script runs the same validation. If the notes are missing or invalid, it shows the failures and asks whether to continue.
With `-NonInteractive`, it only shows a warning.
The What's New button in the title bar appears only when notes exist for the installed version.

## Run the script

After installing the build prerequisites below, run this command from the repository root in PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\release\build-releases.ps1
```

Select **yes** or **no** for Store, Beta, and stable sideload. Then select **x64** or **All**.
All includes x86, x64, and ARM64. If all channels are No, the script exits.

The script reads the version from `src/Wino.Mail.WinUI/Package.appxmanifest`. It does not increment the version or change the source manifest.
The version must have four numeric components, a nonzero major component, and a zero revision.
For example, `2.0.55.0` is valid.

The script compiles Release once for each selected architecture. All three channels use the same compiled binaries.
Each invocation keeps its build intermediates inside that run's staging directory, so a Visual Studio build or another release run cannot lock its XAML compiler outputs.
Sideload packaging replaces the identity, runtime profile, notification IDs, and resource index without compilation.
Beta packaging also replaces display names and artwork. Theme and accent preferences do not change.
The script creates and signs a separate bundle for each sideload distribution.
It checks each final copy against its signed bundle. Each sideload distribution receives its own App Installer update feed.
The script does not install or launch packages.

For a Store release, the script selects a test certificate from `Cert:\CurrentUser\My`.
The certificate subject must match the Store publisher in `Package.appxmanifest`.
The certificate must be valid for code signing and must have an accessible private key.
By default, the script selects the newest valid matching certificate.

To select a specific certificate, use its thumbprint:

```powershell
pwsh -NoProfile -File .\scripts\release\build-releases.ps1 -NonInteractive -Store -Architectures x64 -StoreTestCertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

You can also set `WINO_STORE_TEST_CERTIFICATE_THUMBPRINT` for repeated builds.

## Beta artwork and runtime profiles

The source `release-profile.json` describes Store stable. `scripts/release/profiles` contains the two sideload profiles.
The packager checks profile identity and notification IDs against the generated manifest.
The app reads its profile before activation. Mutex and event names use the installed package family name, without the version.

Supply beta artwork under `release-assets/Beta`, with paths that match the packaged assets.
See the README in that directory for the asset inventory command.
The packager requires every declared branding asset. It stops if an asset is missing.
Stable artwork is not a fallback for beta. Compiled executable metadata remains unchanged.

For a different artwork directory, use `-BetaAssetsPath`:

```powershell
pwsh -NoProfile -File .\scripts\release\build-releases.ps1 -NonInteractive -Store -Beta -Sideload -Architectures x64 -BetaAssetsPath D:\WinoBetaArtwork
```

For all architectures, supply `x86,x64,ARM64` from PowerShell.
The CI beta workflow uses this same script and requires the artwork in the checkout.
CI uses the `WINO_BETA_RELEASE_*` secrets listed below. It no longer uses the old PFX secret.
GitHub beta tags use `beta/v<version>` to keep them separate from stable tags.
The workflow creates a GitHub prerelease. Website feed publication remains a separate operation.

## Main database relocation

Stable first checks its LocalState database. Existing local data takes precedence over publisher data.
Otherwise, it creates a consistent SQLite snapshot of the publisher database, including committed WAL data.
The 200-to-210 migrator reads the local snapshot and writes its staging database in LocalState.
A completed publisher version-210 database can be copied directly after validation.

Failed copies never become migration inputs. Failed migrations retain local checkpoints for retry.
Normal startup remains blocked until migration succeeds or the user explicitly chooses a fresh start.
Publisher databases and legacy credentials remain unchanged. The intelligence database already uses LocalState and does not move.
Beta starts without accounts and never imports legacy publisher data or tokens.

Release the stable migration before broad beta distribution.
Keep the old publisher data during this rollout. Do not copy the local database back when reverting a release.

## Build requirements

- Windows, PowerShell 7, and the .NET SDK selected by `global.json`.
- Visual Studio Build Tools with MSBuild and C++ tools for x86/x64.
- C++ ARM64 tools when All is selected.
- Windows SDK tools: MakeAppx and MakePRI.
- Access to the package sources in `nuget.config`.

If a package source requires authentication, obtain access from the project maintainers before running a build.
Build tools and SDKs must be installed on the developer's machine; the release script discovers them but does not install them.

The script finds these tools automatically. It uses MSBuild through the .NET SDK because the project targets .NET 10.
The Visual Studio application does not need to run.

## Sideload signing setup

Store-only builds do not require Azure credentials. Microsoft signs Store packages during publication.
The script uses SignTool only for the local-update bundle.
Beta and stable sideload builds require the Wino Azure Artifact Signing account and a public-trust certificate profile.
Request access and account details from the project maintainers. Creating an unrelated Azure account does not grant permission to sign official Wino packages.

1. Install the client tools:

   ```powershell
   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
   ```

2. With the account administrator, create or select an Entra application registration authorized for release signing.
3. Record its directory (tenant) ID and application (client) ID.
4. Create a client secret under **Certificates & secrets**.
5. Have the account administrator assign **Artifact Signing Certificate Profile Signer** to the application's service principal at the Wino certificate profile scope.
6. Set the variables in [Local script environment](local-script-environment.md).

The environment guide lists all keys, values, and sources, including translation settings.
The script reads no local signing configuration file.
The publisher must match the Wino sideload identity. The endpoint must match the signing account's region.
The script maps the login variables to Azure's standard names only inside SignTool.
It removes Wino, OpenAI, and Azure variables from build and packaging processes.
It does not call Azure CLI, open a login window, or try another credential provider.

Before the client secret expires, create a replacement and update its environment variable.
After a successful signing run with the replacement, remove the previous secret in Entra ID.

The Azure [signing integration documentation](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) describes the credential providers and timestamp service.

## Outputs

Packages appear under `D:\Wino Releases`, one folder per manifest version. Set `WINO_RELEASES_ROOT` or pass `-OutputRoot` to use another location.

```text
2.0.55.0/
  Beta/
    WinoMail_Beta_2.0.55.0.msixbundle
    WinoMailBetaIsolated.appinstaller
    Dependencies/                       (when required)
  Sideload/
    WinoMail_SideloadRelease_2.0.55.msixbundle
    WinoMail.appinstaller
    Dependencies/                       (when required)
  Store/
    WinoMail_Store_2.0.55.0.msixupload
    WinoMail_Store_2.0.55.0.msixbundle
    WinoMail_Store_TestCertificate.cer
  Symbols/                              (PDB files only)
```

Every channel in a run is packaged from one compilation, so the build stores its PDB files once in the version's
`Symbols/` directory. A later run for the same version, for example Beta after Store, keeps the existing `Symbols/`
when its PDB files are identical. If they differ, that run's channels get their own `Symbols/` directory instead.
Symbol upload is intentionally opt-in: an interactive build asks whether to upload them after packaging.
Declining does not delete the symbols. Upload them later with:

```powershell
pwsh -NoProfile -File .\scripts\release\upload-sentry-symbols.ps1 `
  -Version 2.1.1.0 `
  -SymbolsPath 'D:\Wino Releases\2.1.1.0\Symbols'
```

The upload script uses the `SENTRY_AUTH_TOKEN` environment variable and the
`bkaankose/winomail` Sentry project. It uploads the symbols for one exact
build once, regardless of how many signed packages were produced.

Only selected channels appear. Both sideload bundles have a verified, timestamped signature.
Stable sideload folder and bundle names use three version components. Package manifests and App Installer versions retain all four components.
The Store upload includes the architecture bundle and symbols.
The script does not change the Store upload file.
The separate Store `.msixbundle` retains the Store identity and has a local test signature.
The `.cer` file contains the public test certificate.
The script verifies package identities, architectures, binary hashes, and sideload resource candidates before it completes.

To publish a Store release, upload the `.msixupload` file to the Wino listing in Partner Center with an authorized account.
The script creates App Installer update feeds for the selected sideload channels. It does not upload any files; `publish-releases.ps1` does.

## Install the local Store update

The local package version must be greater than the installed Store version.
Close Wino Mail before you install the package.

1. Import the public certificate for the current user:

   ```powershell
   Import-Certificate -FilePath .\WinoMail_Store_TestCertificate.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```

2. Install the signed Store bundle:

   ```powershell
   Add-AppxPackage .\WinoMail_Store_2.0.55.0.msixbundle
   ```

Windows updates the installed Store-identity package and retains its application data.
The Microsoft Store replaces the test signature after it installs a later published version.

## Sideload website updates

An App Installer file contains a bundle URL, package identity, and update policy. It lets Windows locate subsequent releases through a permanent feed URL.

The default beta feed URL is `https://download.winomail.app/WinoMailBetaIsolated.appinstaller`.
The default stable feed URL is `https://download.winomail.app/WinoMail.appinstaller`.
The bundle and dependencies use a versioned directory, named after the bundle, under `https://download.winomail.app/`.
The environment guide lists optional overrides for these URLs.
The package base URL must end with a slash. Distribution URLs support HTTP and HTTPS.

`download.winomail.app` is the `wino-downloads` Cloudflare R2 bucket. Publish with:

```powershell
pwsh -File .\scripts\release\publish-releases.ps1
```

The script lists the versions under `D:\Wino Releases`, the channels each contains, and the version each channel
currently serves. Pick a version and the channels to publish; you can update one channel, such as Beta, on its own.
Store builds are listed but never uploaded. Use `-Version` and `-Channels Beta,Sideload` with `-NonInteractive` to skip the prompts.
The script needs the R2 credentials described in the [environment guide](local-script-environment.md).

For each selected channel the script:

1. Checks that the App Installer file points at its permanent feed URL and that every package it references exists locally.
2. Uploads the bundle and dependencies under the versioned directory, with `application/msixbundle` (or the matching package type) and `Cache-Control: no-cache`. Files already published under the same key are overwritten, so rebuilding and republishing a version replaces its packages.
3. Verifies the packages over HTTPS, then uploads the App Installer file last with `application/appinstaller` and `no-cache`.
4. Confirms that the feed now serves the new version.

For example:

```text
https://download.winomail.app/WinoMailBetaIsolated.appinstaller
https://download.winomail.app/WinoMail_Beta_2.0.55.0/WinoMail_Beta_2.0.55.0.msixbundle
https://download.winomail.app/WinoMail_Beta_2.0.55.0/Dependencies/...
https://download.winomail.app/WinoMail.appinstaller
https://download.winomail.app/WinoMail_SideloadRelease_2.0.55/WinoMail_SideloadRelease_2.0.55.msixbundle
```

Older versioned bundles stay available, so users mid-update are not affected. The website download buttons link to the two feed URLs.

Beta uses `WinoMail.Beta`. Stable sideload retains `WinoMail.Sideload`. Store stable retains its existing identity.
Each installation has separate databases, credentials, settings, notification hosts, and process coordination.
Windows still selects the handler for shared protocols and file associations. Browser sessions, Windows SSO, and remote mailbox data remain shared.

Do not publish the new beta identity through the old `WinoMailBeta.appinstaller` feed.
Freeze that feed and provide transition instructions. Existing beta users install the new beta separately and configure accounts again.
Do not uninstall their previous application or delete its data.

Users must install through the `.appinstaller` file to enable its update settings.
Opening the bundle directly does not establish the update feed.
The feed requests update checks on launch with a four-hour minimum interval.
It also enables background checks, which Windows schedules every eight hours.
It does not force downgrades or block application startup.
See Microsoft's [update settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings).

## macOS DMG

`scripts/release/build-macos-release.sh` builds the macOS app as a signed, notarized, and stapled DMG.
It runs on a Mac and uses the same manifest version as Windows: `2.0.55.0` becomes app version `2.0.55`.
It does not change `Info.plist` or any other source file.

One-time setup on the Mac:

1. Install Xcode and the .NET SDK selected by `global.json`, then run `dotnet workload install macos`.
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
bash scripts/release/build-macos-release.sh
```

Select **Apple silicon only** or **Universal**. Universal is the default and also runs on Intel Macs.
For unattended runs, use `--non-interactive --arch universal` or `--arch arm64`.

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
See `src/Wino.Core.MacOS.Bindings/README.md` for the integration and for testing an update locally.

The script stops if the DMG already exists. Failed runs keep their logs under `.staging/macos-<run>` in the output root.
The DMG opens a standard window with the app and an Applications link.

Codesign needs the login keychain, which is unavailable to SSH sessions (`errSecInternalComponent`).
To build over SSH, start `screen -dmS winosign` in Terminal on the Mac and run the script inside that session.
This is not required when the script runs in Terminal.

## Mac App Store package

`scripts/release/build-macos-appstore.sh` builds the Mac App Store `.pkg`.
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
bash scripts/release/build-macos-appstore.sh
bash scripts/release/build-macos-appstore.sh --upload
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

If a selected destination exists, the script stops before compilation. It never replaces an existing release.
Move the existing directory or update the source manifest version before another run.

A release lock in the release output folder prevents concurrent runs. The lock closes when the script exits.
Failed work remains under `D:\Wino Releases\.staging\<run-id>` with logs and command records.
The error identifies the failed stage. No channel becomes a completed output until all selected channels pass verification.
Successful runs permanently delete their staging directory after the release outputs are finalized.
The script also removes the `.staging` parent when empty. Diagnostics from earlier failed runs remain available.

For signing failures, verify the secret expiry, profile role assignment, endpoint region, and certificate profile configuration.
Do not distribute an unsigned package from staging.
