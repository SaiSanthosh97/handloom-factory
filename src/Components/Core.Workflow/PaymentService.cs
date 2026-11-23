using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow;

public sealed record PaymentRow(Employee Employee, WeekPayment Payment);

public sealed record WeekPayments(DateRange Week, DateOnly Today, IReadOnlyList<PaymentRow> Rows)
{
    public Money PendingTotal => Rows.Where(r => r.Payment.Status == PaymentStatus.Pending).Aggregate(Money.Zero, (sum, r) => sum + r.Payment.Total);

    public Money PaidTotal => Rows.Where(r => r.Payment.Status == PaymentStatus.Paid).Aggregate(Money.Zero, (sum, r) => sum + r.Payment.Total);
}

/// <summary>Wages due on or before today that haven't been marked paid (for the dashboard).</summary>
public sealed record DuePayments(Money Total, int Count, DateOnly? OldestWeekStart);

/// <summary>
/// Weekly payments (§ Payment Tracking, D7, D11): see what each worker is owed for a pay week, mark it paid with a
/// date (freezing it), mark the whole week paid, or undo a mistake.
/// </summary>
public sealed class PaymentService(WorkflowContext context)
{
    private static readonly DateRange AllTime = new(DateOnly.MinValue, DateOnly.MaxValue);

    /// <summary>Every worker's line for the Sunday–Saturday week containing <paramref name="anyDay"/>.</summary>
    public async Task<Result<WeekPayments>> WeekAsync(DateOnly anyDay, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<WeekPayments>(ErrorCode.NotAllowed);
        }

        var week = Periods.PayWeek(anyDay);
        var data = await LoadAsync(null, cancellationToken);
        var rows = data.Employees
            .Where(e => e.IsWorker)
            .Select(e => new PaymentRow(e, PaymentCalculator.ForWeek(e.Id, week, data.Entries, data.Payments, data.Rates)))
            .Where(r => r.Employee.IsActive || r.Payment.Status != PaymentStatus.Nothing)
            .OrderBy(r => r.Employee.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Result.Success(new WeekPayments(week, data.Today, rows));
    }

    public async Task<Result<Payment>> MarkPaidAsync(string employeeId, DateOnly weekStart, DateOnly paidDate, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<Payment>(ErrorCode.NotAllowed);
        }

        var data = await LoadAsync(employeeId, cancellationToken);
        if (data.Employees.FirstOrDefault(e => e.Id == employeeId && e.IsWorker) is not { } employee)
        {
            return Result.Fail<Payment>(ErrorCode.NotFound);
        }

        var week = Periods.PayWeek(weekStart);
        if (paidDate > data.Today || paidDate < week.Start)
        {
            return Result.Fail<Payment>(ErrorCode.PaidDateInvalid);
        }

        var line = PaymentCalculator.ForWeek(employeeId, week, data.Entries, data.Payments, data.Rates);
        if (line.Status == PaymentStatus.Paid)
        {
            return Result.Fail<Payment>(ErrorCode.AlreadyPaid, employee.FullName);
        }

        if (line.Status == PaymentStatus.Nothing)
        {
            return Result.Fail<Payment>(ErrorCode.NothingToPay, employee.FullName);
        }

