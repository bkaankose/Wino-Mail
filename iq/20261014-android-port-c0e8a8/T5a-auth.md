# T5a Android authentication plumbing (done, 14 Oct 2026)

## Design

- **Gmail** `AndroidGmailAuthenticator` replaces Core's `IGmailAuthenticator`. On each call it picks one path:
  1. The account has a stored Google token: shared `GmailAuthenticator`.
  2. Play services is available: `AuthorizationClient`.
  3. Otherwise: shared loopback flow (`HttpListener` on 127.0.0.1, page in a Custom Tab).
- **Play path:**
  - The request uses `SetAccount` for a known address, or `Prompt.SelectAccount` for a new account.
  - On `hasResolution`, an interactive call starts the PendingIntent. A silent call raises `AuthenticationAttentionException`.
  - Cancel raises `AccountSetupCanceledException`.
  - A partial grant is refused (`GetMissingScopes`, now public).
  - The address comes from the OpenID userinfo `email` claim.
  - Tokens are reused for 10 minutes. Refresh calls `clearToken`, then authorizes silently.
- **Outlook** `AndroidOutlookAuthenticationHost`:
  - Redirect is `msauth://<package>/<url-encoded Base64(SHA-1(cert))>`, computed at runtime.
  - Sign-in uses the system browser, with the current activity as parent.
  - MSAL keeps its own token cache.
  - **Broker is off.** It needs `<queries>` and broker result handling that could not be tested.
- **Glue:** `AndroidActivityResults` starts the sign-in screens for a result. `MainActivity.OnActivityResult` forwards to it, and it passes other request codes to MSAL's `AuthenticationContinuationHelper`.
- **Shared:** MSAL log forwarding moved to `Wino.Authentication/MsalLogForwarding`. The Mac and iOS hosts now use it.

## Files

- `src/Wino.Platform.Android/Authentication/*` (three classes), plus the registration and the csproj.
- `src/Wino.Mail.Android/Authentication/MsalRedirectActivity.cs` and `MainActivity`.
- Shared: `MsalLogForwarding.cs`, `GmailAuthenticator.cs`, and the Mac and iOS Outlook hosts.

## Packages and manifest

- `Xamarin.GooglePlayServices.Auth` 121.6.0.2 (play-services-auth 21.6.0, minSdk 23). Version 22.0.0 needs minSdk 24.
- `Xamarin.AndroidX.Fragment` 1.8.9.3 → 1.9.0, because Play services requires it.
- Manifest: `MsalRedirectActivity` (exported, VIEW/BROWSABLE, `msauth://com.winomail.android`, no path), plus MSAL and Play services activities.

## Debug signing (`~/Library/Application Support/Xamarin/Mono for Android/debug.keystore`)

- SHA-1: `28:BA:A7:C2:D9:9E:A7:B4:A3:4D:84:C3:F7:E1:83:C5:F0:C2:B6:1D`
- Signature hash: `KLqnwtmep7SjTYTD9+GDxfDCth0=`. The app computed the same value at runtime.
- Release hash: `keytool -exportcert -alias <a> -keystore <ks> | openssl sha1 -binary | openssl base64`. With Play App Signing, use the Play Console SHA-1.
- Windows machines have their own debug keys.

## Owner registration

1. **Entra:** Add platform → Android, package `com.winomail.android`, plus each signature hash.
2. **Google Cloud:** an Android OAuth client for `com.winomail.android`, plus each SHA-1.

## Verification

- Android Debug and Release builds: 0 errors. No trim warnings from MSAL or Play services.
- `Wino_Phone_API36`, using a temporary probe (since removed):
  - The app launched to Welcome.
  - The Play services account UI opened (`T5a-gmail-play-services.png`). Back returned a cancel.
  - MSAL opened Chrome (`T5a-outlook-msal.png`; its terms screen was not accepted). Back returned `authentication_canceled` through `OnActivityResult`.
  - `HttpListener` answered a request from `adb shell nc`.
- Core tests: 1215 passed, 5 known failures.
- Mac arm64 and iOS Simulator Debug builds succeeded.

## Open issues

- The final clean build was not re-checked on screen: T6 installed its gallery on the shared emulator, so the emulator was left running.
- Real sign-in waits for the registrations.
- The loopback page stays open in the Custom Tab.
- If the process dies while a sign-in screen is open, its result is lost.
- Owner Windows handoff: Debug x64 build, Release gate, Core tests.
