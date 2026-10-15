# T2 shared moves (done, 14 Oct 2026)

## New shared types

| Type | Location | Replaces |
| --- | --- | --- |
| `ISecureItemStore`, `SecureItemAccess` | `Wino.Core.Domain` (Interfaces, Enums) | `AppleKeychainStore` API (now implements it) |
| `ISecretProtectionKeyProvider` | `Wino.Core.Domain/Interfaces` | key access inside `AppleSecretProtector` |
| `AesGcmEnvelopeSecretProtector` | `Wino.Services/Security` | `AppleSecretProtector` (same `WMKP` v1 bytes and AAD) |
| `SecureItemSecretProtectionKeyProvider` | `Wino.Services/Security` | key item `protection` / `aes256-gcm-v1` |
| `SecureItemAccountCredentialPersistence` | `Wino.Services/Security` | `AppleAccountCredentialPersistence` |
| `SecureItemDavCredentialStore` | `Wino.Services/Dav` | `AppleDavCredentialStore` |
| `SecureItemPushRelayDeviceIdentityStore` | `Wino.Services/PushRelay` | `AppleKeychainPushRelayDeviceIdentityStore` |
| `SecureItemGoogleTokenStore` | `Wino.Authentication` | `AppleGoogleTokenStore` |
| `FileSystemFileService`, `JsonFileConfigurationService`, `ConfigurationStatePersistenceService` | `Wino.Services` | `AppleFileService`, `AppleConfigurationService`, `AppleStatePersistenceService` |

Windows equivalents are WinRT-based (nothing to reuse). Scopes, accounts, the `wino-keychain:v1:` prefix, JSON shapes and `preferences.json` are unchanged.

Removed: the eight Apple files above and `AppleCredentialMissingException`. Shared code throws the base `AccountCredentialMissingException`, which every caller already catches. `AppleKeychainStore.Write` takes `SecureItemAccess` in place of `SecAccessible`; the push identity still gets `AfterFirstUnlockThisDeviceOnly`.
Registrations updated: `src/Wino.Mail.MacOS/Composition.cs` and `src/Wino.Platform.iOS/IOSPlatformRegistration.cs` (`IOSPlatformBootstrap.Protector` is now `AesGcmEnvelopeSecretProtector`; `ISecureItemStore` is registered too).

Nullable fixes: these warnings existed before the move and were hidden in the Apple project. `IDavCredentialStore.GetPasswordAsync` and `IGoogleTokenStore.ReadAsync` now return nullable types. The three `CustomServerInformation` password properties are `string?`. Restored Wino Account tokens fall back to `string.Empty` (the entity default) when a revision stores null.

## Android plug-in

Implement `ISecureItemStore` (protected files with atomic replace, or Keystore-backed) and `ISecretProtectionKeyProvider`. The provider can be a Keystore-wrapped 32-byte key; it cannot be `SecureItemSecretProtectionKeyProvider` over a store that the protector itself encrypts. Then register `AesGcmEnvelopeSecretProtector`, the four `SecureItem*` stores, `JsonFileConfigurationService(dataRoot)`, `FileSystemFileService` and `ConfigurationStatePersistenceService`, as the Mac head does. `SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock` can be a no-op because Auto Backup is off.

## Tests (`tests/Wino.Core.Tests`)

New: `Security/AesGcmEnvelopeSecretProtectorTests.cs` (round trip, fixed golden envelope from an independent AesGcm run, AAD layout, tamper of nonce/tag/ciphertext, wrong context or identity, header and length checks, missing or invalid key, provisioning), `Services/SecureItemAccountCredentialPersistenceTests.cs`, `Helpers/InMemorySecureItemStore.cs`.

## Verification

- `dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug -p:WinoTargetPlatform=MacOS`: 1206 passed, 5 failed. The same 5 fail on a clean HEAD worktree with the T1 changes: MailRecipientHistory, HtmlPreviewVisitor host-like hrefs, AttachmentFileService `..\..\payload.exe`, and 2 AccountAttentionNetworkGate tests.
- macOS Debug `osx-arm64` and `osx-x64` (commands as in development-macos.md): succeeded, 0 errors.
- iOS Debug Simulator (`-r iossimulator-arm64 -p:WinoTargetPlatform=iOS`): every project compiled, including `Wino.Platform.iOS` and `Wino.Mail.iOS`. The build then failed at `AOTCompile`: `mono-aot-cross` 10.0.12 is missing because the 17:06 Android workload install replaced the pack with 10.0.13 (workload version 10.0.402). Toolchain issue, not T2; workload repair was not run (shared toolchain).
- Release gate (AOT, trim, warnings as errors): `dotnet build src/Wino.Services/Wino.Services.csproj -c Release -p:WinoTargetPlatform=MacOS -p:NuGetAudit=false` and the same for `src/Wino.Authentication/Wino.Authentication.csproj`: 0 warnings, 0 errors. Without `NuGetAudit=false`, restore fails before T2 code compiles (NU1902/NU1903, `System.Security.Cryptography.Pkcs` 10.0.0).

## Pending for the owner (Windows)

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug
```

Not run: launching the Mac or iOS apps to confirm that existing Keychain credentials and the envelope still read at runtime. Byte compatibility rests on the unchanged code paths plus the golden-vector test.
