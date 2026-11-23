# TD-00 · Overall Architecture

**Status:** Designed (2026-11-23, C-001). Not implemented yet. Each section will get its own TD document when it is built.

## Summary
The app runs only on Android phones and has **no server of its own** (D1). Each phone keeps an **encrypted SQLite database**. The **owner's phone is the hub**: it holds the master copy for the whole factory. Each **worker's phone** holds only that worker's data (D5).

The phones exchange **encrypted, signed messages** through the free **ntfy.sh** relay. Each worker has **one private channel** (topic + key) with the owner. This design is adapted from Travel Vault / Use It Before (TD-10 there). It is simpler here, because the trust model is a star rather than a group of equal family members.

Logins use a **name + 4-digit PIN** (D2). All wage figures are computed by pure functions from entries and rate history. The pay week runs **Sunday–Saturday** (D7).

```
          Worker phone (Ramesh)            Worker phone (Suma)
          only Ramesh's data               only Suma's data
                 │  channel A (topic A, key A)    │  channel B (topic B, key B)
                 ▼                                ▼
            ┌───────────────────── ntfy.sh ─────────────────────┐
            │  temporary encrypted mailbox (~12 h), no accounts │
            └───────────────────────────────────────────────────┘
                                   ▲
                                   │ reads/writes every channel
                         Owner phone (hub, master copy)
                         all employees, rates, entries, payments, audit
```

## 1. Components
The project uses the same component model as the owner's other apps. Everything except the app and the `Device.*` projects is plain .NET 10, so it can be unit-tested on a PC.

| Component | Responsibility | Depends on |
|---|---|---|
| `Core.Common` | `IClock`, ids, Hybrid Logical Clock, JSON options, `Money` (paise), `PayWeek` date helpers | – |
| `Core.Security` | PIN hashing (PBKDF2), AES-256-GCM, ECDSA P-256, HKDF, HMAC proofs | Common |
| `Core.Factory` | Domain: `Factory`, `Employee`, `RateChange`, `ProductionEntry`, `Payment`, `AuditEntry`, `WageCalculator`, rules | Common |
| `Core.Reports` | Weekly / monthly / yearly reports, dashboard figures, CSV writer | Factory |
| `Core.Workflow` | Application services (use cases), validation, audit writing, role checks | Factory, Reports, Security |
| `Storage.Sqlite` | Encrypted DB, migrations, repositories, sync tables | Factory, Common |
| `Core.Relay` | ntfy publish/poll client behind `IRelay` (copied from Travel Vault) | – |
| `Core.Sync` | Channel protocol, linking, outbox, apply rules, ntfy notifications | Relay, Security, Factory |
| `Core.AppUpdater` | GitHub-release update check + SHA-256 verified download (copied) | – |
| `Device.Security` | Android Keystore key wrapping, phone screen-lock prompt | Security |
| `Device.BackgroundWork` | Android JobScheduler periodic work (copied) | – |
| `HandloomFactory.App` | MAUI screens, view models (CommunityToolkit.Mvvm), DI, resources (en, kn) | all |

Rules: dependencies point inward (the app → workflow → domain). The domain has no I/O. Time always comes from `IClock`, so pay-week logic can be tested on any date.

## 2. Data model

### 2.1 Money and dates
- Money is stored as **`long` paise** (`Money` value type). `₹500.00` = `50000`. There is no floating point anywhere.
- Dates are `DateOnly` in the **factory time zone**. The time zone is stored in `Factory` at setup and defaults to the phone's zone (Asia/Kolkata). Every phone uses this zone, not its own, to decide "today" and the pay week.

### 2.2 Entities
| Entity | Key fields | Notes |
|---|---|---|
| `Factory` | Id, Name, OwnerName, TimeZone, MaxSareesPerDay (50), CreatedAt | One per owner phone |
| `Employee` | Id, Code (`E001`…), FullName, Phone?, Role, IsActive, CreatedAt | The owner is also an `Employee` with Role = Admin (for login) |
| `RateChange` | Id, EmployeeId, RatePaise, EffectiveFrom, CreatedAt | Rate on day D = the latest change with EffectiveFrom ≤ D |
| `ProductionEntry` | **Id = hash(EmployeeId, EntryDate)**, EmployeeId, EntryDate, SareeCount, EnteredBy (Employee/Admin), CreatedAt, UpdatedAt, Version (HLC) | The deterministic id means both phones upsert the same record |
| `Payment` | Id = hash(EmployeeId, WeekStart), EmployeeId, StartDate (Sun), EndDate (Sat), Status, FrozenSarees, FrozenEarningsPaise, AdjustmentPaise, TotalPaise, PaidDate? | Rows exist only for weeks that have been marked Paid. Pending weeks are computed on the fly |
| `AuditEntry` | Id, At, ActorEmployeeId, Action, TargetType, TargetId, OldJson, NewJson, Source (Local/Sync) | Append-only; stored on the owner phone |

