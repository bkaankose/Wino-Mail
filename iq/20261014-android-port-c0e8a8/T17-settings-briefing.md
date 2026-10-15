# T17 Settings, theme gallery, briefing, notifications (done, 15 Oct 2026)

Windows pages define content; boards define the look. Values: `wino_material_settings.xml`.

## Settings (restyled T9 infrastructure)
- **Rows:** cards are 56 dp, or 72 dp with supporting text. The root list has a 40 dp glyph slot, with text and primary subheaders at 72 dp. The Windows groups and order are unchanged.
- **Top app bar:** 64 dp with a 22 sp title; the "Settings · group" subtitle is removed.
- **Segmented controls:** `SettingsBinder.Segmented` is now the M3 segmented button (selected segment with a check). Light/dark/system uses short labels (`Android_Appearance_*`).
- **Accent swatches:** 40 dp, wrapping, with a 2 dp ring on the selected one.
- **Theme gallery:** a card grid (at least 104 dp per card, 120 dp tall, 12 dp corners) with the name on the image. The selected card has a stroke and a check badge.

## Daily briefing
- **Shared code:** `DailyBriefingCardContent` replaces the Mac and iOS `Describe` copies. `DailyBriefingAvailability` holds the entitlement → eligible → unseen rule. Mac and iOS use both; Windows is untouched.
- **Android:**
  - The mail list app bar shows the action only when the entitlement rule allows it, with an unseen dot.
  - It opens a bottom sheet on compact widths and a 400 dp side sheet on medium and expanded widths.
- **Debug only:** `DebugCommand briefing [--es kind fake]` (no shared entitlement simulation exists).

## Notifications
- **Importance:** mail channels are default importance; calendar stays high.
- **Header line:** mail notifications show the account address.
- **Unchanged from T11:** the configured actions, and no sync channel.

## Verification
- **Builds:** Android Debug 0 errors, no new warnings. Mac arm64 and x64, and iOS Simulator Debug: 0 errors.
- **Tests:** ViewModels 236/236 (4 new), Controls 95/95, Core with the 5 known failures.
- **Emulator (phone, plus the tablet size):** Settings pages; live theme, accent and dark changes.
- **Not entitled:** with the demo account, the inbox has no briefing action and `briefingstate` reports available=false.
- **Briefing:** a sample-card sheet on the phone and a side sheet on the tablet size.
- **Notifications:** test posts showed importance 3 for mail and 4 for calendar.
- **Screenshots:** `T17-*.png`.

## Omitted (no Windows/shared support)
Board regrouping and row summaries, dynamic colour, drawer-wallpaper and text-size options, briefing events/tasks, notification Reply/Archive/Snooze/Join, initials avatars, sync channel.

## Pending
- **Owner:** Windows Debug and the Release gate, TalkBack, and the briefing with a real entitlement.
- **Not tracked:** `Views/Debug/DebugBriefing.cs` is git-ignored.
