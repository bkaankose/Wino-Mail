# Local script environment

This reference lists environment variables for release packaging and translation maintenance.
Configure only the variables required for the task and platform. Store packaging and translation dry runs do not require signing credentials.

| Platform | Sections | Where to set variables |
| --- | --- | --- |
| Windows | Windows Store local-update signing, Windows sideload signing, Windows release output, Windows publication | Windows **Environment Variables**, under the developer's user account or system settings |
| macOS | macOS notarization and App Store Connect | The shell profile, such as `~/.zprofile`, with `export KEY=value` |
| Both | Translations | Either of the above, on the host that runs the script |

Restart the terminal and its parent application after changes. Existing processes keep their previous environment.
Secret values belong in the environment, never in this document or a script.

## Windows Store local-update signing

Store builds use a local test certificate to sign the installable `.msixbundle`.
The Partner Center `.msixupload` does not use this certificate.

| Optional key | Default or value | Purpose and source |
| --- | --- | --- |
| `WINO_STORE_TEST_CERTIFICATE_THUMBPRINT` | Newest valid matching certificate | Certificate in `Cert:\CurrentUser\My`. Its subject must match the Store manifest publisher. |

The command-line `-StoreTestCertificateThumbprint` value overrides this environment variable.
The thumbprint is not a secret.

## Windows sideload signing (Beta and stable sideload)

These seven variables are required when Beta or stable sideload is selected. Store-only releases need none of them.
Both sideload channels use the same signing account and the `WINO_BETA_RELEASE_` variables, despite the prefix's name.
Official releases require access to the Wino Artifact Signing account. Request account details and signing access from the project maintainers.

| Key | Value | Purpose and source |
| --- | --- | --- |
| `WINO_BETA_RELEASE_AZURE_TENANT_ID` | Directory GUID | Signing login. Entra ID → App registrations → Overview → Directory ID. |
| `WINO_BETA_RELEASE_AZURE_CLIENT_ID` | Authorized application GUID | Signing login. The same page → Application ID. |
| `WINO_BETA_RELEASE_AZURE_CLIENT_SECRET` | Client secret value | Signing login. App registration → Certificates & secrets. Use the value, not the secret ID. |
| `WINO_BETA_RELEASE_SIGNING_ENDPOINT` | `https://<region>.codesigning.azure.net/` | Signing region. Azure Artifact Signing account → Overview. |
| `WINO_BETA_RELEASE_SIGNING_ACCOUNT_NAME` | Wino signing account name | Signing account. Obtain from the maintainers or Azure account Overview. |
| `WINO_BETA_RELEASE_SIGNING_CERTIFICATE_PROFILE_NAME` | Wino certificate profile name | Signing certificate. Azure account → Certificate profiles. |
| `WINO_BETA_RELEASE_PUBLISHER_SUBJECT` | `CN=Burak Kaan Köse, O=Burak Kaan Köse, L=Wroclaw, S=Dolnośląskie, C=PL` | Package publisher required by the script. Must match the signing certificate subject. |

The publisher subject identifies the Wino package, not the developer running the script. Keep this value unchanged for official releases.

Assign **Artifact Signing Certificate Profile Signer** to the application's service principal at the certificate profile scope.
Install the [Artifact Signing client tools](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations).
The script passes the three login values to SignTool through its environment. Azure CLI is not used.

| Optional key | Default or value | Purpose and source |
| --- | --- | --- |
| `WINO_BETA_RELEASE_APPINSTALLER_URI` | `https://download.winomail.app/WinoMailBetaIsolated.appinstaller` | Beta update feed URL. Obtain an override from the distribution-site administrator. |
| `WINO_BETA_RELEASE_PACKAGE_BASE_URI` | Parent URL of the App Installer URL; normally `https://download.winomail.app/` | Root for versioned beta downloads. Use HTTP or HTTPS and a trailing slash. |
| `WINO_BETA_RELEASE_SIGNING_DLIB_PATH` | Unset; tools are discovered automatically | Custom installation only. Full path to the x64 `Azure.CodeSigning.Dlib.dll`. |
| `WINO_SIDELOAD_RELEASE_APPINSTALLER_URI` | `https://download.winomail.app/WinoMail.appinstaller` | Stable sideload update feed URL. Obtain an override from the distribution-site administrator. |
| `WINO_SIDELOAD_RELEASE_PACKAGE_BASE_URI` | Parent URL of the stable App Installer URL; normally `https://download.winomail.app/` | Root for stable sideload downloads. Use HTTP or HTTPS and a trailing slash. |

