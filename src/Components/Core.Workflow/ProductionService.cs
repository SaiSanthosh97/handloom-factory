using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Reports;

namespace HandloomFactory.Core.Workflow;

/// <summary>One line on the "sarees for a day" screen.</summary>
/// <param name="WeekPaid">The week is already marked paid for this worker: a change becomes an adjustment on the next payment (D11).</param>
public sealed record DaySheetRow(Employee Employee, int? SareeCount, Money? Rate, EnteredBy? EnteredBy, bool WeekPaid = false);

public sealed record DaySheet(DateOnly Date, DateOnly Today, int MaxPerDay, IReadOnlyList<DaySheetRow> Rows)
{
    public bool IsToday => Date == Today;

    public int Total => Rows.Sum(r => r.SareeCount ?? 0);
}

/// <summary>Production entries (§ Saree Entry Rules): the admin enters any day; a worker enters only today.</summary>
public sealed class ProductionService(WorkflowContext context)
{
    /// <summary>All active workers (and disabled ones who have an entry that day) with that day's counts.</summary>
    public async Task<Result<DaySheet>> GetDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<DaySheet>(ErrorCode.NotAllowed);
        }

        var factory = await context.FactoryAsync(cancellationToken);
        var today = (await context.TimeAsync(cancellationToken)).Today;
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var entries = (await context.Store.GetEntriesAsync(new DateRange(date, date), null, cancellationToken)).ToDictionary(e => e.EmployeeId);
        var weekStart = Periods.PayWeek(date).Start;
        var paid = (await context.Store.GetPaymentsAsync(null, cancellationToken)).Where(p => p.WeekStart == weekStart).Select(p => p.EmployeeId).ToHashSet();
        var rows = (await context.Store.GetEmployeesAsync(cancellationToken))
            .Where(e => e.IsWorker && (e.IsActive || entries.ContainsKey(e.Id)))
            .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => entries.TryGetValue(e.Id, out var entry)
                ? new DaySheetRow(e, entry.SareeCount, rates.RateOn(e.Id, date), entry.EnteredBy, paid.Contains(e.Id))
                : new DaySheetRow(e, null, rates.RateOn(e.Id, date), null, paid.Contains(e.Id)))
            .ToList();
        return Result.Success(new DaySheet(date, today, factory?.MaxSareesPerDay ?? FactoryInfo.DefaultMaxSareesPerDay, rows));
    }

    /// <summary>
    /// Admin saves a day for several workers at once. A null count removes that worker's entry for the day.
    /// All rows are checked first; nothing is saved if any row is wrong (the error names the employee).
    /// </summary>
    /// <returns>The number of entries that changed.</returns>
    public async Task<Result<int>> SaveDayAsync(DateOnly date, IReadOnlyDictionary<string, int?> counts, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<int>(ErrorCode.NotAllowed);
        }

        var factory = await context.FactoryAsync(cancellationToken);
        var maxPerDay = factory?.MaxSareesPerDay ?? FactoryInfo.DefaultMaxSareesPerDay;
        var today = (await context.TimeAsync(cancellationToken)).Today;
        var employees = (await context.Store.GetEmployeesAsync(cancellationToken)).Where(e => e.IsWorker).ToDictionary(e => e.Id);
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var existing = (await context.Store.GetEntriesAsync(new DateRange(date, date), null, cancellationToken)).ToDictionary(e => e.EmployeeId);

        var now = context.Now;
        var changes = new ChangeSet();
        foreach (var (employeeId, count) in counts)
        {
            if (!employees.TryGetValue(employeeId, out var employee))
            {
                return Result.Fail<int>(ErrorCode.NotFound, employeeId);
            }

            existing.TryGetValue(employeeId, out var old);
            if (count is null)
            {
                if (old is not null)
                {
                    changes.DeletedEntries.Add(old);
                    changes.Audit.Add(context.Audit(AuditActions.EntryDeleted, "Entry", old.Id, Describe(employee, old.Date, old.SareeCount), null));
                }

                continue;
            }

            if (old?.SareeCount == count && old.EnteredBy == EnteredBy.Admin)
            {
                continue;
            }

            var rule = EntryRules.Check(Role.Admin, date, count.Value, today, maxPerDay, rates.RateOn(employeeId, date));
            if (rule != ErrorCode.None)
            {
                return Result.Fail<int>(rule, employee.FullName);
            }

            if (old?.SareeCount == count)
            {
                continue; // same number the worker typed: leave it as theirs
            }

            var entry = new ProductionEntry(ProductionEntry.IdFor(employeeId, date), employeeId, date, count.Value, EnteredBy.Admin, old?.CreatedAt ?? now, now);
            changes.Entries.Add(entry);
            changes.Audit.Add(context.Audit(AuditActions.EntrySaved, "Entry", entry.Id, old is null ? null : Describe(employee, date, old.SareeCount), Describe(employee, date, count.Value)));
        }

        if (changes.Entries.Count + changes.DeletedEntries.Count > 0)
        {
            await context.Store.CommitAsync(changes, cancellationToken);
        }

        return Result.Success(changes.Entries.Count + changes.DeletedEntries.Count);
    }

    /// <summary>A worker saves (or changes) their own count for today. Refused when the admin already set today's value (D10).</summary>
    public async Task<Result<ProductionEntry>> SaveOwnTodayAsync(int count, CancellationToken cancellationToken = default)
    {
        if (context.Session.User is not { IsWorker: true } worker)
        {
            return Result.Fail<ProductionEntry>(ErrorCode.NotAllowed);
        }

        var factory = await context.FactoryAsync(cancellationToken);
        var today = (await context.TimeAsync(cancellationToken)).Today;
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var rule = EntryRules.Check(Role.Employee, today, count, today, factory?.MaxSareesPerDay ?? FactoryInfo.DefaultMaxSareesPerDay, rates.RateOn(worker.Id, today));
        if (rule != ErrorCode.None)
        {
            return Result.Fail<ProductionEntry>(rule);
        }

        var old = (await context.Store.GetEntriesAsync(new DateRange(today, today), worker.Id, cancellationToken)).FirstOrDefault();
        if (old?.EnteredBy == EnteredBy.Admin)
        {
            return Result.Fail<ProductionEntry>(ErrorCode.NotAllowed);
        }

        var now = context.Now;
        var entry = new ProductionEntry(ProductionEntry.IdFor(worker.Id, today), worker.Id, today, count, EnteredBy.Employee, old?.CreatedAt ?? now, now);
        var changes = new ChangeSet();
        changes.Entries.Add(entry);
        changes.Audit.Add(context.Audit(AuditActions.EntrySaved, "Entry", entry.Id, old is null ? null : Describe(worker, today, old.SareeCount), Describe(worker, today, count)));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success(entry);
    }

    private static string Describe(Employee employee, DateOnly date, int count) => $"{employee.FullName} {date:yyyy-MM-dd}: {count}";
}

