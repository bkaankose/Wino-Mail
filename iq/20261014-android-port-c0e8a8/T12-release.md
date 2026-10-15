# T12 Android release infrastructure (done, 15 Oct 2026)

## Fixed
- **Release policy never applied:** `build/Wino.Android.targets` tested `OutputType == Exe`, but the SDK turns an app's `Exe` into `Library` first, so Release built with profiled AOT, no LLVM and `TrimMode=partial`. It now tests `AndroidApplication`.
- **Full trimming broke Welcome** (`ClassNotFoundException: WinoMaxWidthLayout`; also `WinoZoneLayout`, `WinoBarBackdropView`): views named only in XML were removed. Embedded `ILLink.Descriptors.xml` in Presentation.Android and Mail.Controls.Android root every `[Register]`ed view.
- **IL2091** `WinoViews.Require<T>`: annotated.
- **IL2026** `[Export]` JavaScript interface (the fallback for WebView without WebMessageListener): replaced by a Java `WinoJavaScriptBridge` (`AndroidJavaSource`) that posts to a managed `Handler`; `Mono.Android.Export` is gone. Verified by forcing the fallback on API 36: the reader renders (`T12-js-bridge-fallback-reader.png`).
- An incremental Release build crashed at start (`TypeLoadException`); clean builds are fine (documented).

## Build (clean, throwaway test key in the scratchpad, `-p:NuGetAudit=false` still needed for Pkcs)
- Time 556 s (earlier clean run 480 s). Warnings: 7, all from the Ical.Net package (IL2070/2072/2075/2087); none in Wino projects.
- APK 59.3 MB, AAB 57.2 MB, arm64-v8a only. Native libs 107.3 MB uncompressed (AOT images 72.1 MB): assembly store 16.6, `libaot-Wino.Core` 15.8, SkiaSharp 9.8, monosgen 3.1, e_sqlite3 1.8, monodroid 1.4, sentry 0.85 MB.
- Signer verified (`apksigner`): the test certificate. Upload-key steps stay in `docs/releases-android.md` (owner).

## 16 KB
All 170 `.so` (monosgen, monodroid, e_sqlite3, SkiaSharp, sentry*, System.* natives, every `libaot-*`) have LOAD alignment `0x4000` (NDK r28c `llvm-readelf`), APK and AAB. `zipalign -c -P 16` passes; libs are stored compressed (`extractNativeLibs=true`).

## Runtime (Release)
| AVD | Cold start `TotalTime` (ms) | Checks |
| --- | --- | --- |
| Wino_Phone_API36_16KB (PAGE_SIZE 16384) | 1558, 1014, 1044 (578–747 on a warm emulator before the reset) | Welcome, Add account, back, dark, rotation |
| Wino_Phone_API23 | 397, 399, 396 | Welcome, Add account, back, rotation (no system dark mode) |
Logcat: no crash, MissingMethod, TypeLoad, ClassNotFound or JSON exceptions. Screenshots `T12-16kb-*`, `T12-api23-*`.

## Native AOT spike (scratch copy)
Builds in 255 s: APK 61.2 MB, one 121.6 MB `libWino.Mail.Android.so`, 11 warnings (4 IL3050 in Mono.Android/Java.Interop, 7 Ical.Net). Reaches Welcome on API 36 (`T12-nativeaot-api36.png`); SIGSEGV at start on API 23. Stays off.

## Notes / pending
- The API 23 image's WebView 44 cannot run the shared reader scripts (ES2017 syntax errors), so mail bodies do not render there; real devices update WebView through Play.
- Docs updated: `docs/releases-android.md` (command, clean build, descriptors, ELF check, Native AOT, Status), `docs/harness/development-android.md` (targets note, checks, troubleshooting).
