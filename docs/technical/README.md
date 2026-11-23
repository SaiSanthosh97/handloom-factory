# Technical Design Documents

TD-00 is the overall design. After that there is one document per implemented feature. Each one explains **what** was built and **how** it works, down to the small details. It also covers **why** each approach was chosen over the alternatives, the trade-offs and edge cases, and how the feature is tested.

Documents are updated in the same change as the code, and every change is logged in [../CHANGES.md](../CHANGES.md).

| # | Topic | Components | Status |
|---|---|---|---|
| [TD-00](TD-00-Architecture.md) | Overall architecture: components, data model, wage and pay-week rules, PIN login, owner-hub sync, linking, notifications, background work, testing | all | Designed |
| [TD-01](TD-01-Release-0.1.0-Owner-Basics.md) | Release 0.1.0: foundation, money and pay weeks, PIN login, workers and rates, day entry, dashboard, weekly report, encrypted storage, update alerts, Android 6.0+ | `Core.*`, `Storage.Sqlite`, `Device.Security`, App | Implemented |
| [TD-02](TD-02-Release-0.2.0-Payments-and-Data.md) | Release 0.2.0: weekly payments, corrections after payment, wages to pay, worker history, month/year reports, export, import and restore | `Core.Factory`, `Core.Workflow`, `Storage.Sqlite`, App | Implemented |
| [TD-03](TD-03-Release-0.3.0-Worker-Phones-and-Sync.md) | Release 0.3.0: worker phones, QR linking with key exchange, owner-hub encrypted sync through ntfy, outbox, conflict rules, offline catch-up, unlinking, background sync | `Core.Security`, `Core.Relay`, `Core.Sync`, `Storage.Sqlite`, `Device.*`, App | Implemented |
| [TD-04](TD-04-Release-0.4.0-CSV-and-History.md) | Release 0.4.0 (shipped inside 0.5.0): CSV report export, change history screen | `Core.Reports`, `Core.Workflow`, App | Implemented |
| [TD-05](TD-05-Release-0.5.0-Notifications.md) | Release 0.5.0: optional ntfy notifications (worker submitted, weekly payments ready), background update notification | `Core.Sync`, App | Implemented |