### 2.3 Tables (Storage.Sqlite)
```
factory(id PK, name, owner_name, time_zone, max_per_day, created_at)
employee(id PK, code UNIQUE, full_name, phone, role, is_active, pin_hash, pin_salt, pin_iters,
         failed_pins, locked_until, created_at, version)
rate_change(id PK, employee_id FK, rate_paise, effective_from, created_at, version)
     INDEX(employee_id, effective_from)
production_entry(id PK, employee_id FK, entry_date, saree_count, entered_by, created_at, updated_at, version)
     UNIQUE(employee_id, entry_date)
payment(id PK, employee_id FK, start_date, end_date, status, frozen_sarees, frozen_earnings_paise,
        adjustment_paise, total_paise, paid_date, version)  UNIQUE(employee_id, start_date)
audit_log(id PK, at, actor_id, action, target_type, target_id, old_json, new_json, source)
settings(key PK, value)
sync_channel(employee_id PK, relay_url, topic, epoch, key_wrapped, peer_sign_pub, state, last_msg_id,
             last_sync_at, next_seq, peer_last_seq, invite_id, invite_secret_hash, invite_expires)
outbox(id PK, employee_id, payload, created_at, attempts)
```
- `version` holds the HLC of the last change and is used for last-writer-wins.
- The schema is versioned through `PRAGMA user_version` and ordered migrations in code (no ORM), as in Travel Vault.
- **Encryption:** SQLite3 Multiple Ciphers. The 256-bit database key is random and stored wrapped by an Android Keystore key. It is **not** derived from the PIN, because background sync must be able to open the database without the user. The PIN only gates the UI.

## 3. Wage and pay-week rules (Core.Factory)

### 3.1 Pay week (D7)
```
WeekStart(d) = d − (int)d.DayOfWeek days      // DayOfWeek.Sunday = 0
WeekEnd(d)   = WeekStart(d) + 6               // Saturday
```
Example: Mon 2026-11-23 → week 2026-11-22 (Sun) … 2026-11-28 (Sat), paid Sat 2026-11-28.

Sunday work therefore belongs to the Saturday payment that follows it, which is what the owner asked for. Month = calendar month; year = calendar year (reports only).

### 3.2 Earnings
```
earnings(employee, range) = Σ over entries e in range:  e.SareeCount × rateOn(employee, e.EntryDate)
rateOn(employee, d)       = rate of the RateChange with the greatest EffectiveFrom ≤ d
```
- An entry dated before the employee's first rate change is an error that validation prevents. The first rate is required when an employee is created, effective from that date.
- The "Rate" column in a report shows the single rate, or "varies" when the rate changed inside the period.

### 3.3 Payments and adjustments (D11)
- **Marking Paid** freezes the week: `FrozenSarees` and `FrozenEarnings` are copied from the computed values, and `Adjustment` = the outstanding adjustment at that moment. `Total = FrozenEarnings + Adjustment`.
- **Outstanding adjustment** for an employee is
  `Σ over paid weeks (computedEarningsNow(week)) − Σ over paid weeks (FrozenEarnings + Adjustment)`.
  This is "owed for paid weeks minus actually paid". It is zero unless an entry in a paid week was corrected later. It can be negative (an over-payment is recovered).
- A **pending week's amount** = computed earnings of that week + outstanding adjustment (if it is the oldest unpaid week).
- **Unmarking** a payment (a mistake) is allowed for the owner and is audited.

### 3.4 Entry rules
| Who | Allowed | Rejected |
|---|---|---|
| Employee | EntryDate == today (factory zone), 0 ≤ count ≤ MaxPerDay, own record only | Past/future dates; entries in a Paid week; a record already edited by Admin |
| Admin | Any date ≤ today, any active or disabled employee | Future dates; count out of range; dates before the employee's first rate |

Editing an entry in a **Paid** week is allowed for the admin. It produces an adjustment (3.3) and an audit entry, and the screen warns "This week is already paid; the difference will be added to the next payment".

