# Local release packages

This guide explains how to build release artifacts from a Wino Mail checkout and prepare them for distribution.
The release script creates packages locally. Publication to Microsoft Store or the download website is a separate maintainer task.

## Channels

| Channel | Distribution | Required access |
| --- | --- | --- |
| Store | Upload package for Microsoft Partner Center | Package sources for builds; access to the Wino Partner Center listing for publication. |
| Beta | Signed bundle with the beta App Installer feed | Wino Artifact Signing account; download-site access for publication. |
| Stable sideload | Same signed bundle with the stable App Installer feed | Same signing account; download-site access for publication. |

All channels use the same source and compiled binaries. Selecting Beta does not enable a separate compiler configuration or feature set.
The channel controls artifact naming and distribution. Release maintainers decide which version to publish to each feed.

## Run the script

After installing the build prerequisites below, run this command from the repository root in PowerShell 7:

```powershell
pwsh -NoProfile -File .\scripts\build-releases.ps1
```

Select **yes** or **no** for Store, Beta, and stable sideload. Then select **x64** or **All**.
All includes x86, x64, and ARM64. If all channels are No, the script exits.

The script reads the version from `src/Wino.Mail.WinUI/Package.appxmanifest`. It does not increment the version or change the source manifest.
The version must have four numeric components, a nonzero major component, and a zero revision.
For example, `2.0.55.0` is valid.

The script compiles Release once for each selected architecture. All three channels use the same compiled binaries.
Sideload packaging changes the package identity and resource index, then creates a new bundle without compilation.
The script signs this bundle once, then copies it into the selected Beta and stable sideload folders.
It verifies that each copy matches the signed bundle. Each sideload channel receives its own App Installer update feed.
The script does not install or launch packages.

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

Store-only builds do not require Azure credentials or signing tools. Microsoft signs Store packages during publication.
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

Packages appear under `src/Wino.Mail.WinUI/AppPackages`:

```text
WinoMail_Beta_2.0.55.0/
  WinoMail_Beta_2.0.55.0.msixbundle
  WinoMailBeta.appinstaller
  Dependencies/                         (when required)
WinoMail_SideloadRelease_2.0.55/
  WinoMail_SideloadRelease_2.0.55.msixbundle
  WinoMail.appinstaller
  Dependencies/                         (when required)
WinoMail_Store_2.0.55.0/
  WinoMail_Store_2.0.55.0.msixupload
```

Only selected channels appear. Both sideload bundles have a verified, timestamped signature.
Stable sideload folder and bundle names use three version components. Package manifests and App Installer versions retain all four components.
The Store upload includes the architecture bundle and symbols.
The script verifies package identities, architectures, binary hashes, and sideload resource candidates before it completes.

To publish a Store release, upload the `.msixupload` file to the Wino listing in Partner Center with an authorized account.
The script creates App Installer update feeds for the selected sideload channels. It does not upload any files.

## Sideload website updates

An App Installer file contains a bundle URL, package identity, and update policy. It lets Windows locate subsequent releases through a permanent feed URL.

The default beta feed URL is `http://download.winomail.app/WinoMailBeta.appinstaller`.
The default stable feed URL is `http://download.winomail.app/WinoMail.appinstaller`.
The bundle and dependencies use a versioned directory under `http://download.winomail.app/`.
The environment guide lists optional overrides for these URLs.
The package base URL must end with a slash. Distribution URLs support HTTP and HTTPS.

The default URLs belong to the official Wino distribution site. Publishing there requires access from the project maintainers.
For an authorized publication, complete these steps for each selected sideload channel:

1. Upload the channel directory, including its bundle and dependencies, under the package base URL.
2. Verify that the package URLs in the channel's App Installer file are accessible.
3. Upload that App Installer file to its permanent feed URL last.
4. Link the website download button to the corresponding feed URL.

For example:

```text
http://download.winomail.app/WinoMailBeta.appinstaller
http://download.winomail.app/WinoMail_Beta_2.0.55.0/WinoMail_Beta_2.0.55.0.msixbundle
http://download.winomail.app/WinoMail_Beta_2.0.55.0/Dependencies/...
http://download.winomail.app/WinoMail.appinstaller
http://download.winomail.app/WinoMail_SideloadRelease_2.0.55/WinoMail_SideloadRelease_2.0.55.msixbundle
```

Configure the website to serve `.appinstaller` as `application/appinstaller` and `.msixbundle` as `application/msixbundle`.
Use short cache lifetimes for both feed files. Keep older versioned bundles available during updates.

Beta and stable sideload share the `WinoMail.Sideload` package identity. They update the same installed application and cannot be installed side by side.
Their download folders and update feed URLs are separate. Publish each feed only when its channel is ready.

Users must install through the `.appinstaller` file to enable its update settings.
Opening the bundle directly does not establish the update feed.
The feed requests update checks on launch with a four-hour minimum interval.
It also enables background checks, which Windows schedules every eight hours.
It does not force downgrades or block application startup.
See Microsoft's [update settings](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings).

## Failures and repeated builds

If a selected destination exists, the script stops before compilation. It never replaces an existing release.
Move the existing directory or update the source manifest version before another run.

A release lock prevents concurrent runs in this checkout. The lock closes when the script exits.
Failed work remains under `AppPackages/.staging/<run-id>` with logs and command records.
The error identifies the failed stage. No channel becomes a completed output until all selected channels pass verification.
Successful runs permanently delete their staging directory after the release outputs are finalized.
The script also removes the `.staging` parent when empty. Diagnostics from earlier failed runs remain available.

For signing failures, verify the secret expiry, profile role assignment, endpoint region, and certificate profile configuration.
Do not distribute an unsigned package from staging.

## Script tests

```powershell
pwsh -NoProfile -File .\tests\scripts\Build-Releases.Tests.ps1
```

These tests use temporary fixtures and process substitutes. A real release build provides the package and Native AOT verification.
