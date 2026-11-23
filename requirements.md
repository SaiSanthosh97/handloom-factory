# Project: Handloom Factory Production & Wage Tracking System

> **Living document.** Every change to these requirements is recorded in [docs/CHANGES.md](docs/CHANGES.md) and every decision has an ID (D-xx) in [§ Decisions](#decisions).
> Technical design: [docs/technical/README.md](docs/technical/README.md).
> Last updated: 2026-11-23 (C-002: MVP releases, update alerts, full export/import).

## Objective

Build a production-ready **Android app** for a small handloom factory.

Primary goal:

Track the sarees each employee produces and calculate weekly and monthly earnings automatically.

The factory currently has fewer than 10 employees, but the design must cope with growth (target: up to ~50 workers on one owner phone).

The app must be extremely simple to use for workers with limited technical knowledge.

- Do not over-engineer the UI.
- Focus on simplicity, reliability and maintainability.
- **No server of our own.** Everything runs on the phones; phones sync through the free ntfy.sh relay (D1).

---

# Business Requirements

## Roles

### Employee (worker)

Uses **their own Android phone** (D3).

Can:

- Log in with a 4-digit PIN
- Enter the sarees produced **today**
- Edit today's entry until the day ends (midnight)
- View their own production history
- View their own weekly earnings
- View their own monthly earnings
- View their own payment status (Pending / Paid)

Cannot:

- View other employees
- View factory reports
- View the wages of other employees (their phone never receives other workers' data, D5)
- Enter or change entries for past days (D9)

---

### Factory Owner (Admin)

Uses their own Android phone, which holds the **master copy** of all data (D5).

Can:

- Set up the factory (first launch)
- Add employee
- Edit employee
- Disable employee
- Link an employee's phone (QR code) and re-link it (new phone or forgotten PIN)
- Set the rate per saree for each employee
- Update the rate per saree (from a chosen date, with history)
- View all employee production
- View weekly, monthly and yearly reports
- View wage calculations
- Add or correct any employee's entry for any day
- Mark weekly wage payments as Paid (with paid date)
- Export reports to CSV (opens in Excel)
- View the audit log

---

# User Flow

## Employee Flow

First time only: Owner shows a QR code → worker scans it → worker chooses a 4-digit PIN.

Then:

Login (tap name + PIN)

↓

Dashboard

↓

Enter Today's Sarees

↓

Save (works offline; syncs later)

↓

View Own Earnings

### Example

Employee Login: tap **Ramesh**, enter PIN `••••`

↓

Today's Sarees

[ 3 ]

Save

↓

This Week (Sun 22 – Sat 28 Nov)

12 Sarees

₹6000 Earnings

↓

This Month (November)

42 Sarees

₹21000 Earnings

---

# Admin Flow

First time only: Set up factory (factory name, owner name, owner PIN).

Then:

Login (tap name + PIN)

↓

Dashboard

↓

View Production Summary

↓

Manage Employees

↓

View Weekly Report

↓

View Monthly Report

↓

Mark Payments Paid (Saturday)

---

# Login (simple PIN, D2)

- The login screen shows the name(s) set up on this phone. The user taps their name and enters a **4-digit PIN**. No passwords, no OTP, no accounts.
- On a worker's own phone there is only one name, so login is effectively "enter PIN".
- The PIN is stored only as a salted hash on the phone, never in plain text and never sent anywhere.
- **Worker phone:** stays logged in for 30 days (D3). The PIN is asked again after 30 days or after logging out.
- **Owner phone:** asks for the PIN each time the app is opened after 5 minutes in the background, because it holds everyone's wages.
- 5 wrong PINs → wait 30 seconds (doubling on further failures).
- **Worker forgot PIN:** the owner taps "Re-link phone" for that worker and shows a new QR. The worker scans it and chooses a new PIN. Their history comes back from the owner's phone.
- **Owner forgot PIN:** confirm with the phone's own screen lock (fingerprint / phone PIN), then choose a new app PIN.

---

# Dashboard Requirements

Employee Dashboard:

- Today's Sarees (with a large entry box)
- This Week's Sarees
- This Month's Sarees
- This Week's Earnings
- This Month's Earnings
- Last payment status
- Sync status ("Sent to owner" / "Waiting for internet")

Admin Dashboard:

- Total Employees (active)
- Today's Production (and who hasn't entered yet)
- Weekly Production
- Monthly Production
- Weekly Wage Liability
- Monthly Wage Liability
- Pending payments
- Last sync time per worker

---

# Saree Entry Rules

One entry per employee per day.

Employee can:

- Create the entry for the current day
- Edit the same day's entry until the day ends (midnight, phone time)

Admin can:

- Create or edit any entry for any day (e.g. a forgotten day, or a worker without a smartphone)

If the admin has edited an entry, the admin's value wins; later edits from the worker's phone for that day are ignored (D10).

A count of 0 is allowed. The maximum is 50 per day (guards against typing mistakes; configurable).

Example:

Date: 2026-11-23
Employee: Ramesh
Sarees: 3

---

# Wage Calculation

Every employee has their own rate per saree (D6). Rates have history: a change applies from a chosen date, and earlier days keep the old rate.

Earnings for a day = sarees that day × the employee's rate in effect on that day.

Example:

Ramesh

Rate = ₹500 per saree

Week Total = 10 sarees

Earnings = 10 × 500

Result = ₹5000

The system calculates everything automatically. No manual calculations. Money is calculated in whole paise (no rounding drift).

---

# Pay Week (D7)

- The factory works **Monday to Saturday**. **Sunday is a holiday**, but an employee may choose to work on Sunday.
- Wages are paid **every Saturday, at the end of the day**.
- **Pay period = Sunday to Saturday.** Saturday's payment covers the Sunday just before (if worked) + Monday–Saturday, including Saturday's own production.
- Example: the payment on **Sat 2026-11-28** covers **Sun 2026-11-22 – Sat 2026-11-28**.
- The "This Week" figures on both dashboards follow this same Sunday–Saturday period.
- Monthly = calendar month. Yearly = calendar year (January–December). Both are for reports only; payments are weekly.

---

# Reports

## Weekly Report (Sunday–Saturday)

Show:

- Employee Name
- Sarees Produced (per day and total)
- Rate (shows "varies" if the rate changed during the week)
- Earnings
- Payment Status

## Monthly Report

Show:

- Employee Name
- Sarees Produced
- Rate
- Earnings

## Yearly Report

Show:

- Employee Name
- Total Sarees
- Total Earnings

All reports can be exported to CSV (UTF-8, opens correctly in Excel, including names in any script) and shared via WhatsApp, email or Google Drive.

---

# Payment Tracking

One payment record per employee per pay week.

Example:

Employee Weekly Earnings: ₹5000

Payment Status:

- Pending
- Paid

Admin marks Paid and sets the Paid Date (default today).

Example: 2026-11-28

When marked Paid, the week's sarees and amount are **frozen** in the payment record. If an entry in a paid week is corrected afterwards, the difference is shown as an **adjustment** and added to the next week's payment (D11).

The worker sees their own payment status on their phone.

---

# Data Sync (no server, D1, D4, D5)

- Each phone keeps its own database. All actions work offline and are saved on the phone first.
- Phones exchange encrypted messages through **ntfy.sh** (free, no account), or a self-hosted ntfy server set in Settings.
- **Owner is the hub.** Each worker has a **private encrypted channel** (topic + key) shared only between that worker's phone and the owner's phone.
  - Worker → owner: their daily entries.
  - Owner → worker: their profile, rates, corrected/added entries, payments.
- Sync runs about every 15 minutes in the background, when the app opens, and right after saving.
- The relay only ever sees encrypted data and forgets it after about 12 hours. A phone that was offline longer asks the other side to resend.
- Linking a worker's phone: the owner shows a one-time QR code (valid 15 minutes) that carries the channel details.
- Disabling a worker stops their channel; their history stays on the owner's phone.

---

# Notifications (NTFY, optional)

- Configured by the owner in Settings (on/off, ntfy topic). Off by default.
- Worker submits production → a push to the owner's ntfy topic: "Ramesh submitted 3 sarees today".
- Saturday evening → "Weekly payments ready: ₹X for N workers".
- The owner subscribes to that topic in the free ntfy Android app.
- Notification text is **not** encrypted (names and counts are visible to the relay); this is shown in Settings when turning it on.

---

# Non Functional Requirements

- Android only, **Android 6.0 and newer** (D19). This covers most phones from 2016 on, including 32-bit ones.
- Mobile first, large touch targets, big numbers
- Fully offline; syncs when internet is available
- Fast data entry: today's entry in ≤ 3 taps after login
- Language: **English** (Kannada cancelled by the owner, C-005; the text is kept in a resource file, so a language can still be added later) (D8)
- Simple PIN login (see Login)
- Data on the phone is stored in an encrypted database
- Audit log of all changes
- Free and open-source dependencies only (MIT / Apache-2.0 / BSD)

---

# Technical Stack (D12)

Same approach as the owner's other apps (Use It Before, Travel Vault).

## App

.NET 10 MAUI, Android

- MVVM with CommunityToolkit.Mvvm
- Dependency Injection
- Clean separation: domain and application logic in plain .NET libraries (unit-testable on a PC); the app project contains only screens and Android glue.

## Storage

SQLite on the phone (Microsoft.Data.Sqlite + SQLite3 Multiple Ciphers for encryption). Versioned schema migrations in code.

## Sync

ntfy relay, AES-256-GCM encryption, ECDSA signatures, Hybrid Logical Clock — adapted from Travel Vault / Use It Before (`Core.Relay`, `Core.Sync`), copied into this repo (D13).

## Background work

Android JobScheduler (about every 15 minutes) for sync, Saturday payment preparation and cleanup. (Replaces Hangfire.)

## Logging

Structured logging to a rolling file on the phone (7 days), exportable from Settings for support. Logs: logins, employee updates, production entries, payment changes, sync errors, application errors. No PINs or keys are ever logged.

## Removed from the original stack

ASP.NET Core Web API, PostgreSQL, EF Core, Docker / Docker Compose, Hangfire, JWT / refresh tokens, MediatR. (Reason: no server, C-001.)

---

# Architecture

```
HandloomFactory.slnx

src/
  Apps/
    HandloomFactory.App            (.NET MAUI Android app: screens, view models, DI)
  Components/
    Core.Common                    (clock, ids, hybrid logical clock, JSON, money)
    Core.Security                  (PIN hashing, encryption, signatures)
    Core.Factory                   (domain: employees, rates, entries, pay weeks, payments, audit)
    Core.Reports                   (weekly / monthly / yearly reports, CSV export)
    Core.Workflow                  (application services: the use cases below)
    Core.Relay                     (ntfy client)
    Core.Sync                      (owner-hub sync, linking, notifications)
    Core.AppUpdater                (in-app updates from GitHub releases)
    Storage.Sqlite                 (encrypted database, migrations, repositories)
    Device.BackgroundWork          (Android periodic jobs)
    Device.Security                (Android Keystore, phone screen lock)

tests/
  one test project per component (e.g. Core.Factory.Tests)
```

---

# Domain Entities

## Factory

- Id
- Name
- OwnerName
- CreatedAt

## Employee

- Id
- EmployeeCode (auto, e.g. E001)
- FullName
- PhoneNumber (optional)
- Role (Admin / Employee)
- IsActive
- CreatedAt
- LinkedDevice (when the phone is linked)

## RateChange (rate history)

- Id
- EmployeeId
- RatePerSaree
- EffectiveFrom (date)
- CreatedAt

## ProductionEntry

- Id
- EmployeeId
- EntryDate
- SareeCount
- EnteredBy (Employee / Admin)
- CreatedAt
- UpdatedAt

## Payment

- Id
- EmployeeId
- PeriodType (Weekly)
- StartDate (Sunday)
- EndDate (Saturday)
- TotalSarees
- Rate (or "varies")
- Adjustment (from corrections to earlier paid weeks)
- TotalAmount
- PaymentStatus (Pending / Paid)
- PaidDate

## AuditEntry

- Id
- At
- Actor
- Action
- Target
- OldValue / NewValue

---

# Use Cases (D14)

Commands:

- SetUpFactory
- CreateEmployee
- UpdateEmployee
- DisableEmployee
- ChangeRate
- LinkEmployeePhone / RelinkEmployeePhone
- SaveProductionEntry (employee: today only; admin: any day)
- MarkPaymentPaid
- ExportReport

Queries:

- GetEmployeeDashboard
- GetAdminDashboard
- GetWeeklyReport
- GetMonthlyReport
- GetYearlyReport
- GetEmployeeHistory
- GetAuditLog

Every command validates its input and writes an audit entry.

---

# Testing

Mandatory:

- Domain tests (wage calculation, pay weeks, rate history, entry rules)
- Application tests (every use case, against a real SQLite database)
- Sync tests (owner + several worker phones through an in-memory relay that expires messages like ntfy.sh)

Target: 80%+ coverage of the `Core.*` and `Storage.*` components.

---

# Security

- 4-digit PIN, stored as a salted PBKDF2 hash, with lockout after wrong attempts
- Role-based screens and use cases (Admin / Employee)
- Encrypted database on each phone (key kept in the Android Keystore)
- Encrypted and signed sync messages; each worker's channel has its own key
- Input validation on every use case
- Audit log

---

# Data Export & Import (D15, D18)

- **Export all data:** the owner can export everything to one file: factory, employees, rate history, all production entries, all payments (paid dates, amounts, adjustments) and the audit log.
  - File name: `handloom-data-YYYY-MM-DD.json`. Shared through WhatsApp, Drive or email, or saved to the phone.
  - It is a **plain, readable file**, not password-protected (owner's choice). Anyone with the file can read the wages. The app says this before sharing.
  - **PINs are never written to the file.** After an import, the owner sets a new PIN.
- **Import (re-import):** choose an exported file, see a summary (employees, entries, payments, date range), and confirm.
  - Import **replaces everything** on the phone with the file's contents.
  - Before replacing, the app automatically saves the current data as an export in the app's folder, so a mistaken import can be undone.
  - Files from a newer app version are refused with "Please update the app first". Damaged files are refused without changing anything.
- Reports can also be exported as CSV (opens in Excel).
- A worker can export their own history (from 0.3.0).

---

# App Updates (D16)

- Every new version is published as a release on **github.com/SaiSanthosh97/handloom-factory** (public: only the app and its source code, never factory data).
- **Every time the app is opened or brought back** (at most once an hour, on any internet connection), it checks for a newer version. When there is one, it shows:
  - a pop-up "New version X is available: Install / Later", once per version, and
  - an **"Update available" banner** on the dashboard until it is installed.
- Settings → **Check for updates** checks immediately.
- From 0.3.0 the background job also checks daily and shows an Android notification "Handloom Factory update available".
- The download is verified against GitHub's SHA-256 before Android's installer opens. Data is kept across updates.

---

# Release Plan (small testable MVPs, D17)

Each step is a release (its own version, tag, tests and docs). The owner tests at the end (C-005); feedback can change later steps.

| Version | What can be tested |
|---|---|
| **0.1.0** | Owner phone only: set up factory, PIN login, add/edit/disable employees with rates (and rate changes), enter sarees for any employee and day, dashboard (today/week/month production and wages), weekly report, update alerts |
| **0.2.0** | Weekly payments (mark paid, paid date, adjustments), employee history, monthly and yearly reports, **export all data / import (replace)** |
| **0.3.0** | Worker phones: link by QR, worker PIN login, worker enters today's sarees, sync via ntfy, worker earnings and payments, worker export |
| **0.4.0** | CSV report export, audit log screen (Kannada cancelled, C-005) |
| **0.5.0** | ntfy push notifications, background update notification, polish from feedback |

---

# Deliverables

1. Complete solution (components + Android app)
2. Folder structure
3. Domain models
4. Database schema
5. Sync and linking (ntfy)
6. Use cases (application layer)
7. PIN login
8. MAUI Android app (English)
9. NTFY notifications
10. Background jobs
11. CSV export
12. Unit and sync tests
13. README
14. Technical documentation (docs/technical)
15. Release guide (signed APK, in-app updates via GitHub releases)

Important:

- Do not generate toy code. Production-ready code.
- SOLID principles, clean separation.
- Prefer maintainability over cleverness.
- If a requirement is unclear, make a sensible decision, record it here and in docs/CHANGES.md.

---

# Decisions

| ID | Decision | Reason |
|---|---|---|
| D1 | No server. Every phone has a local database; phones sync through ntfy.sh. | Owner's choice: no server to run or pay for. Proven in Use It Before / Travel Vault. |
| D2 | Login = tap name + 4-digit PIN. No JWT, OTP or passwords. | Owner's choice: login must be very simple. |
| D3 | Each worker uses their own phone; worker session lasts 30 days. Owner app re-asks the PIN after 5 min in background. | Owner's answer; the owner phone holds everyone's wages. |
| D4 | Offline first: save on phone, sync automatically later. | Factory may not have internet. |
| D5 | Owner phone is the hub; one private encrypted channel per worker. Worker phones hold only their own data. | Workers must not see each other's wages. |
| D6 | Rate is per employee, with rate history (effective-from date). | Owner's answer; past earnings must not change when a rate changes. |
| D7 | Pay period Sunday–Saturday, paid Saturday end of day; Sunday work goes into the following Saturday's payment. | Owner's answer. |
| D8 | ~~English + Kannada.~~ English only (C-005). | Owner cancelled Kannada. |
| D9 | Only the admin can add or change past days. | Owner's answer. |
| D10 | Admin edits win over worker edits for the same day. | Admin corrections must stick. |
| D11 | Paid weeks are frozen; later corrections become an adjustment on the next payment. | Paid money can't silently change. |
| D12 | .NET 10 MAUI Android + SQLite, same component model as the owner's other apps. | Owner's choice; replaces API/PostgreSQL/Docker/Hangfire. |
| D13 | ntfy sync code copied from Travel Vault / Use It Before and adapted, not referenced. | Owner's choice: self-contained repo. |
| D14 | Use cases as application services in a plain library (no MediatR). | Same as other apps; simpler; fully testable. |
| D15 | ~~Records via CSV export only; no full backup/restore.~~ Superseded by D18 (C-002). | – |
| D16 | The app checks GitHub releases on every open (max hourly) and announces new versions with a pop-up and a banner. | Owner: "every time there is an update the app should notify". |
| D17 | Deliver in small, installable MVP releases (0.1.0, 0.2.0, …). | Owner wants to test and give feedback continuously. |
| D18 | Full data export (plain JSON, no PINs) and import that replaces everything, with an automatic copy saved before replacing. | Owner: payments and history must be exportable and re-importable. |
| D19 | Minimum Android 6.0 (API 23); the APK contains 64-bit and 32-bit ARM code. | Owner asked whether 2016 phones work. Most 2016 phones run Android 6/7, often 32-bit. Android 5 lacks the Keystore encryption key the database needs. |