/// <summary>Dashboard and reports (admin).</summary>
public sealed class ReportService(WorkflowContext context)
{
    public async Task<Result<AdminDashboard>> DashboardAsync(CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<AdminDashboard>(ErrorCode.NotAllowed);
        }

        var today = (await context.TimeAsync(cancellationToken)).Today;
        var entries = await context.Store.GetEntriesAsync(EmployeeService.Span(today), null, cancellationToken);
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        return Result.Success(ReportBuilder.Dashboard(today, await context.Store.GetEmployeesAsync(cancellationToken), rates, entries));
    }

    /// <summary>The Sunday–Saturday week that contains <paramref name="anyDayInWeek"/>.</summary>
    public Task<Result<PeriodReport>> WeeklyAsync(DateOnly anyDayInWeek, CancellationToken cancellationToken = default) =>
        PeriodAsync(Periods.PayWeek(anyDayInWeek), cancellationToken);

    public Task<Result<PeriodReport>> MonthlyAsync(DateOnly anyDayInMonth, CancellationToken cancellationToken = default) =>
        PeriodAsync(Periods.Month(anyDayInMonth), cancellationToken);

    public Task<Result<PeriodReport>> YearlyAsync(int year, CancellationToken cancellationToken = default) =>
        PeriodAsync(Periods.Year(year), cancellationToken);

    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default) => (await context.TimeAsync(cancellationToken)).Today;

    private async Task<Result<PeriodReport>> PeriodAsync(DateRange period, CancellationToken cancellationToken)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<PeriodReport>(ErrorCode.NotAllowed);
        }

        var entries = await context.Store.GetEntriesAsync(period, null, cancellationToken);
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        return Result.Success(ReportBuilder.Period(period, await context.Store.GetEmployeesAsync(cancellationToken), rates, entries));
    }
}
