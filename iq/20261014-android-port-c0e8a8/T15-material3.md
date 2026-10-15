# T15 Material 3 restyle (done, 14 Oct 2026)

## Changes
- **Tokens (Presentation.Android):** `wino_material.xml` (M3 metrics), `wino_material_styles.xml` (`Widget.WinoKit.*`), container colours, Material vectors. Container colours follow theme accents. Theme: `actionBarSize` 64 dp and M3 component defaults.
- **Inbox:** 64 dp bar (menu · title · search · overflow), 48 dp chip row (segmented Focused/Other, dropdown filter chip, Select), plain subheaders, tonal FAB. Undo and status bars are snackbars. Back closes search.
- **Drawer and rail:** 360 dp modal drawer with 56 dp pill items; New mail is a tonal item. The rail has a tonal FAB.
- **Reader:** 64 dp bar (Back · Mark unread · overflow, no title). 24 sp headline, M3 bottom app bar (Archive, Delete, Move, Flag) and a tonal Reply FAB. Attachments are assist chips.
- **Compose:** Close · title · Attach · Send (icon) · overflow, 22 sp title. The status line shows only while saving.
- **Search:** 56 dp search bar, M3 filter chips, no FAB. **Move:** modal sheet with handle, filter pill and account subheader.
- **Sync removed:** the toolbar Sync item and the account long-press "Sync" are gone. Pull to refresh still calls the shell's `SynchronizeAsync`.
- **Docs:** `docs/android-design-decisions.md`: reader, mail list and v1 acceptance rows.
- No shared code changed; Settings, Calendar and placeholders are untouched.

## Verification
- **Build:** Android Debug `--no-incremental`: 0 errors. The only warnings are the existing NU1902/NU1903.
- **Emulator:** Wino_Tablet_API36 with demo seed; phone and landscape via `wm size` (reset, emulator killed). emulator-5554 untouched.
- **Screenshots:** `T15-phone-{inbox,drawer,reader,compose,search,move,actionmode}-{light,dark}.png`, `T15-landscape-*`, `T15-tablet-*`, `T15-tablet-pull-to-refresh-snackbar.png`.
- **Pull to refresh:** logcat shows "Starting mail synchronization … FullFolders", then the failure snackbar (closed demo port).
- **Idle frames:** 0 per 10 s on the inbox (phone and tablet).
- **Sync grep:** no Sync button remains; Settings keeps only sync settings (interval, enable, Sync aliases).

## Gaps
- Not seen on screen: Focused/Other (demo has no pivots), recipient chips, TalkBack.
- No last-sync line in the drawer: the shared layer has no formatted last-sync value. The drawer shows the running synchronization only.
- The ActionMode title ("2 item(s) selected", a shared string) truncates on phones.
- Omitted (no shared support): Move "New folder", recent searches. Mode placeholders keep their old bar.
