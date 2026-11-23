# TD-02 · Release 0.2.0: Payments, History, Reports, Export and Import

**Status:** Implemented (2026-11-23, C-006). It has unit and integration tests; per C-005 the owner tests it on a phone at the end.

## Summary
- **Weekly payments.** The week report shows what each worker is owed for the Sunday–Saturday week. The owner picks a paid date and marks a worker (or the whole week) paid, which **freezes** that payment.
- **Corrections after payment.** If an entry in a paid week is corrected later, the difference is added to (or taken from) the **next unpaid week** as a correction.
- **Wages to pay.** The dashboard shows wages whose payday has passed and that aren't marked paid.
- **Worker history.** Each worker's screen lists their last 12 weeks.
- **More reports.** Reports have Week, Month and Year views.
- **Export and import.** Settings → Export all data writes one readable file, shared through Android's share sheet. Import replaces everything after a confirmation, and an automatic safety copy is saved first. A new phone can restore from that file on its first screen.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Factory/Payments.cs` | `Payment` (stored only when paid), `WeekPayment` (a computed line), `PaymentCalculator` (rules) |
| `Core.Workflow/PaymentService.cs` | Week lines, mark paid, mark whole week, undo, wages due, history |
| `Core.Workflow/DataExchange.cs` | `FactoryData` (file content), `FactoryDataFile` (read, write, validate), `ImportPreview` |
| `Core.Workflow/DataService.cs` | Export, preview, import (with the access rules) |
| `Core.Workflow/AccountService.cs` | `OwnerNeedingPinAsync` / `SetFirstOwnerPinAsync` (after an import on a new phone) |
| `Storage.Sqlite` migration 2 | `payment` table; `ReplaceAllAsync` (import in one transaction) |
| App: `ReportViewModel`/`ReportPage` | Week, Month and Year views; payments in the week view |
| App: `HomeViewModel` | "Wages to pay" card → opens the oldest unpaid week |
| App: `WorkerViewModel` | "Recent weeks" history |
| App: `SettingsViewModel`, `SetupViewModel`, `DataFiles`, `NewPinPage` | Export, share, import, restore on a new phone, safety copies, choosing a PIN after import |

## How it works

### Payments are frozen when marked paid (D11)
Only paid weeks are stored. A `Payment` row holds the sarees and earnings **as they were when marked paid**, the correction settled with it, the total and the paid date. Pending weeks are always computed from the entries, so a pending amount can never go stale.

```
outstanding correction(employee) = Σ over paid weeks (earnings computed NOW)
                                 − Σ over paid weeks (frozen earnings + correction settled)
```
In other words, "what the paid weeks are worth today" minus "what was actually paid for them". It is zero until someone corrects a paid week. It can be negative: if a worker was paid for 4 sarees but made 3, the extra ₹500 is taken back from the next payment.

The correction is added only to the **oldest unpaid week that has work**, so it is never counted twice. Once that week is paid, its stored `Adjustment` cancels the outstanding amount (tested).

- **Mark paid:** allowed for a pending line. The paid date must fall between the week's Sunday and today, so the owner can pay on the Saturday or later.
- **Undo** (a mistake): deletes the payment row, and the week becomes pending and is recomputed. Both actions are audited.
- **Mark all paid** pays every pending line of the week in **one transaction**.
- **Wages to pay:** pending lines in weeks whose Saturday is today or earlier. The current week only counts from its payday.

### Day entry and paid weeks
`DaySheetRow.WeekPaid` marks workers whose week is already paid. The day screen then shows "week already paid: a change is added to the next payment", so the owner isn't surprised.

### Export file (D18)
- Indented JSON, marked with `"kind": "handloom-factory-data"` and a `formatVersion` (currently 1).
- **Money is written as whole paise** (`"rate": 50000` means ₹500), so the file is exact; dates are `yyyy-MM-dd`.
- Contents: factory, every employee (including the owner), rate history, every entry, every payment and the whole audit log.
- **PINs and keys are never in the file** (tested: the word "pin" doesn't appear).
- The file is shared from the app cache through Android's share sheet (WhatsApp, Drive, Gmail, Files…). Only the latest export stays in the cache.

### Import replaces everything
1. **Pick a file** (JSON, plain text or "unknown" type are all accepted, because WhatsApp and Drive often relabel `.json`).
2. **Read and validate** it:
   - the file marker must be present and the format version not newer than this app's (newer gives "please update the app");
   - every section must be present, with an owner;
   - no duplicate ids, codes, (employee, day) entries or (employee, week) payments;
   - no records for unknown employees, and no negative counts or zero rates.

   A bad file is refused **before anything changes** (tested).
3. **Confirm:** show the factory name, export time, workers, entries, payments and date range, with the warning "This REPLACES everything".
4. **Safety copy:** the current data is exported to `safety-copies/before-import-<time>.json` (the newest 5 are kept).
5. `ReplaceAllAsync` runs in **one SQLite transaction**: delete, then write everything. A failure leaves the phone exactly as it was.
6. **PINs:** people present in the imported data keep their PIN, so re-importing on the same phone keeps the owner's PIN. Everyone else's PIN is removed.
7. **Log out** and go to the next step: login, or **"Choose your PIN"** when the imported owner has no PIN on this phone (a new phone).

**Who may import:** the logged-in owner, or a phone that isn't set up yet. That is the "Restore from a data file" button on the first screen of a new phone. Once a phone is set up, nobody else can overwrite it (tested).

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| Store only paid weeks; compute pending ones | Pending amounts can never be out of date | Generate a payment row every Saturday (stale if entries change, needs a job) |
| Correction = "owed for paid weeks − paid for them" | One formula, self-correcting, handles over- and under-payment, never double counts | Track each correction as a separate record (more state to keep in sync) |
| Correction goes to the oldest unpaid week | It is paid at the next payment, as the requirements say | Split it across weeks |
| Paid date between Sunday and today | Owners sometimes pay early or late; future dates are a typo | Saturday only |
| Payments shown inside the weekly report | One screen shows both "what was made" and "what is paid"; keeps 5 tabs | A separate Payments tab (6 tabs is too many for Android's bottom bar) |
| Plain JSON file, money in paise, version number | Readable, exact, safe to evolve | CSV (several tables, loses structure); encrypted (owner chose plain) |
| Import replaces everything, in one transaction, with a safety copy | Predictable; a failure or mistake is recoverable | Merge (surprising conflicts), chosen against by the owner |
| Keep PINs of people present in the imported data | Re-importing on the same phone doesn't lock the owner out | Always clear PINs (needless "choose PIN" on the same phone) |
| "Restore" on the setup screen | A new phone shouldn't need a dummy factory first | Import only from Settings |

## Tests (16 new; 99 in total)
`Core.Workflow.Tests/PaymentAndDataTests.cs`:
- pending → paid → frozen; already paid is refused;
- a correction after payment shows on the paid week ("corrected since paid") and becomes a correction on the next week, then disappears once that week is paid;
- an over-payment is taken back;
- the correction goes to the oldest unpaid week only;
- whole week, undo, and wages to pay (current week not counted before payday);
- paid date range and "nothing to pay";
- history order and status;
- month and year reports.

Export and import:
- new phone (PIN needed, everything restored including audit) and a phone that isn't set up;
- re-import keeps the owner's PIN and drops later changes;
- bad files, newer files, and workers refused.

## Known limits
- **Sync of the export file.** Exported data restores only on the owner's phone. Worker phones (0.3.0) must be re-linked after a restore on a new owner phone, because channel keys are deliberately not exported.
- A worker's phone can't export yet (planned with the worker app in 0.3.0).
