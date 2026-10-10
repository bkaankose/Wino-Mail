# Wino.Core.MacOS.Bindings

Managed bindings for Apple APIs and native frameworks that .NET for macOS does not cover: Swift-only
Apple frameworks such as StoreKit 2, and third-party Objective-C frameworks such as Sparkle. Each
Swift module is a folder:

```
<Module>/
├── Swift/*.swift                     Swift source with @_cdecl C exports
├── Native/Wino<Module>.xcframework   prebuilt from Swift/, committed
├── Native/Wino<Module>.sources.sha256  hash of the Swift source the framework was built from
└── *.cs                              [LibraryImport] declarations and the public C# API
```

The .NET build never compiles Swift. It embeds the committed framework into
`Wino Mail.app/Contents/Frameworks`, and app signing re-signs it.

## Changing a module

1. Edit the Swift source.
2. Run `./build-native.sh <Module>` (requires Xcode). It builds an arm64 + x86_64 framework for
   macOS 14.0 and updates the source hash.
3. Update the C# declarations to match, build the app, and commit the source, framework and hash together.

## Adding a module

1. Create `<Module>/Swift/` and run `./build-native.sh <Module>`.
2. Add a `NativeReference` for `<Module>/Native/Wino<Module>.xcframework` to the project file.
3. Bind the exports with `[LibraryImport("@rpath/Wino<Module>.framework/Wino<Module>")]`.

## Conventions

- Async exports take a C callback and an opaque context, then answer once with a UTF-8 JSON
  envelope: `{"ok":true,"value":...}` or `{"ok":false,"error":"..."}`. See `StoreKit2/StoreKit2Native.cs`.
- Callbacks are `[UnmanagedCallersOnly]` and must not throw back into Swift.
- This is a binding project so the SDK packages the frameworks for the app. The root `ApiDefinition.cs`
  stays empty; Objective-C modules keep their API definition in their own folder (`Sparkle/SparkleApiDefinition.cs`).
- Builds with `-p:WinoMacDistribution=AppStore` leave out modules that must not ship in the Mac App Store
  (Sparkle) and build into `bin|obj/<Configuration>-AppStore/`, so the two variants never share outputs.

## StoreKit2

`StoreKit2Client` covers the storefront, products, purchase with an `appAccountToken`, current and
unfinished transactions, finishing, Restore Purchases (`SyncAsync`) and transaction updates.
In Debug builds, the MacDebugBridge commands `sk2-storefront`, `sk2-products <id>...` and
`sk2-entitlements` call these functions.

Purchases use it through `MacAppStoreClient` (Wino.Platform.MacOS, registered only in App Store builds),
behind the shared `IAppStoreClient`. `WinoAppStorePurchaseService` (Wino.Services) buys Unlimited Accounts
and Wino Intelligence (`UNLIMITED_ACCOUNTS`, `AI_PACK`) and links them to the Wino Account through
`POST /api/v1/store/apple/transactions`:
- A purchase made while signed in carries the Wino Account ID as `appAccountToken`. It is finished only
  after the API linked it, so StoreKit delivers it again when the link fails.
- At launch and when the Wino Account page loads, the purchases tagged with this account are sent again.
- Unlimited Accounts bought signed out unlocks from the App Store entitlement alone. The Redeem card
  links it to a Wino Account later, as with the Microsoft Store.
- Refresh purchases runs `AppStore.sync()` first (Restore Purchases).
- Other channels (Stripe) appear only in the United States storefront.

## Sparkle

Sparkle 2 updates DMG builds; Mac App Store builds contain none of it (App Review Guideline 2.4.5).
It is an Objective-C framework, so it is bound with an API definition instead of Swift exports:

```
Sparkle/
├── SparkleApiDefinition.cs        Objective-C binding: SPUStandardUpdaterController, SPUUpdater
├── SparkleUpdater.cs              public C# API (start, Check for Updates, automatic-update settings)
├── update-sparkle.sh              replaces the framework with an official Sparkle release
└── Native/Sparkle.xcframework     official release, committed; Sparkle.version records its URL and hash
```

To update Sparkle, run `./Sparkle/update-sparkle.sh <version>`, check the release notes for API
changes, build, and commit the framework and `Sparkle.version` together. The script removes
`Downloader.xpc` (the app has the network.client entitlement) and the headers.

The app side lives in `Wino.Mail.MacOS`:
- `Sparkle.plist` (merged into Info.plist): feed URL, EdDSA public key, daily checks, installer service.
- `Entitlements.plist`: the mach-lookup exception for Sparkle's sandboxed installer
  (`Entitlements.AppStore.plist` omits it).
- `WinoSignSparkleHelpers` in the project file signs Sparkle's `Autoupdate` and `Updater.app`, which the
  SDK does not sign.
- `AppDelegate.Updates.cs`: starts the updater and adds **Check for Updates…** to the app menu.
  Settings › General has the automatic-update switches.

To try an update locally, serve an appcast and a signed `.zip` of a newer build from `127.0.0.1`, then
`defaults write ~/Library/Containers/com.winomail.macos/Data/Library/Preferences/com.winomail.macos SUFeedURL <url>`.
Delete the override afterwards.