## 4. PIN login (Core.Security, Core.Workflow)
- PBKDF2-HMAC-SHA256, 16-byte random salt, 210,000 iterations, 32-byte hash. The iteration count is stored per user so it can be raised later. Comparison is constant-time.
- **Lockout:** after 5 failures, a 30 s wait, doubling on each further failure up to 15 min. It resets on success and is stored in the DB so restarting the app does not reset it.
- **Session:** on a worker phone the session lasts 30 days. On the owner phone the PIN is required again after 5 min in the background (D3).
- **Honest limit:** a 4-digit PIN has only 10,000 combinations. It protects against casual use of an unlocked phone, not against someone who can extract the encrypted database and the Keystore key. That is acceptable for this threat model and is documented.
- **Forgot PIN:** a worker is re-linked by the owner (section 5.3). The owner confirms with the phone's screen lock (`BiometricPrompt` with device credential) and then sets a new PIN.

## 5. Sync (Core.Relay, Core.Sync)

### 5.1 Channel
Each linked worker has one channel with these properties:
- **Topic:** 32 random base62 characters (~190 bits), so it cannot be guessed.
- **Channel key:** a random 256-bit key with a readable epoch number. Message keys are derived with HKDF.
- **Signatures:** each phone has its own ECDSA P-256 device key. Both public keys are pinned in `sync_channel`.

### 5.2 Wire format (simplified from Travel Vault)
```
Envelope { v=1, channel, from (owner|worker), seq, sent (HLC), kind, body }
  JSON → gzip → pad to bucket (384/768/1536/2600 B) → AES-256-GCM(label "msg/<channel>/<epoch>")
  → ECDSA signature over ciphertext
wire = "hf1.<epoch>.<b64url ciphertext>.<b64url signature>"   (≤ 3,800 chars, ntfy limit ~4 KB)
```
**Receive checks:** the epoch key is known → it decrypts → it is not my own message → the signature verifies against the pinned peer key → `seq` > the last seen from the peer (replay protection) → apply.

Large snapshots (a full year of entries) are split across several messages by the batching codec.

### 5.3 Linking a worker phone
1. The owner opens Employee → **Link phone**. The owner phone creates the channel and shows a **QR code** (valid 15 min, one-time). It contains: factory name, employee id and name, relay URL, topic, epoch, channel key, the owner's signing public key, and an invite id + secret.
2. The worker installs the app, taps **"I am a worker"**, and scans the code. The phone creates its device key and publishes `link {devicePub, HMAC(secret, devicePub)}`.
3. The owner phone verifies that the invite is unused and unexpired and that the proof is valid. It pins the worker key and replies with `link-ok` plus a **snapshot**: profile, rates, entries, payments and factory settings. The owner sees "Ramesh's phone is linked ✓".
4. The worker phone verifies the owner's signature (its key came from the QR, which is the trust anchor), stores the snapshot, and asks the worker to **choose a PIN**.

**Re-link** (new phone or forgotten PIN): the owner creates a new channel (new topic, key and epoch) and sends `revoked` on the old topic. The old phone shows "This phone was unlinked" and wipes its data. The new phone receives the full history in the snapshot.

**Disable employee:** the same `revoked` message is sent and the channel is closed. History stays on the owner phone.

### 5.4 What moves on a channel
| Record | Author | Direction |
|---|---|---|
| Employee profile, factory settings (name, zone, max/day, notify topic) | Owner | owner → worker |
| RateChange | Owner | owner → worker |
| ProductionEntry | Worker (today) or Owner (any day) | both ways |
| Payment | Owner | owner → worker |

Records travel as `changes` messages containing whole records with their HLC `version`. The authority rules are enforced **on the receiving side**. The owner phone ignores and logs any worker-authored record that is not the worker's own ProductionEntry for a date allowed by 3.4. The worker phone accepts only owner-signed messages, so anything arriving on its channel is accepted.

### 5.5 Conflicts (D10)
- ProductionEntry: if the stored record has `EnteredBy = Admin`, a worker version is ignored. Otherwise the higher HLC wins. The HLC (wall time + counter + node) protects against small clock differences.
- **Worker date check:** the owner accepts a worker entry only if the wall-time part of its HLC falls on `EntryDate` in the factory zone. Entries made late at night and synced days later are therefore still accepted.
- **Known gap:** a worker who changes the phone's date could backdate an entry. The owner sees "entered at" times and the audit log, and admin edits override. This is accepted for the current scale.

### 5.6 Sync cycle (every ~15 min, on app open, right after Save, pull-to-refresh)
```
for each channel on this phone (worker: one; owner: one per linked worker):
  1. poll the topic since last_msg_id → verify → apply (5.4/5.5) → write audit (owner)
  2. answer link requests (owner) / state requests (either side)
  3. if last sync > 11 h ago → publish state-request (the peer republishes the channel snapshot)
  4. flush the outbox (exponential backoff on errors / HTTP 429)
  5. update last_sync_at; check-in every 6 h (owner dashboard shows "last seen")
```
Every step is wrapped, so one failing channel never blocks the others. Unsent changes stay in the outbox and nothing is lost offline. On the owner phone, channels are polled concurrently (max 4) to keep a 50-worker cycle short.

