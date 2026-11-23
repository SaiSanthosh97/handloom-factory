# TD-01 · Release 0.1.0: Owner Basics

**Status:** Implemented (2026-11-23, C-003). Covers the solution foundation, PIN login, the domain and wage rules, workers, daily entry, the dashboard, the weekly report, encrypted storage and update alerts.

## Summary
0.1.0 runs on the **owner's phone only**. The owner:
- sets up the factory and a 4-digit PIN;
- adds workers with a rate per saree and can change that rate later (history is kept);
- types the sarees for any day up to today;
- sees today, this pay week (Sunday–Saturday) and this month on the dashboard;
- browses weekly reports week by week.

Data lives in an encrypted SQLite file whose key is wrapped by the Android Keystore. Every time the app opens it checks GitHub for a newer version and offers it.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Common/Clock.cs` | `IClock`, `ManualClock`, `FactoryTime` ("today" in the factory zone), `Ids.New()` / `Ids.For()` (deterministic ids) |
| `Core.Common/Money.cs` | `Money` in paise: exact arithmetic, typed-amount parsing, Indian grouping (₹1,23,456.50) |
| `Core.Common/Periods.cs` | `DateRange`; `Periods.PayWeek` (Sun–Sat), `PayDay`, `Month`, `Year` |
| `Core.Common/Result.cs` | `Result` / `Result<T>` and `ErrorCode` (the app turns codes into messages) |
| `Core.Security/Pins.cs` | PBKDF2 PIN hashing, `PinLockout` |
| `Core.Security/DatabaseKey.cs` | `IKeyVault`, `DatabaseKey.LoadOrCreate`, `InMemoryKeyVault` (tests) |
| `Core.Factory/Model.cs` | `FactoryInfo`, `Employee`, `RateChange`, `ProductionEntry`, `AuditEntry` |
| `Core.Factory/Wages.cs` | `RateBook` (rate on a day), `WageCalculator`, `EntryRules` |
| `Core.Reports/Reports.cs` | `ReportBuilder.Dashboard / Period / ForEmployee` |
| `Core.Workflow/*` | `IFactoryStore` + `ChangeSet` (unit of work), `UserSession`, `WorkflowContext`, `AccountService`, `EmployeeService`, `ProductionService`, `ReportService` |
| `Storage.Sqlite/*` | `FactoryDatabase` (encrypted, migrations), `SqliteFactoryStore`, `SqliteSettings`, `FactoryDatabaseFiles` |
| `Device.Security/Android/AndroidKeystoreVault.cs` | Wraps the database key in the Keystore (copied from Travel Vault) |
| `Core.AppUpdater/*` | GitHub release check and verified download (copied from Travel Vault) |
| `HandloomFactory.App/Services/UpdateService.cs` | Check on every open (max hourly), pop-up once per version, banner, install; `SessionLock` (owner PIN after 5 min) |
| `HandloomFactory.App/ViewModels`, `Views` | Start, Setup, Login, Home, Sarees (day entry), Workers, Worker, Weekly report, Settings |

## How it works

### Deterministic ids (one entry per employee per day)
`ProductionEntry.Id = SHA-256("entry|<employeeId>|<yyyy-MM-dd>")[..32]`. `RateChange.Id` is built the same way from employee + effective date. Saving the same employee and day twice updates one row, enforced also by `UNIQUE(employee_id, entry_date)`.

This matters most from 0.3.0 onwards. When a worker phone and the owner phone create "today's entry" independently, they produce the **same id**, so sync merges the two without a server.

### Pay week and "today"
- `Periods.PayWeek(d)` goes back to Sunday (`DayOfWeek.Sunday == 0`) and adds 6 days. Saturday is payday and is included. Sunday work falls into the week that ends the following Saturday (D7).
- "Today" comes from `FactoryTime`, which uses the **factory's** time zone (stored at setup, Asia/Kolkata by default), not UTC. Tested: 20:00 UTC on the 22nd is already the 23rd in India.

### Wages
`earnings = Σ count × RateBook.RateOn(employee, day)`. A rate change applies from its date onwards, so changing a rate never alters past earnings. The weekly report shows the rate, or "varies" when it changed during the week. Money is held in `long` paise, so there is no rounding error.

### Use cases and the unit of work
Every use case follows the same steps:
1. Check the role (`UserSession.IsAdmin`).
2. Validate the input.
3. Apply the domain rules (`EntryRules`).
4. Build a `ChangeSet`: the data rows plus one `AuditEntry` per change.
5. Call `IFactoryStore.CommitAsync` once, which runs as **one SQLite transaction**.

So a change and its audit entry are saved together or not at all.

Saving a whole day (`SaveDayAsync`) **validates every row before writing anything**. The error names the worker (e.g. "Suma: Sarees per day must be between 0 and 50.") and nothing is saved. A cleared box deletes that day's entry, which is also audited.

### PIN login
- PBKDF2-SHA256, 16-byte salt, 100,000 iterations. That is fast enough on a 2016 phone, and with only 10,000 possible PINs more iterations would add little.
- The login screen logs in as soon as the 4th digit is typed.
- **Lockout:** after 5 wrong PINs there is a 30 s wait, doubling up to 15 min. It is stored in the `credential` table, so restarting the app doesn't reset it.
- The owner's phone asks for the PIN again after **5 minutes in the background** (`SessionLock`, D3).
- PINs live only in `credential`. They are never in audit entries or logs, and will never be in exports.

### Storage
- One encrypted SQLite file, `factory.db`. Its key is random and stored in `factory.key`, wrapped by an AES-GCM key held in the Android Keystore.
- The database key is not derived from the PIN, so background sync (0.3.0) can open the database while the app is locked.
- If the Keystore key is lost (for example after a factory reset), the start screen offers "Start fresh".
- Schema version 1 is applied through `PRAGMA user_version`. Later releases add steps and never edit released ones.

### Update alerts (D16)
- `UpdateService.CheckAndAnnounceAsync` runs when the dashboard appears and whenever the app comes back to the foreground. It checks at most once an hour, on any connection.
- A newer GitHub release produces:
  - a **pop-up** "Version X is available. Install now?", shown once per version;
  - a **banner** on the dashboard that stays until the update is installed.
- Settings → Check for updates checks immediately.
- The APK is downloaded, checked against GitHub's SHA-256 (`UpdateDownloader`) and opened in Android's installer. On Android 8+ the app first asks for "Install unknown apps" if needed.

### Android version (D19)
`SupportedOSPlatformVersion = 23` (Android 6.0). The APK contains **arm64 and 32-bit ARM** code, because many 2016 budget phones run a 32-bit Android even on 64-bit chips. The Keystore AES-GCM key used for the database needs API 23, which is why 6.0 is the minimum and not 5.0.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| `ChangeSet` unit of work, committed in one transaction | A change and its audit entry are never separated; one code path for later sync/import | Repository per entity with separate saves |
| Validate the whole day before saving | No half-saved days | Save valid rows, report the rest (confusing) |
| Owner is an `Employee` with Role = Admin | One login path for everyone | A separate "owner" table |
| Default "rate applies from" = start of the current pay week | The owner can enter this week's days straight after adding a worker | Today (blocks entering earlier days of the week) |
| Day entry screen lists every worker for one day | The owner enters the whole factory in one pass, with a single Save | One worker at a time (many taps) |
| Update check on every open, max hourly | The owner asked to be told about every update; hourly avoids GitHub rate limits | Daily on Wi-Fi only (Travel Vault's rule) |
| Android 6.0 minimum, arm + arm64 | Covers most 2016 phones | Android 8.0 (excludes many 2016 phones); Android 5 (no Keystore AES) |

## Tests (83, all passing)
- `Core.Common.Tests` (24): pay-week boundaries (Sunday, Saturday, year change), months including a leap February, Indian money formatting and parsing, factory-zone "today", deterministic ids.
- `Core.Factory.Tests` (25): the ₹5,000 example from the requirements, a rate change mid-week, Sunday work counted toward the next Saturday, entry rules for both roles, dashboard totals and "not entered yet", weekly report columns, PIN hashing and lockout timing.
- `Core.Workflow.Tests` (17): every use case against a **real encrypted database**, including:
  - setup and validation; login, lockout and unlock after the wait; PIN change;
  - data surviving a reopen; the file holding no readable names, and the wrong vault failing to open it;
  - adding, editing, disabling and enabling workers; rate history; workers refused admin actions;
  - day entry with edit, clear and no-change cases; a bad row saving nothing; past, future and before-first-rate days; worker versus admin precedence.
- `Core.AppUpdater.Tests` (17): copied from Travel Vault.

## Found in the emulator test (before release)
- **Crash on a wrong PIN.** The login runs when the 4th digit is typed and clears the box afterwards. Doing that inside Android's key event crashed the password field (`IndexOutOfBoundsException: setSpan … ends beyond length 0`). The login is now posted to run after the key event (`Dispatcher.Dispatch`).
- **PIN box lost the keyboard.** It was disabled while the PIN was being checked, which drops focus, and focusing it while the page was still appearing was ignored. So after a wrong PIN the user had to tap the box again. Now the box stays enabled and gets focus (and the keyboard) 300 ms after the page appears and after every wrong PIN.
- **Wording:** "1 sarees" became "1 saree"; "Paid on Saturday Sat 28 Nov" became "Payday: Sat 28 Nov"; validation messages are titled "Please check" instead of "Something went wrong".
- **Reports tab icon:** 📊 rendered as a white square (tab icons are tinted to one colour), so it was replaced with ∑.

## Known limits in 0.1.0
- **No data export yet** (that comes in 0.2.0). Uninstalling the app deletes its data.
- An owner who forgets their PIN has no recovery yet. Planned: confirm with the phone's screen lock (TD-00 §4).
- English only. Kannada arrives in 0.4.0.
