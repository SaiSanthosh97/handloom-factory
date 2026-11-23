# TD-05 · Release 0.5.0: Notifications

**Status:** Implemented (2026-11-23, C-009). Released together with 0.4.0 (TD-04) as version 0.5.0.

## Summary
- **Owner push notifications (optional, D14).** Settings → "Notifications" switch. When on, the owner's phone gets a notice through the free **ntfy** app:
  - "Ramesh submitted 3 sarees today", sent by the worker's phone after that day's entry has reached the relay.
  - "Weekly payments ready: ₹42,500 for 8 workers", sent by the owner's phone on Saturday from 18:00 factory time, once per week, only when something is still to pay.
  - A "Send a test notification" button.
- **Update notification in the background (D16).** About once a day the background job checks GitHub Releases. A new version is announced with an Android notification, once per version, even when the app is closed. Tapping it opens the app, which shows the existing install pop-up and banner.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Sync/SyncEngine.Notify.cs` | `SetNotificationsAsync`, `SendTestNotificationAsync`, `SendWeeklyNoticeIfDueAsync`, `NotifySubmittedAsync` (called from the worker's flush) |
| `Core.Sync` `SharedSettings` record | Carries the topic (or "off") from the owner's phone to each linked worker phone |
| App `Services/UpdateNotifier.cs` | Background GitHub check every 20 h; Android notification channel "updates"; Android 13 permission request |
| App `Services/SyncScheduler.cs` | Each run: sync, then the weekly notice, then the update check |
| App `SettingsViewModel` / `SettingsPage` | Switch, topic, "Open in ntfy", test button |

## How it works

### Topic
- Turning notifications on creates a random topic `hf-` + 24 random characters. It is impossible to guess, so only the owner's ntfy app sees the notices. Turning them off clears it.
- The topic is queued to every linked worker channel as a `SharedSettings` record (in the outbox, like all sync data). Workers linked later receive it in the snapshot.
- "Open in ntfy" opens `https://ntfy.sh/<topic>`. If the ntfy app is installed, it offers to subscribe. Otherwise the browser page explains how.

### Why the texts are plain
ntfy shows the message as text on the lock screen, so these notices cannot be end-to-end encrypted the way sync data is. The Settings screen says so. They carry only a name and a count, or a total. All sync data stays encrypted and signed (TD-03).

### "Submitted" notice
Sent from the **worker's** phone right after the flush that delivered the entry to the relay. It does not wait for the owner's phone to receive it, so the notice arrives even while the owner's phone is offline. Only the last entry in a batch is announced, so quick corrections give one notice. A failed notice is ignored, because the entry itself was sent.

### Weekly notice
- **When:** the owner's phone sends it from the background job (about every 15 minutes) or the foreground timer, on the pay-week end day (Saturday) at or after `WeeklyNoticeHour` = 18:00 factory time.
- **Once per week:** setting `notify.weeklySent` stores the week's start date and is set only after the notice was sent.
- **Total:** `PaymentCalculator.ForWeek` for each active worker whose week is still *Pending*. If everyone is paid, nothing is sent.

### Background update check
- **How often:** every sync run calls `UpdateNotifier.CheckInBackgroundAsync`. It checks GitHub at most every 20 hours (`update.bgCheck`).
- **Once per version:** it notifies once per release tag (`update.notified`).
- **Offline:** errors are ignored and the check is tried again on a later run.
- **Android 13+ permission:** `POST_NOTIFICATIONS` is asked once, from the home screen. If the user refuses, the in-app pop-up and banner still work.
- **Small icon:** the notification uses `notification_icon`, a white glyph, as Android requires.

## Trade-offs
- Notices use the same ntfy server as sync, so no new service is added. The cost is that ntfy.sh's limits are shared with sync. Notices are far fewer than sync messages.
- Firebase push was rejected because it needs a Google project and server keys (against D1, "no server").

## Tests
Two tests in `tests/Core.Sync.Tests/SyncTests.cs` run fake phones over an in-memory relay:
- `Worker_submission_notifies_the_owner_when_turned_on`:
  - nothing is sent while notifications are off;
  - after the owner turns them on, the topic reaches the worker's phone and the next entry sends "Ramesh submitted 3 sarees today";
  - after turning them off again, nothing more is sent.
- `Weekly_payments_notice_on_saturday_evening_once`:
  - nothing is sent on Monday or on Saturday before 18:00;
  - at 18:30 on Saturday, "Weekly payments ready: ₹1,450 for 2 workers" is sent, and only once.

The Android update notification is not covered by unit tests. The owner tests it on the phone.
