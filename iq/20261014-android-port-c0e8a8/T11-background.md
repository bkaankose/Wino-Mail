# T11 Background and notifications (done, 14 Oct 2026)

## Shared seams (no head rules)
- `ISynchronizationManager.RequestsQueued` (raised after queuing), `IBaseSynchronizer.HasQueuedRequests(predicate)`.
- `IApplicationRuntime.FlushQueuedRequestsAsync`: initializes without loops and runs ExecuteRequests only for kinds with queued requests. The queues are in memory, so a new process has nothing to send.
- `ICalendarService.GetNextReminderTriggerAsync`: the same trigger rule as `CheckAndNotifyAsync`.
- `NotificationContentRules` (Domain): scope, privacy text, summary threshold, configured actions, reminder text and snooze. `AppleNotificationBuilder` and `AppleNotificationCategories` now use it (Windows untouched).
- `IPushTokenProvider` doc: tokens are opaque (the relay client never checked hex).

## Android
- **Platform** (`Notifications/`, `Background/`, `PushRelay/`): builder, channels, intents, reminder scheduler and state, WorkManager scheduler, FCM token provider (Firebase only with `google-services.json`; `RemoteNotifications` follows it), device info ("android").
- **Head** (`Background/`): action receiver, alarm receiver (also boot, update and exact-permission change), `PendingChangesWorker` (fixed Java name), `WinoMessagingService` (compile-only), lifecycle hook, background runtime helper. Also `AndroidNotificationPermission.RequestAsync`, the hook T5b calls at the end of setup; until then the shell asks once.
- **Lifecycle:** onStop enqueues the flush and sets the alarm; the grace stop flushes first.
- **Packages:** Work.Runtime 2.11.2.1, Firebase.Messaging 125.1.3 (minSdk 23); Tracing.Ktx lifted to 2.0.0.
- **Docs:** 9 strings; `development-android.md` "Background and notifications" (`google-services.json` goes in `src/Wino.Mail.Android/`).

## Deviations
- **Actions:** mail actions are the two configured shared actions (default Mark as read and Delete), not a fixed Archive, Delete and Mark read set.
- **Taps:** taps go through `INotificationResponseService`.
- **Token rotation:** the binding marks `onNewToken` obsolete, so the token is read at every start; a token that rotates while the app runs reaches the relay at the next start.

## Verification
- **Builds:** Android Debug 0 errors and no new warnings. Mac Debug arm64 and x64 and iOS Simulator Debug 0 errors.
- **Tests (12 new):** Core 1227 passed with the 5 known failures, ViewModels 225/225, Controls 95/95.
- **Wino_Phone_API36**:
  - **Permission:** the prompt appears on the first shell (`T11-permission-prompt.png`).
  - **Grouping and actions:** test mail notifications group per account with Mark as read and Delete (`T11-notifications-grouped.png`). Mark as read reached the receiver and the shared service; the request was queued and the flush enqueued. `dumpsys jobscheduler` shows the job with the NET constraint, retrying against the closed demo port.
  - **Tap** opens the message (`T11-tap-opens-message.png`); reminder and sign-in notifications show (`T11-reminder-and-signin.png`).
  - **Grace period:** shortened to 30 s, the log shows "flush enqueued (left the foreground)", "Queued changes before the stop", then "stopped after 30 seconds" and the process ended.
  - **Reminder alarm:** with exact alarms denied the alarm was inexact and fired 16 s late in a new process, showing 1 reminder (`T11-reminder-alarm-inexact.png`). With exact alarms granted it fired 1 s after the trigger.

## Pending
- **Windows (owner):** Debug x64, the Release gate and Core tests for the shared Domain, Core, Services and resources changes.
- **FCM and relay:** untested by decision.
- **Not run:** API < 31 expedited foreground service, API 33-below permission path, the tablet.
- **Emulator:** grace override reset to 10 min (log line not seen).