        var payment = PaymentCalculator.MarkPaid(line, paidDate, context.Now);
        var changes = new ChangeSet();
        changes.Payments.Add(payment);
        changes.Audit.Add(context.Audit(AuditActions.PaymentMarked, "Payment", payment.Id, null,
            $"{employee.FullName} {week.Start:yyyy-MM-dd}..{week.End:yyyy-MM-dd}: {payment.Total} paid {paidDate:yyyy-MM-dd}"));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success(payment);
    }

    /// <summary>Marks every pending line of the week paid (one transaction).</summary>
    /// <returns>How many were marked.</returns>
    public async Task<Result<int>> MarkWeekPaidAsync(DateOnly weekStart, DateOnly paidDate, CancellationToken cancellationToken = default)
    {
        var week = await WeekAsync(weekStart, cancellationToken);
        if (!week.Ok)
        {
            return Result.Fail<int>(week.Error!.Code);
        }

        if (paidDate > week.Value!.Today || paidDate < week.Value.Week.Start)
        {
            return Result.Fail<int>(ErrorCode.PaidDateInvalid);
        }

        var changes = new ChangeSet();
        foreach (var row in week.Value.Rows.Where(r => r.Payment.Status == PaymentStatus.Pending))
        {
            var payment = PaymentCalculator.MarkPaid(row.Payment, paidDate, context.Now);
            changes.Payments.Add(payment);
            changes.Audit.Add(context.Audit(AuditActions.PaymentMarked, "Payment", payment.Id, null,
                $"{row.Employee.FullName} {payment.WeekStart:yyyy-MM-dd}..{payment.WeekEnd:yyyy-MM-dd}: {payment.Total} paid {paidDate:yyyy-MM-dd}"));
        }

        if (changes.Payments.Count == 0)
        {
            return Result.Fail<int>(ErrorCode.NothingToPay);
        }

        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success(changes.Payments.Count);
    }

    /// <summary>Undo "paid" (a mistake). The week becomes pending again and is recomputed.</summary>
    public async Task<Result> UnmarkAsync(string employeeId, DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        var start = Periods.PayWeek(weekStart).Start;
        var payment = (await context.Store.GetPaymentsAsync(employeeId, cancellationToken)).FirstOrDefault(p => p.WeekStart == start);
        if (payment is null)
        {
            return Result.Fail(ErrorCode.NotPaid);
        }

        var name = (await context.Store.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == employeeId)?.FullName ?? employeeId;
        var changes = new ChangeSet();
        changes.DeletedPaymentIds.Add(payment.Id);
        changes.Audit.Add(context.Audit(AuditActions.PaymentUnmarked, "Payment", payment.Id,
            $"{name} {payment.WeekStart:yyyy-MM-dd}..{payment.WeekEnd:yyyy-MM-dd}: {payment.Total} paid {payment.PaidDate:yyyy-MM-dd}", null));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success();
    }

    /// <summary>Weeks whose payday has come (Saturday on or before today) and that aren't paid.</summary>
    public async Task<Result<DuePayments>> DueAsync(CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<DuePayments>(ErrorCode.NotAllowed);
        }

        var data = await LoadAsync(null, cancellationToken);
        var total = Money.Zero;
        var count = 0;
        DateOnly? oldest = null;
        var weeks = data.Entries.Select(e => Periods.PayWeek(e.Date)).Where(w => w.End <= data.Today).Distinct().OrderBy(w => w.Start).ToList();
        foreach (var employee in data.Employees.Where(e => e.IsWorker))
        {
            foreach (var week in weeks)
            {
                var line = PaymentCalculator.ForWeek(employee.Id, week, data.Entries, data.Payments, data.Rates);
                if (line.Status == PaymentStatus.Pending)
                {
                    total += line.Total;
                    count++;
                    oldest = oldest is { } o && o <= week.Start ? o : week.Start;
                }
            }
        }

        return Result.Success(new DuePayments(total, count, oldest));
    }

    /// <summary>One worker's recent weeks, newest first (worker screen history).</summary>
    public async Task<Result<IReadOnlyList<WeekPayment>>> HistoryAsync(string employeeId, int weeks = 12, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin && context.Session.User?.Id != employeeId)
        {
            return Result.Fail<IReadOnlyList<WeekPayment>>(ErrorCode.NotAllowed);
        }

        var data = await LoadAsync(employeeId, cancellationToken);
        var list = new List<WeekPayment>();
        var week = Periods.PayWeek(data.Today);
        for (var i = 0; i < weeks; i++)
        {
            list.Add(PaymentCalculator.ForWeek(employeeId, week, data.Entries, data.Payments, data.Rates));
            week = Periods.PayWeek(week.Start.AddDays(-1));
        }

        return Result.Success<IReadOnlyList<WeekPayment>>(list);
    }

    private async Task<(DateOnly Today, IReadOnlyList<Employee> Employees, IReadOnlyList<ProductionEntry> Entries, IReadOnlyList<Payment> Payments, RateBook Rates)> LoadAsync(string? employeeId, CancellationToken cancellationToken) =>
        ((await context.TimeAsync(cancellationToken)).Today,
         await context.Store.GetEmployeesAsync(cancellationToken),
         await context.Store.GetEntriesAsync(AllTime, employeeId, cancellationToken),
         await context.Store.GetPaymentsAsync(employeeId, cancellationToken),
         new RateBook(await context.Store.GetRateChangesAsync(cancellationToken)));
}
