# Local script environment

This reference lists environment variables for release packaging and translation maintenance.
Configure only the variables required for the task. Store packaging and translation dry runs do not require signing credentials.

Set variables through Windows **Environment Variables**, under the developer's user account or system settings.
Restart the terminal and its parent application after changes. Existing processes keep their previous environment.
Secret values belong in the environment, never in this document or a script.

## Beta and stable sideload releases

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
| `WINO_BETA_RELEASE_APPINSTALLER_URI` | `http://download.winomail.app/WinoMailBeta.appinstaller` | Beta update feed URL. Obtain an override from the distribution-site administrator. |
| `WINO_BETA_RELEASE_PACKAGE_BASE_URI` | Parent URL of the App Installer URL; normally `http://download.winomail.app/` | Root for versioned beta downloads. Use HTTP or HTTPS and a trailing slash. |
| `WINO_BETA_RELEASE_SIGNING_DLIB_PATH` | Unset; tools are discovered automatically | Custom installation only. Full path to the x64 `Azure.CodeSigning.Dlib.dll`. |
| `WINO_SIDELOAD_RELEASE_APPINSTALLER_URI` | `http://download.winomail.app/WinoMail.appinstaller` | Stable sideload update feed URL. Obtain an override from the distribution-site administrator. |
| `WINO_SIDELOAD_RELEASE_PACKAGE_BASE_URI` | Parent URL of the stable App Installer URL; normally `http://download.winomail.app/` | Root for stable sideload downloads. Use HTTP or HTTPS and a trailing slash. |

Only selected channels require valid feed settings. Beta and stable sideload must use different feed URLs when selected together.

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

Run these commands from the repository root with Python 3.10 or later:

```powershell
python .\scripts\translate_resources.py --dry-run
python .\scripts\validate_resources.py --dry-run
```

Use `--apply` instead of `--dry-run` to write changes. Translation requests use the configured OpenAI project.
Use `--locales`, such as `--locales de_DE pl_PL`, to limit the affected languages.
The shared rules in `.config/translation_allowlist.json` exclude legitimate English terms from the validation report.

See [Local release packages](releases.md) for build prerequisites, package outputs, and publication steps.
