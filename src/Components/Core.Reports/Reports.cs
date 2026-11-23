using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Reports;

/// <summary>The owner's dashboard (§ Dashboard Requirements).</summary>
public sealed record AdminDashboard(
    DateOnly Today,
    DateRange Week,
    DateRange Month,
    int ActiveEmployees,
    int TodaySarees,
    IReadOnlyList<string> NotEnteredToday,
    int WeekSarees,
    Money WeekWages,
    int MonthSarees,
    Money MonthWages);

/// <summary>One employee's row in a period report.</summary>
public sealed record ReportRow(
    string EmployeeId,
    string Code,
    string Name,
    bool IsActive,
    IReadOnlyList<int?> DailyCounts,
    int Sarees,
    Money? Rate,
    bool RateVaries,
    Money Earnings);

/// <summary>A weekly (Sunday–Saturday), monthly or yearly report.</summary>
public sealed record PeriodReport(DateRange Period, IReadOnlyList<DateOnly> Days, IReadOnlyList<ReportRow> Rows, int TotalSarees, Money TotalEarnings);

/// <summary>One employee's own figures (worker dashboard and the owner's employee screen).</summary>
public sealed record EmployeeSummary(
    DateOnly Today,
    int? TodaySarees,
    DateRange Week,
    EarningsSummary ThisWeek,
    DateRange Month,
    EarningsSummary ThisMonth,
    Money? CurrentRate);

/// <summary>
/// Report building: pure functions over employees, rate history and entries, so the owner's phone and a worker's
/// phone always compute the same numbers from the same data.
/// </summary>
public static class ReportBuilder
{
    /// <param name="entries">Entries for at least the current month and pay week (more is fine).</param>
    public static AdminDashboard Dashboard(DateOnly today, IReadOnlyList<Employee> employees, RateBook rates, IReadOnlyList<ProductionEntry> entries)
    {
        var week = Periods.PayWeek(today);
        var month = Periods.Month(today);
        var active = employees.Where(e => e.IsWorker && e.IsActive).OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var todays = entries.Where(e => e.Date == today).ToDictionary(e => e.EmployeeId);
        var weekSummary = WageCalculator.Summarize(entries, rates, week).Values;
        var monthSummary = WageCalculator.Summarize(entries, rates, month).Values;

        return new AdminDashboard(
            today,
            week,
            month,
            active.Count,
            todays.Values.Sum(e => e.SareeCount),
            active.Where(e => !todays.ContainsKey(e.Id)).Select(e => e.FullName).ToList(),
            weekSummary.Sum(s => s.Sarees),
            weekSummary.Aggregate(Money.Zero, (sum, s) => sum + s.Earnings),
            monthSummary.Sum(s => s.Sarees),
            monthSummary.Aggregate(Money.Zero, (sum, s) => sum + s.Earnings));
    }

    /// <summary>
    /// Report for any period. Rows: every active worker, plus disabled workers who have entries in the period.
    /// <see cref="ReportRow.DailyCounts"/> has one item per day only for periods of up to 7 days (weekly report).
    /// </summary>
    public static PeriodReport Period(DateRange period, IReadOnlyList<Employee> employees, RateBook rates, IReadOnlyList<ProductionEntry> entries)
    {
        var inPeriod = entries.Where(e => period.Contains(e.Date)).ToList();
        var summaries = WageCalculator.Summarize(inPeriod, rates, period);
        var days = period.DayCount <= 7 ? period.Days().ToList() : [];
        var byEmployeeDay = inPeriod.ToDictionary(e => (e.EmployeeId, e.Date), e => e.SareeCount);

        var rows = employees
            .Where(e => e.IsWorker && (e.IsActive || summaries.ContainsKey(e.Id)))
            .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e =>
            {
                var summary = summaries.TryGetValue(e.Id, out var s) ? s : new EarningsSummary(e.Id, 0, Money.Zero, rates.RatesIn(e.Id, period));
                var daily = days.Select(d => byEmployeeDay.TryGetValue((e.Id, d), out var count) ? count : (int?)null).ToList();
                return new ReportRow(e.Id, e.Code, e.FullName, e.IsActive, daily, summary.Sarees, summary.SingleRate, summary.RateVaries, summary.Earnings);
            })
            .ToList();

        return new PeriodReport(period, days, rows, rows.Sum(r => r.Sarees), rows.Aggregate(Money.Zero, (sum, r) => sum + r.Earnings));
    }

    public static EmployeeSummary ForEmployee(string employeeId, DateOnly today, RateBook rates, IReadOnlyList<ProductionEntry> entries)
    {
        var own = entries.Where(e => e.EmployeeId == employeeId).ToList();
        var week = Periods.PayWeek(today);
        var month = Periods.Month(today);
        return new EmployeeSummary(
            today,
            own.FirstOrDefault(e => e.Date == today)?.SareeCount,
            week,
            WageCalculator.SummarizeOne(employeeId, own, rates, week),
            month,
            WageCalculator.SummarizeOne(employeeId, own, rates, month),
            rates.CurrentRate(employeeId, today));
    }
}
