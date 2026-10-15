# Android release packages

This guide explains how to build the Android release APK and App Bundle (AAB).
The [release index](releases.md) lists the shared steps. Toolchain, emulators, and sign-in registrations are in the [Android development guide](harness/development-android.md).

Google Play is the distribution target, but nothing exists on the Play side yet: no Play Console app, upload key, or store listing. Local testing uses APKs installed on emulators or devices. There is no Android release script.

## Version

The head reads the version from `src/Wino.Mail.WinUI/Package.appxmanifest`, like the Apple heads: `2.2.2.0` becomes version name `2.2.2` and version code `major * 1000000 + minor * 1000 + patch` (`2002002`), so each release has a higher code. The version code is also the Sentry `dist`. Override with `-p:ApplicationDisplayVersion=... -p:ApplicationVersion=...` only for a test build.

## Release build

Release builds of the head use Mono full AOT with LLVM and full trimming for `android-arm64` ([build/Wino.Android.targets](../build/Wino.Android.targets)). `PublishAot` stays false.

```sh
dotnet publish src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Release -r android-arm64 -p:WinoTargetPlatform=Android \
  -p:AndroidPackageFormats='"apk;aab"' -p:NuGetAudit=false
```

A clean publish takes about 8 to 9 minutes on an Apple silicon Mac. Delete `src/Wino.Mail.Android/obj/Release` and `bin/Release` before a release build: an incremental Release build after trimming inputs changed produced an APK that crashed at start with a `TypeLoadException` in `Mono.Android.Runtime`.

Full trimming removes managed types that nothing references in code. Views that layouts inflate by their Java name are kept by `ILLink.Descriptors.xml` in `controls/Wino.Presentation.Android` and `controls/Wino.Mail.Controls.Android`; add every new `[Register]`ed view that only XML uses there, or the Release app fails with `ClassNotFoundException` while inflating. Debug builds do not trim, so they do not show the failure.

The signed package is `com.winomail.android-Signed.apk` or `.aab` in `src/Wino.Mail.Android/bin/Release/net10.0-android/android-arm64/publish/`. Without signing properties the SDK signs with the machine's debug keystore, which is enough for local testing (`adb install -r` works for Release APKs, which contain every assembly). Play rejects a debug-signed AAB.

Known issue: restore of a Release build fails with NU1902/NU1903 for `System.Security.Cryptography.Pkcs` 10.0.0, a pre-existing advisory in the shared graph. Until the package is updated, local Release builds pass `-p:NuGetAudit=false`. This is a workaround for a known issue, not release policy.

## Signing with the upload key

The upload key does not exist yet. When it is created:

1. Generate it once with `keytool -genkeypair -v -keystore wino-upload.keystore -alias wino-upload -keyalg RSA -keysize 4096 -validity 10000`, store it and its passwords outside the repository, and keep an offline backup. `.gitignore` ignores `*.keystore` and `*.jks`.
2. Enroll the app in Play App Signing; Google holds the app signing key and the upload key only authenticates uploads.
3. Register the SHA-1 of the upload key and of the Play app signing certificate with Entra and the Google Android OAuth client ([sign-in registrations](harness/development-android.md#sign-in-registrations)).
4. Sign the build with the standard properties, reading passwords from the environment:

```sh
dotnet publish src/Wino.Mail.Android/Wino.Mail.Android.csproj -c Release -r android-arm64 -p:WinoTargetPlatform=Android \
  -p:AndroidPackageFormats=aab -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=/path/to/wino-upload.keystore \
  -p:AndroidSigningKeyAlias=wino-upload -p:AndroidSigningKeyPass=env:WINO_ANDROID_KEY_PASS -p:AndroidSigningStorePass=env:WINO_ANDROID_STORE_PASS
```

The package id `com.winomail.android` cannot change after the first Play upload.

## 16 KB page-size check

Play requires native libraries aligned for 16 KB memory pages for apps that target Android 15 or later. Check every release package:

```sh
zipalign -c -P 16 -v 4 com.winomail.android-Signed.apk
unzip -q com.winomail.android-Signed.apk 'lib/*' -d apk
for f in apk/lib/arm64-v8a/*.so; do llvm-readelf -lW "$f" | awk '$1=="LOAD"{print $NF}' | sort -u; done | sort | uniq -c
emulator -avd Wino_Phone_API36_16KB
adb shell getconf PAGE_SIZE
adb install -r com.winomail.android-Signed.apk
```

Every LOAD segment must show `0x4000`. `llvm-readelf` is in NDK r28c (`sdkmanager "ndk;28.2.13676358"`, then `$ANDROID_HOME/ndk/28.2.13676358/toolchains/llvm/prebuilt/darwin-x86_64/bin`). The APK stores native libraries compressed (`extractNativeLibs=true` for minSdk 23), so zip alignment does not apply to them; the ELF alignment does. `getconf` must print `16384`. The app must start and reach its first page without a crash in `adb logcat`. Also start the Release APK on `Wino_Phone_API23` to check the minimum OS.

## Deferred

- Play Console app, store listing, Data safety form, internal testing track, and the account deletion link.
- Google Play Billing and purchases (Unlimited Accounts and Wino Account): out of v1.
- Release and upload scripts under `scripts/release/android`, and the translation prompt the other release scripts run.
- Firebase project and `google-services.json` for push relay notifications.
- Sentry symbol upload for the AOT build.

## Native AOT

`PublishAot=true` is experimental for Android in .NET 10 and stays off. A spike on 15 Oct 2026 (head only, in a scratch copy) built an APK of 61.2 MB with one 121.6 MB `libWino.Mail.Android.so` and 11 trim/AOT warnings (4 IL3050 in Mono.Android and Java.Interop, 7 IL207x in Ical.Net). It reached Welcome on Android 16 and crashed with SIGSEGV in `libWino.Mail.Android.so` at start on Android 6.0.

## Status (15 October 2026)

Verified on 15 Oct 2026 with a throwaway test key (not the upload key): the Release APK (59.3 MB) and AAB (57.2 MB) publish with Mono full AOT, LLVM and `TrimMode=full`; the only trim warnings are 7 from the Ical.Net package. All 170 native libraries are 16 KB aligned. The APK starts and reaches Welcome on `Wino_Phone_API36_16KB` (page size 16384) and `Wino_Phone_API23`, through Add account and back, dark mode and rotation, with no crash or trimming exception in logcat.
