# T5c onboarding correction (done, 14 Oct 2026)

## Removed or restored
- **Shared:** `IAccountSetupHandoffHost` is deleted. `NavigationCoordinator.cs`, `AccountSetupProgressPageViewModel.cs` and `NavigationCoordinatorTests.cs` are restored to HEAD. After setup succeeds, the coordinator shows the shell as on Windows and iOS.
- **Android head:**
  - `AndroidNavigationService` no longer has the handoff.
  - The done page is gone: address, notification switch, first-sync card, Open inbox and its POST_NOTIFICATIONS request.
  - Welcome feature rows, the privacy link, the restore chooser dialog and the provider-page privacy note are gone.
- **Strings:** seven `Android_*` strings removed. `Android_Welcome_Tagline` stays.
- **Permission:** the only request is the T11 one on the first shell.

## Boards now
- **Welcome:** the Windows WelcomePageV2 content:
  - logo, `WelcomeWindow_Title`
  - two restore links, import progress and status
  - `GetStartedDescription`, Get started
  - The zone sits at the bottom over the wallpaper.
  - **Simplified:** the Windows `AppDescription` names Windows, so the tagline replaces it.
- **Setup progress:** the iOS and Windows page: title, steps with status and error, success bar, error bar with Go back and Try again.
- **AddAccount/IMAP:** unchanged. Every element binds the shared wizard and IMAP ViewModels.
  - **Simplified:** no "More providers" catalogue; a provider row continues at once.

## Verification
- **Builds:**
  - Android Debug: 0 errors, NU190x warnings only.
  - Mac Debug arm64 and x64, iOS Simulator Debug: 0 errors.
- **Tests:** ViewModels 226/226, Controls 95/95, Core 1229 pass with the 5 known failures.
- **Wino_Phone_API23:**
  - Welcome → provider → IMAP with `test@example.invalid`: inline validation failure.
  - Gmail: the setup page showed the failure (Play services error 10). Go back returned to step 3; Try again failed again.
  - Light and dark (dark set through `preferences.json`; API 23 has no `cmd uimode`).
- **Screenshots:** `T5c-*.png`.
- **Not run:** successful setup into the shell, tablet, API 33+ prompt.