### 5.7 ntfy push notifications (optional)
- These are separate from sync. The owner sets a **notify topic** in Settings (off by default). It reaches workers' phones inside the factory settings record.
- Worker phone, after a successful Save: `POST <relay>/<notifyTopic>` with the body "Ramesh submitted 3 sarees today" and title = factory name. If it fails, the message is queued and sent once (no duplicates).
- Owner phone background job, on Saturday after 18:00 (once per week): "Weekly payments ready: ₹42,500 for 8 workers".
- The text is **plain**, not encrypted. The Settings screen says so when the feature is enabled.

## 6. Use cases (Core.Workflow, D14)
Each use case is a class with one `ExecuteAsync`. It takes a request record and returns a `Result<T>` with error codes that map to localised messages. Each one does four things in a single DB transaction:
1. checks the role of the logged-in user,
2. validates the input,
3. applies the domain rule,
4. writes the change, the audit entry and the outbox message.

| Commands | Queries |
|---|---|
| SetUpFactory, CreateEmployee, UpdateEmployee, DisableEmployee, ChangeRate, LinkEmployeePhone, RelinkEmployeePhone, SaveProductionEntry, MarkPaymentPaid, UnmarkPayment, ChangePin, ExportReport | GetEmployeeDashboard, GetAdminDashboard, GetWeeklyReport, GetMonthlyReport, GetYearlyReport, GetEmployeeHistory, GetAuditLog |

## 7. App (HandloomFactory.App)
- **First launch:** two big buttons, "I am the owner" (set up factory) and "I am a worker" (scan QR).
- **Worker:** Login → Dashboard, which has the big "Today's sarees" number box, Save, and the week/month cards → History → Payments.
- **Owner:** Login → Dashboard → tabs: Today | Employees | Reports | Payments | Settings.
- Text comes from `.resx` resources in English and Kannada (`AppResources.kn.resx`), with a language switch in Settings. Numbers use Western digits; money uses `en-IN` grouping (₹21,000).
- Accessibility: large fonts (number entry 48 sp), a minimum touch target of 56 dp, high contrast, and no gestures that are hard to discover.

## 8. Background work and logging
| Job | When | Phone |
|---|---|---|
| Sync cycle | ~15 min (JobScheduler, network required) | both |
| Weekly "payments ready" notice | Saturday ≥ 18:00, once per week | owner |
| Cleanup | daily: log files > 7 days, delivered outbox rows, expired invites | both |

Logging goes to a structured JSON-lines rolling file (7 days) through `Microsoft.Extensions.Logging`. Logged events: logins (success/failure, never the PIN), employee and rate changes, entries, payments, sync errors and crashes. The owner can share the log file from Settings.

## 9. Testing
- **Domain:** pay-week boundaries (Sunday, Saturday, month/year edges), rate changes mid-week, adjustments after corrections, entry rules for both roles.
- **Workflow:** every use case against a real temporary SQLite database, including role checks and audit entries.
- **Sync:** an owner phone plus several worker phones talking through an **in-memory relay that expires messages after 12 h**, with a shared manual clock. Scenarios: linking, a worker entry reaching the owner, an admin correction overriding a worker edit, the 2-day offline catch-up, re-linking (the old phone is cut off), disabling a worker, tampered/replayed messages, and checking that a worker's phone never receives another worker's data.
- Coverage target: 80%+ for `Core.*` and `Storage.*`.

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| ntfy relay, owner phone as hub (D1, D5) | No server or cost. Privacy between workers comes from separate channels. | Own API + PostgreSQL (needs hosting); Firebase (Google account and lock-in); one shared topic (every phone receives everyone's wages) |
| Deterministic entry id (employee + date) | "One entry per employee per day" holds across phones without coordination | Random ids + a merge step |
| Admin edits always win (D10) | Corrections must stick even if a worker edits later | Pure last-writer-wins |
| Freeze paid weeks + computed adjustment (D11) | Paid money never changes silently; the formula self-corrects | Editing the paid amount; forbidding corrections |
| Money in paise (`long`) | Exact arithmetic | `decimal` rupees (fine, but paise matches storage) or `double` (rounding errors) |
| DB key in Keystore, not from the PIN | Background sync works while the app is locked | PIN-derived key (sync only while open; 4 digits is a weak key anyway) |
| No MediatR / CQRS library (D14) | Fewer moving parts; the same pattern as the other apps | MediatR pipeline |
