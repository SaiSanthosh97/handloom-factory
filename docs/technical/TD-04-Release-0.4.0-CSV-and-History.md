# TD-04 · Release 0.4.0: CSV Reports and Change History

**Status:** Implemented (2026-11-23, C-008). Kannada was cancelled for this release (C-005).

## Summary
- **CSV reports.** Every report (week, month, year) can be exported from the Reports tab as a CSV file that opens correctly in Excel, and shared through WhatsApp, email or Drive. Weekly files also carry each worker's payment status, paid date and amount.
- **Change history.** Settings → Change history lists who changed what and when, newest first: workers, rates, saree entries (including those from workers' phones), payments, PIN changes, exports, imports and phone links. It can be exported as CSV too.

## Where the code is
| File | Purpose |
|---|---|
| `Core.Reports/ReportCsv.cs` | `Write` (period report, optional payment columns, translated headings), `WriteLog` (change history), `Number`, `Escape` |
| `Core.Workflow/AuditService.cs` | The change history with people's names (owner only) |
| App: `ReportViewModel.ExportCsvCommand`, `AuditViewModel`/`AuditPage`, `ShareFiles` | Export and share; history screen with friendly action names |

## How it works

### The CSV file
- **UTF-8 with a byte-order mark:** without it, Excel on Windows guesses the wrong encoding and garbles names in Indian scripts and the "₹"-free headings.
- **Comma-separated, CRLF line ends**, cells quoted when they contain commas, quotes or line breaks (RFC 4180).
- **Amounts as plain numbers in rupees** (`1351.5`, `21000`), with no "₹" and no grouping commas, so Excel treats them as numbers that can be summed. The headings say "(Rs)".
- **Layout:**
  - Rows 1–2: a title and the date range.
  - A header row, then one row per worker.
  - The week view has a column per day (Sun–Sat). Month and year views have only totals.
  - A **Total** row: per day, sarees and earnings.
- **CSV injection is neutralised:** a cell starting with `=`, `+`, `-` or `@` that isn't a number gets a leading `'`, so a worker named "=SUM(…)" can't run as a formula when the owner opens the file (tested).

### Change history
`AuditService.RecentAsync` returns the latest 500 entries, newest first, with the actor's name. The screen maps each action code to friendly text ("Marked paid", "Sarees saved", "Worker's phone linked"…). Stored values are small JSON objects, shown as "key: value" text with "before → after". Entries saved from a worker's phone are marked "(from worker's phone)" (TD-03).

## Key decisions
| Decision | Why | Alternatives rejected |
|---|---|---|
| CSV, not .xlsx | Opens in Excel, Google Sheets and LibreOffice with no extra library on the phone | ClosedXML/xlsx (several MB, slow on old phones) |
| Plain rupee numbers | Excel can add them up | "₹1,23,456" text (Excel sees text, not numbers) |
| BOM + UTF-8 | Excel shows every script correctly | Plain UTF-8 (garbled in Excel on Windows) |
| Escape formula-like cells | Safe to open files containing names typed by anyone | Trust the data |

## Tests (5 new)
`Core.Factory.Tests/CsvTests.cs`:
- the weekly file has a BOM, title, range, day columns, payment columns and a totals row with exact values;
- a name with a comma is quoted and a formula-like name is neutralised;
- escaping rules;
- plain numbers.

`Core.Workflow.Tests` (`AuditTests`): newest first, the actor's name, details, and owner only.
