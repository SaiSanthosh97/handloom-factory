using HandloomFactory.Core.Common;

namespace HandloomFactory.Core.Factory;

/// <summary>
/// Every employee's rate history, answering "what did one saree pay on this day?" (D6). A rate change applies
/// from its date on; earlier days keep the earlier rate, so changing a rate never changes past earnings.
/// </summary>
public sealed class RateBook
{
    private readonly Dictionary<string, RateChange[]> _byEmployee;

    public RateBook(IEnumerable<RateChange> changes) =>
        _byEmployee = changes
            .GroupBy(c => c.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.EffectiveFrom).ToArray());

    /// <returns>The rate in effect on the day, or null when the employee had no rate yet.</returns>
    public Money? RateOn(string employeeId, DateOnly day)
    {
        if (!_byEmployee.TryGetValue(employeeId, out var changes))
        {
            return null;
        }

        Money? rate = null;
        foreach (var change in changes)
        {
            if (change.EffectiveFrom > day)
            {
                break;
            }

            rate = change.Rate;
        }

        return rate;
    }

    /// <summary>The rate today or the latest one set (including one that starts in the future).</summary>
    public Money? CurrentRate(string employeeId, DateOnly today) =>
        RateOn(employeeId, today) ?? (_byEmployee.TryGetValue(employeeId, out var c) && c.Length > 0 ? c[0].Rate : null);

    public IReadOnlyList<RateChange> History(string employeeId) =>
        _byEmployee.TryGetValue(employeeId, out var changes) ? changes : [];

    /// <summary>The distinct rates that applied on the days in the range (for a report's "Rate" column).</summary>
    public IReadOnlyList<Money> RatesIn(string employeeId, DateRange range)
    {
        var rates = new List<Money>();
        if (RateOn(employeeId, range.Start) is { } first)
        {
            rates.Add(first);
        }

        foreach (var change in History(employeeId))
        {
            if (change.EffectiveFrom > range.Start && change.EffectiveFrom <= range.End && !rates.Contains(change.Rate))
            {
                rates.Add(change.Rate);
            }
        }

        return rates;
    }
}

/// <summary>Sarees and earnings for one employee over a period.</summary>
public sealed record EarningsSummary(string EmployeeId, int Sarees, Money Earnings, IReadOnlyList<Money> Rates)
{
    /// <summary>The single rate for the period, or null when it changed during the period ("varies") or none applied.</summary>
    public Money? SingleRate => Rates.Count == 1 ? Rates[0] : null;

    public bool RateVaries => Rates.Count > 1;
}

/// <summary>
/// The wage rule: earnings for a day = sarees that day × the employee's rate on that day. Everything is computed,
/// never typed (no manual calculations).
/// </summary>
public static class WageCalculator
{
    public static Money EarningsFor(ProductionEntry entry, RateBook rates) =>
        (rates.RateOn(entry.EmployeeId, entry.Date) ?? Money.Zero) * entry.SareeCount;

    /// <summary>One summary per employee that has entries in the range.</summary>
    public static IReadOnlyDictionary<string, EarningsSummary> Summarize(IEnumerable<ProductionEntry> entries, RateBook rates, DateRange range)
    {
        var result = new Dictionary<string, EarningsSummary>();
        foreach (var group in entries.Where(e => range.Contains(e.Date)).GroupBy(e => e.EmployeeId))
        {
            var sarees = 0;
            var earnings = Money.Zero;
            foreach (var entry in group)
            {
                sarees += entry.SareeCount;
                earnings += EarningsFor(entry, rates);
            }

            result[group.Key] = new EarningsSummary(group.Key, sarees, earnings, rates.RatesIn(group.Key, range));
        }

        return result;
    }

    public static EarningsSummary SummarizeOne(string employeeId, IEnumerable<ProductionEntry> entries, RateBook rates, DateRange range) =>
        Summarize(entries.Where(e => e.EmployeeId == employeeId), rates, range).TryGetValue(employeeId, out var summary)
            ? summary
            : new EarningsSummary(employeeId, 0, Money.Zero, rates.RatesIn(employeeId, range));
}

/// <summary>Who may enter which day (§ Saree Entry Rules).</summary>
public static class EntryRules
{
    /// <returns><see cref="ErrorCode.None"/> when allowed.</returns>
    public static ErrorCode Check(Role actor, DateOnly date, int count, DateOnly today, int maxPerDay, Money? rateOnDate)
    {
        if (date > today)
        {
            return ErrorCode.FutureDate;
        }

        if (actor == Role.Employee && date != today)
        {
            return ErrorCode.NotAllowed; // D9: only the admin adds or changes past days
        }

        if (count < 0 || count > maxPerDay)
        {
            return ErrorCode.CountOutOfRange;
        }

        return rateOnDate is null ? ErrorCode.NoRateOnDate : ErrorCode.None;
    }
}
