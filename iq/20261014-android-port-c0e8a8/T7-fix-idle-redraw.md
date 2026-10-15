# T7 fix: idle redraw and ANR on the inbox (done, 14 Oct 2026)

## Cause
Rows whose mail has a queued server change (`MailItemViewModel.IsBusy`, set from the synchronizer's pending operations, as on Windows) showed an indeterminate `ProgressBar` (`wino_row_busy`). It animates every frame for as long as the change stays queued. On the demo account (closed port), offline, or with a failing server, that is forever. Reading a mail queues "mark read", so the inbox kept rendering after Back.
Under host load this starved the system compositor. The recorded ANR (`dropbox data_app_anr` 20:48: input dispatch timeout; main thread in `binder_thread_read`; composer HAL 55 %, surfaceflinger 33 %) matches.
Not the cause: bar blur (`WinoBarBackdropView.OnDraw` did not run while idle), PropertyChanged or projection loops (none logged while idle), timers.

## Fix
`controls/Wino.Mail.Controls.Android`: `wino_row_busy` is now a static `WinoIconView` (Clock glyph, secondary text colour), not a spinner (`wino_mail_row.xml`, `WinoMailRowView`).

## Numbers (`dumpsys gfxinfo` frames in 10 s idle, Wino_Phone_API36, host load ~24)

| State | Before | After |
| --- | --- | --- |
| Inbox, cold start | 0 | 0 |
| Inbox after reading a mail (pending change) | 88–115 | 0 |
| Reader | 0–3 | 3 (WebView settle) |
| Drawer open | 0 | 0 |

Scrolling still renders (28 frames for two flings; janky only because of host load). Compose shows 2 fps (caret blink), which is expected.

Screenshot: `T7-fix-pending-glyph-light.png` (Clock on the pending row).