Only selected channels require valid feed settings. Beta and stable sideload must use different feed URLs when selected together.

## Windows release output

| Optional key | Default | Purpose |
| --- | --- | --- |
| `WINO_RELEASES_ROOT` | `D:\Wino Releases` | Where `build-releases.ps1` writes packages and `publish-releases.ps1` looks for them. The `-OutputRoot` and `-ReleasesRoot` parameters override it for one run. On a Mac the default is `~/Wino Releases`. |

## macOS notarization and App Store Connect

`build-macos-release.sh` runs on a Mac and submits the DMG with `xcrun notarytool`.
Set these variables in the shell profile, such as `~/.zprofile`, with `export KEY=value`. Open a new Terminal window after changes.

| Key | Value | Purpose and source |
| --- | --- | --- |
| `WINO_NOTARY_KEY_PATH` | Full path to `AuthKey_<key id>.p8` | App Store Connect API key file. Users and Access → Integrations → App Store Connect API. The file downloads once; keep it outside the repository. |
| `WINO_NOTARY_KEY_ID` | Ten-character key ID | The same page → Key ID. Not a secret. |
| `WINO_NOTARY_ISSUER_ID` | Issuer GUID | The same page → Issuer ID. Not a secret. |

The key needs the **Developer** role or higher. The script passes the key to `notarytool` and never prints or copies it.
`build-macos-appstore.sh` uses the same variables to validate and upload the Mac App Store package with `xcrun altool`.
`WINO_RELEASES_ROOT` also applies on the Mac. Its default there is `~/Wino Releases`.

## Publication to download.winomail.app (Windows Beta and sideload, macOS DMG)

`publish-releases.ps1` on Windows and `publish-macos-release.sh` on the Mac upload to the `wino-downloads` Cloudflare R2
bucket that serves `https://download.winomail.app`. Both use R2's S3-compatible API, because packages exceed the 300 MB
single-request limit of the Cloudflare API. Set the same three variables on each host that publishes; on the Mac, in the
shell profile with `export KEY=value`.

| Key | Value | Purpose and source |
| --- | --- | --- |
| `WINO_R2_ACCOUNT_ID` | Cloudflare account ID | Cloudflare dashboard → R2 Object Storage → Overview → Account details. Not a secret. |
| `WINO_R2_ACCESS_KEY_ID` | R2 API token access key ID | R2 Object Storage → Manage API tokens → Create API token. Permission **Object Read & Write**, applied to the `wino-downloads` bucket only. |
| `WINO_R2_SECRET_ACCESS_KEY` | R2 API token secret access key | Shown once when the token is created. Store it only in this environment variable. |

## Translations

`translate_resources.py` synchronizes locale keys and translates missing values.
`validate_resources.py` identifies values that still match English and can translate those values again.
Both scripts use the same API key. Dry runs need no key and make no API calls.

| Key | Value or default | Purpose and source |
| --- | --- | --- |
| `WINO_OPENAI_API_KEY` | OpenAI project API key | Required for translation requests. Create a key through [OpenAI API keys](https://platform.openai.com/api-keys). |
| `WINO_TRANSLATION_MODEL` | `gpt-5.6-luna` | Optional model for `translate_resources.py`. Select a model available to the configured OpenAI project. |
| `WINO_TRANSLATION_VALIDATION_MODEL` | `gpt-5-nano` | Optional repair model for `validate_resources.py`. Select a model available to the configured OpenAI project. |

`--model` overrides the corresponding environment variable for one run.
Both scripts read credentials only from `WINO_OPENAI_API_KEY`.

The release scripts on both platforms can run `translate_resources.py --apply` before building; that run requires `WINO_OPENAI_API_KEY`.
Run these commands from the repository root with Python 3.10 or later (`python3` on macOS):

```powershell
python .\scripts\localization\translate_resources.py --dry-run
python .\scripts\localization\validate_resources.py --dry-run
```

Use `--apply` instead of `--dry-run` to write changes. Translation requests use the configured OpenAI project.
Use `--locales`, such as `--locales de_DE pl_PL`, to limit the affected languages.
The shared rules in `.config/translation_allowlist.json` exclude legitimate English terms from the validation report.

See the [release index](releases.md) for the Windows and macOS build prerequisites, package outputs, and publication steps.
