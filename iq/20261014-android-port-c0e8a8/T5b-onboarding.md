# T5b Android onboarding and account setup (done, 14 Oct 2026)

## Files
- Head `Views/Onboarding/`: `WelcomePageFragment` (rewritten), `ProviderSelectionFragment`, `SpecialImapCredentialsFragment`, `ImapSetupFragment`, `AccountSetupFragment`, `OnboardingViews`, `OnboardingForm`. Layouts `fragment_welcome`, `fragment_provider_selection`, `fragment_account_setup`, `onboarding_page`, `onboarding_*` rows/fields; `values/styles_onboarding.xml`, `dimens_onboarding.xml` (+ `values-h720dp`). The T4 Welcome layouts are deleted.
- Head infra: route `Initialize` (setup pages run the VM's `InitializeAsync`, as on iOS); `AndroidNavigationService` implements the handoff; `POST_NOTIFICATIONS` in the manifest. **Out-of-scope fix:** `AndroidThemeService.LoadWallpaper` now subsamples (Nighty.jpg decoded to 80 MB and crashed dark mode on API 23).
- Presentation.Android: `WinoMaxWidthLayout` (560 dp column), top-bar/row dimens.
- Shared: `IAccountSetupHandoffHost` (optional host capability). `NavigationCoordinator` starts the first sync and waits for the handoff before showing the shell; Stop ends the wait. `AccountSetupProgressPageViewModel` adds the created address, a notification choice (saved to `MailAccountPreferences.IsNotificationsEnabled`) and first-sync progress from `AccountSynchronizationProgressUpdatedMessage`. Two coordinator tests. Six `Android_*` strings.

## Screens (wallpaper behind, content in the Wino zone, M3 components)
- **Welcome:** hero, three features, Add account, Restore from backup (dialog: Wino Account or backup file), Privacy Policy.
- **AddAccount:** the shared 3-step wizard. Providers come from the shared featured list plus the custom server. Then name and colour, then capability switches (provider/device dropdown) and the mail range.
- **ImapSetup:**
  - iCloud/Yahoo: the shared credentials VM, with its hint, help link and calendar mode.
  - Other: sign-in, then the servers. **Advanced** reveals the shared advanced section.
- **Permissions:** shared setup steps; failure with Go back / Try again. When done: notification switch, first-sync card, Open inbox. Open inbox saves the choice, asks for POST_NOTIFICATIONS (33+) and shows the shell.

## Omitted board items (no shared implementation)
QR import from Windows; Terms link (no URL); the provider catalogue search; daily briefing switch; calendar/contacts switches after sign-in (shared flow chooses them before sign-in, step 3); "Add another account" on the done page; the "1,240 of 3,180" count (status text only); Advanced server fields for iCloud/Yahoo (catalogue-defined).

## Verification
- Android Debug: 0 errors, no new warnings. ViewModels 227/227, Controls 95/95, Core 1215 pass + 5 known failures. Mac Debug arm64 build succeeded.
- **Wino_Phone_API23, flows:**
  - Welcome → AddAccount → IMAP: the shared "Password is required" error showed. Fake `test@example.invalid` → servers step → Add account showed the validation failure inline.
  - Back walks the steps.
  - Gmail: Play services returned error 10 (not registered), shown on the failure page; Go back worked.
  - Outlook: MSAL opened the browser; back cancelled to the capabilities step.
  - Local-only account → done page held → Open inbox → shell.
- **Wino_Phone_API23, display:** landscape centred column; dark mode (Welcome, AddAccount, iCloud, capabilities); rotation keeps state.
- **Fixed:** API 23 OOM from the full-size provider logos (now downsampled and cached). Radio buttons cost about 9 MB on Android 6, so they became dropdowns.
- Not run: tablet AVD; notification switch and first-sync card with a mail account; POST_NOTIFICATIONS (API 23 has none); the Windows handoff (Debug x64, Release gate, tests).
- Screenshots `T5b-phone-*.png`. `T5b-phone-landscape-setup-done.png` predates the fix that drops the "mail is syncing" line for accounts without mail.

## Owner's real iCloud sign-in should check
- Credentials page Sign in enables only with a valid address and app password.
- The setup steps pass.
- The done page shows the address, the notification switch and live first-sync progress.
- Turning notifications off persists (Settings › Notifications).
- Open inbox → shell; the Inbox fills as the first sync completes; CalDAV calendars appear.
- Back on the done page opens the inbox and never returns to the form.
