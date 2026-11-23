using HandloomFactory.Core.Common;

namespace HandloomFactory.Core.Factory;

/// <summary>
/// A weekly wage payment that has been <b>marked paid</b> (pending weeks are computed, never stored). Marking paid
/// freezes the week's sarees and earnings, so later corrections can't silently change money already handed over (D11).
/// </summary>
/// <param name="Earnings">The week's computed earnings at the moment it was marked paid (frozen).</param>
/// <param name="Adjustment">Corrections to earlier paid weeks settled with this payment (can be negative).</param>
/// <param name="Total">What was actually paid: <see cref="Earnings"/> + <see cref="Adjustment"/>.</param>
public sealed record Payment(
    string Id,
    string EmployeeId,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int Sarees,
    Money Earnings,
    Money Adjustment,
    Money Total,
    DateOnly PaidDate,
    DateTimeOffset MarkedAt)
{
    public static string IdFor(string employeeId, DateOnly weekStart) =>
        Ids.For("payment", employeeId, weekStart.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
}

public enum PaymentStatus
{
    /// <summary>Nothing earned and nothing owed.</summary>
    Nothing = 0,
    Pending = 1,
    Paid = 2,
}

/// <summary>One employee's payment line for one pay week.</summary>
/// <param name="Earnings">Paid: the frozen earnings. Pending: computed now.</param>
/// <param name="Adjustment">Paid: what was settled. Pending: the outstanding correction to add (oldest unpaid week only).</param>
/// <param name="CorrectedSincePaid">Paid weeks only: computed earnings now minus what was frozen (non-zero after a correction).</param>
public sealed record WeekPayment(
    string EmployeeId,
    DateRange Week,
    PaymentStatus Status,
    int Sarees,
    Money Earnings,
    Money Adjustment,
    Money Total,
    DateOnly? PaidDate,
    Money CorrectedSincePaid);

/// <summary>
/// The payment rules (TD-00 §3.3). For each employee:
/// <code>outstanding adjustment = Σ over paid weeks (earnings computed now) − Σ over paid weeks (frozen earnings + adjustment)</code>
/// i.e. "owed for the paid weeks" minus "actually paid for them". It is zero unless an entry in a paid week was
/// corrected later (it can be negative: an over-payment is taken back). It is added to the oldest unpaid week.
/// </summary>
public static class PaymentCalculator
{
    /// <param name="entries">ALL of the employee's entries up to the week (paid weeks are recomputed from them).</param>
    /// <param name="payments">All of the employee's payments.</param>
    public static WeekPayment ForWeek(string employeeId, DateRange week, IReadOnlyList<ProductionEntry> entries, IReadOnlyList<Payment> payments, RateBook rates)
    {
        var own = entries.Where(e => e.EmployeeId == employeeId).ToList();
        var paid = payments.Where(p => p.EmployeeId == employeeId).ToList();
        if (paid.FirstOrDefault(p => p.WeekStart == week.Start) is { } payment)
        {
            var now = WageCalculator.SummarizeOne(employeeId, own, rates, week).Earnings;
            return new WeekPayment(employeeId, week, PaymentStatus.Paid, payment.Sarees, payment.Earnings, payment.Adjustment, payment.Total, payment.PaidDate, now - payment.Earnings);
        }

        var summary = WageCalculator.SummarizeOne(employeeId, own, rates, week);
        var adjustment = IsOldestUnpaid(week, own, paid) ? Outstanding(employeeId, own, paid, rates) : Money.Zero;
        var total = summary.Earnings + adjustment;
        var status = summary.Sarees == 0 && adjustment == Money.Zero ? PaymentStatus.Nothing : PaymentStatus.Pending;
        return new WeekPayment(employeeId, week, status, summary.Sarees, summary.Earnings, adjustment, total, null, Money.Zero);
    }

    /// <summary>Owed for the paid weeks minus what was paid for them.</summary>
    public static Money Outstanding(string employeeId, IReadOnlyList<ProductionEntry> entries, IReadOnlyList<Payment> payments, RateBook rates)
    {
        var owed = Money.Zero;
        var paid = Money.Zero;
        foreach (var payment in payments.Where(p => p.EmployeeId == employeeId))
        {
            owed += WageCalculator.SummarizeOne(employeeId, entries, rates, new DateRange(payment.WeekStart, payment.WeekEnd)).Earnings;
            paid += payment.Earnings + payment.Adjustment;
        }

        return owed - paid;
    }

    /// <summary>Freezes a pending week into a payment.</summary>
    public static Payment MarkPaid(WeekPayment pending, DateOnly paidDate, DateTimeOffset now) =>
        new(Payment.IdFor(pending.EmployeeId, pending.Week.Start), pending.EmployeeId, pending.Week.Start, pending.Week.End,
            pending.Sarees, pending.Earnings, pending.Adjustment, pending.Total, paidDate, now);

    /// <summary>The adjustment goes to the earliest week that has work or money and isn't paid yet.</summary>
    private static bool IsOldestUnpaid(DateRange week, IReadOnlyList<ProductionEntry> entries, IReadOnlyList<Payment> paid)
    {
        var paidStarts = paid.Select(p => p.WeekStart).ToHashSet();
        var earlierUnpaid = entries
            .Where(e => e.Date < week.Start && e.SareeCount > 0)
            .Select(e => Periods.PayWeek(e.Date).Start)
            .Any(start => !paidStarts.Contains(start));
        return !earlierUnpaid;
    }
}
