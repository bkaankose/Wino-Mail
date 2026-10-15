# T3 Wino.Platform.Android (done, 14 Oct 2026)

## Files

- `src/Wino.Platform.Android` (`net10.0-android`, min 23, references Domain, Services, Authentication; Core not needed): `AndroidPaths`, `AndroidPlatformRegistration` (`AndroidPlatformBootstrap.Create(Context)`, `AddAndroidPlatformServices(bootstrap | context, Func<Activity?>)`), `Security/AndroidKeystoreProtectionKeyProvider`, `Services/` (Dispatcher, ExternalLauncher, Clipboard, ReaderRuntime, UserPresence, Shortcut, TaskCompletionSound, MicrosoftStore), `Resources/xml/wino_file_paths.xml`.
- `WinoMail.Android.slnx` (platform project, shared projects, tests).
- Shared moves (no third copy): `Wino.Services/Platform/` `HostAppMetadataService` (was `AppleAppMetadataService`), `LauncherAttachmentPlatformService`, `UnavailableStartupIntegrationService`, `UnavailableSmimeCertificateService` (were iOS-only). Mac `Composition` and `IOSPlatformRegistration` use them.
- `Wino.Services/Security/ProtectedFileSecureItemStore` + `tests/.../ProtectedFileSecureItemStoreTests` (9 tests).

## Registrations

Same shared list as Mac/iOS (envelope protector, four `SecureItem*` stores, `JsonFileConfigurationService`, `FileSystemFileService`, `ConfigurationStatePersistenceService`, `PreferencesService`, `MailAuthenticatorConfiguration`), plus `IDispatcher` (main looper, inline on main), launcher, clipboard, reader runtime, presence (interruption filter), Ctrl shortcuts, task sound, store no-op (review opens the Play listing), metadata (version name; version code = Sentry dist), `FileSystemApplicationResourceResolver` over `FilesDir/PackagedResources`. Capabilities: Printing, PdfExport, Notifications, NotificationActions, GeneralActivation true; everything else false, including RemoteNotifications/PushRelayOptIn until FCM works (T11).
Not registered: Outlook host, Google receiver (T5); notifications, badge, sounds, relay token/device info (T11); theme, dialogs, navigation (head).

## Paths and Keystore

`NoBackupFilesDir/data` (data root: DB, `preferences.json`, `Logs/wino.log`), `/secure` (items), `/protection/wino-protection-v1.key`; `CacheDir/temp`, `CacheDir/shared-files`. Deviation: logs stay at `<data root>/Logs`, not `FilesDir/Logs`, because the lifecycle coordinator, About export and protocol logs all derive it from `ApplicationDataFolderPath`.
Key: a 32-byte random data key, wrapped by the non-exportable AES-256-GCM Keystore key `wino-protection-v1` (AAD `wino-protection-v1:<package>`). File: `WPKW`, version 1, IV length 12, IV, 48-byte ciphertext and tag. No file means missing (null, then `AccountCredentialMissingException`). Bad layout or tag, wrong length, or a Keystore key that is lost or invalidated means corrupt (`CryptographicException`). Keystore or provider failure means unavailable (`PlatformNotSupportedException`). The key is created only when the data, secure and key paths are all absent; no-overwrite rename.
Secure items: `secure/<sha256(scope)>/<sha256(account)>.item` hex. Content is a WMKP envelope with context `wino-secure-item:v1` + length-prefixed scope/account. Writes go to a temp file, then fsync and rename. `SecureItemAccess` is a no-op: no_backup + Auto Backup off + CE storage already gives this-device/after-first-unlock.
Risk: a Keystore key lost on an existing install is never re-provisioned (as on Apple).

## Head manifest (T4)

```xml
<application android:allowBackup="false" ...>
  <provider android:name="androidx.core.content.FileProvider" android:authorities="${applicationId}.fileprovider"
            android:exported="false" android:grantUriPermissions="true">
    <meta-data android:name="android.support.FILE_PROVIDER_PATHS" android:resource="@xml/wino_file_paths" />
  </provider>
</application>
<queries><intent><action android:name="android.support.customtabs.action.CustomTabsService" /></intent></queries>
```
Authority must be `<package>.fileprovider` (`AndroidExternalLauncher.GetFileProviderAuthority`). No permissions needed. Sentry: the head must keep `AndroidEnableAssemblyCompression` enabled (Sentry build error otherwise); `WinoLogger` needs nothing else. The head copies the assets the shared services read as packaged resources into `PackagedResourceRoot`.

## Packages

Xamarin.AndroidX.Browser 1.10.0.1, .Core 1.19.0.1, .WebKit 1.17.0. Also Lifecycle.Common.Java8 and Lifecycle.Process 2.11.0.1: Sentry.Bindings.Android 6.12 pins them below 2.9.3, which caused NU1608, so they are lifted to the graph's Lifecycle version.

## Verification

- Android Debug and Release (`-p:NuGetAudit=false`) builds of the platform project: 0 errors, 0 warnings (plus the existing Pkcs NU1902/1903 in Debug).
- `dotnet test tests/Wino.Core.Tests -p:WinoTargetPlatform=MacOS`: 1215 passed, 5 failed (the same 5 known failures).
- macOS Debug osx-arm64 and osx-x64: succeeded. iOS Debug Simulator (`Wino.Mail.iOS`, iossimulator-arm64): succeeded.
- Not run on a device: Keystore, FileProvider, Custom Tabs, WebView check wait for T4. Windows is the owner's handoff (shared `Wino.Services` change): Debug x64, Release gate, Core tests.
