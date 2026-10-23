# Windows release packages

This guide explains how to build Windows release artifacts from a Mail-Cal checkout and prepare them for distribution.
The [release index](releases.md) lists the shared steps and the macOS guide.
The release script creates packages locally. Publication to Microsoft Store or the download website is a separate maintainer task.

## Channels

| Channel | Distribution | Required access |
| --- | --- | --- |
| Store | Partner Center upload and signed local-update bundle | Package sources and a local Store-subject test certificate. Partner Center access is required for publication. |
| Beta | Signed bundle with the beta App Installer feed | Mail-Cal Artifact Signing account; download-site access for publication. |
| Stable sideload | Separate signed bundle with the stable App Installer feed | Same signing account; download-site access for publication. |

All channels use the same source and compiled binaries. Selecting Beta does not enable a separate compiler configuration or feature set.
Each distribution has its own package identity and runtime profile. All three can run at the same time.
Release maintainers decide which version to publish to each feed.

## Before you run the script

Complete the shared steps in the [release index](releases.md#before-any-release): version, What's New notes, and validation.
The release script runs the same validation. If the notes are missing or invalid, it shows the failures and asks whether to continue.
With `-NonInteractive`, it only shows a warning.
The What's New button in the title bar appears only when notes exist for the installed version.

## Run the script

After installing the build prerequisites below, run this command from the repository root in PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\release\windows\build-releases.ps1
```

The script first asks whether to translate missing resources (see [Translations](#translations)).
Then select **yes** or **no** for Store, Beta, and stable sideload. Then select **x64** or **All**.
All includes x86, x64, and ARM64. If all channels are No, the script exits.

The script reads the version from `src/MailCal.Mail.WinUI/Package.appxmanifest`. It does not increment the version or change the source manifest.
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
pwsh -NoProfile -File .\scripts\release\windows\build-releases.ps1 -NonInteractive -Store -Architectures x64 -StoreTestCertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
```

You can also set `MAILCAL_STORE_TEST_CERTIFICATE_THUMBPRINT` for repeated builds.

## Translations

Before any other question, the script asks whether to run `scripts/localization/translate_resources.py --apply`.
Yes translates every missing key in every locale through the OpenAI API, then continues to the release questions.
The run requires `MAILCAL_OPENAI_API_KEY`; see the [environment guide](local-script-environment.md#translations). If the key is missing or translation fails, the script stops before compilation.
Translated `resources.json` files remain uncommitted changes in the checkout, and the build packages them. Uncommitted changes do not block a release.

Use `-Translate` or `-SkipTranslation` to answer without the prompt. With `-NonInteractive` and neither switch, translation is skipped.

```powershell
pwsh -NoProfile -File .\scripts\release\windows\build-releases.ps1 -NonInteractive -Translate -Store -Architectures x64
```

## Beta artwork and runtime profiles

The source `release-profile.json` describes Store stable. `scripts/release/windows/profiles` contains the two sideload profiles.
The packager checks profile identity, notification IDs, and the session-start task's COM class ID (`StartupTaskActivatorId`) against the generated manifest.
The app reads its profile before activation. Mutex and event names use the installed package family name, without the version.

Supply beta artwork under `scripts/release/Assets/Beta`, with paths that match the packaged assets.
See the README in that directory for the asset inventory command.
The packager requires every declared branding asset. It stops if an asset is missing.
Stable artwork is not a fallback for beta. Compiled executable metadata remains unchanged.

For a different artwork directory, use `-BetaAssetsPath`:

```powershell
pwsh -NoProfile -File .\scripts\release\windows\build-releases.ps1 -NonInteractive -Store -Beta -Sideload -Architectures x64 -BetaAssetsPath D:\MailCalBetaArtwork
```

For all architectures, supply `x86,x64,ARM64` from PowerShell.
The CI beta workflow uses this same script and requires the artwork in the checkout.
CI uses the `MAILCAL_BETA_RELEASE_*` secrets listed below. It no longer uses the old PFX secret.
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
Beta and stable sideload builds require the Mail-Cal Azure Artifact Signing account and a public-trust certificate profile.
Request access and account details from the project maintainers. Creating an unrelated Azure account does not grant permission to sign official Mail-Cal packages.

1. Install the client tools:

   ```powershell
   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
   ```

2. With the account administrator, create or select an Entra application registration authorized for release signing.
3. Record its directory (tenant) ID and application (client) ID.
4. Create a client secret under **Certificates & secrets**.
5. Have the account administrator assign **Artifact Signing Certificate Profile Signer** to the application's service principal at the Mail-Cal certificate profile scope.
6. Set the variables in [Local script environment](local-script-environment.md).

The environment guide lists all keys, values, and sources, including translation settings.
The script reads no local signing configuration file.
The publisher must match the Mail-Cal sideload identity. The endpoint must match the signing account's region.
The script maps the login variables to Azure's standard names only inside SignTool.
It removes Mail-Cal, OpenAI, and Azure variables from build and packaging processes.
It does not call Azure CLI, open a login window, or try another credential provider.

Before the client secret expires, create a replacement and update its environment variable.
After a successful signing run with the replacement, remove the previous secret in Entra ID.

The Azure [signing integration documentation](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations) describes the credential providers and timestamp service.

## Outputs

Packages appear under `D:\Mail-Cal Releases`, one folder per manifest version. Set `MAILCAL_RELEASES_ROOT` or pass `-OutputRoot` to use another location.

```text
2.0.55.0/
  Beta/
    MailCal_Beta_2.0.55.0.msixbundle
    MailCalBetaIsolated.appinstaller
    Dependencies/                       (when required)
  Sideload/
    MailCal_SideloadRelease_2.0.55.msixbundle
    MailCal.appinstaller
    Dependencies/                       (when required)
  Store/
    MailCal_Store_2.0.55.0.msixupload
    MailCal_Store_2.0.55.0.msixbundle
    MailCal_Store_TestCertificate.cer
  Symbols/<architecture>/               (MailCal.Mail.WinUI.pdb, MailCal.NotificationHost.pdb, native dependency PDBs)
```

Every channel in a run is packaged from one compilation, so the build stores its PDB files once in the version's
`Symbols/` directory. These are the same files the `.appxsym` packages contain. `MailCal.Mail.WinUI.pdb` is the
Native AOT image PDB: Release crashes reference only that image, so the build fails if it is missing. Managed
library PDBs are not kept because Native AOT compiles that code into the image. A later run for the same version, for example Beta after Store, keeps the existing `Symbols/`
when its PDB files are identical. If they differ, that run's channels get their own `Symbols/` directory instead.
The symbols stay local; this fork sends no crash reports or symbols anywhere.

Only selected channels appear. Both sideload bundles have a verified, timestamped signature.
Stable sideload folder and bundle names use three version components. Package manifests and App Installer versions retain all four components.
The Store upload includes the architecture bundle and symbols.
The script does not change the Store upload file.
The separate Store `.msixbundle` retains the Store identity and has a local test signature.
The `.cer` file contains the public test certificate.
The script verifies package identities, architectures, binary hashes, and sideload resource candidates before it completes.

To publish a Store release, upload the `.msixupload` file to the Mail-Cal listing in Partner Center with an authorized account.
The script creates App Installer update feeds for the selected sideload channels. It does not upload any files.

## Install the local Store update

The local package version must be greater than the installed Store version.
Close Mail-Cal before you install the package.

1. Import the public certificate for the current user:

   ```powershell
   Import-Certificate -FilePath .\MailCal_Store_TestCertificate.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```

2. Install the signed Store bundle:

   ```powershell
   Add-AppxPackage .\MailCal_Store_2.0.55.0.msixbundle
   ```

Windows updates the installed Store-identity package and retains its application data.
The Microsoft Store replaces the test signature after it installs a later published version.

## Sideload update feeds

An App Installer file contains a bundle URL, package identity, and update policy. It lets Windows locate subsequent releases through a permanent feed URL.

There is no hosted default. Set the feed URL for each sideload channel you build, as the [environment guide](local-script-environment.md) describes; the package base URL defaults to the feed URL's folder.
The package base URL must end with a slash. Distribution URLs support HTTP and HTTPS.
Upload the bundle, its `Dependencies` folder and the `.appinstaller` file to that location yourself, the App Installer file last.

Users must install through the `.appinstaller` file to enable its update settings.
Opening the bundle directly does not establish the update feed.
The feed requests update checks on launch with a four-hour minimum interval.
It also enables background checks, which Windows schedules every eight hours.
It does not force downgrades or block application startup.
See Microsoft's [update settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings).

## Failures and repeated builds

If a selected destination exists, the script stops before compilation. It never replaces an existing release.
Move the existing directory or update the source manifest version before another run.

A release lock in the release output folder prevents concurrent runs. The lock closes when the script exits.
Failed work remains under `D:\Mail-Cal Releases\.staging\<run-id>` with logs and command records.
The error identifies the failed stage. No channel becomes a completed output until all selected channels pass verification.
Successful runs permanently delete their staging directory after the release outputs are finalized.
The script also removes the `.staging` parent when empty. Diagnostics from earlier failed runs remain available.

For signing failures, verify the secret expiry, profile role assignment, endpoint region, and certificate profile configuration.
Do not distribute an unsigned package from staging.
