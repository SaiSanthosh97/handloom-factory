using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Reports;

namespace HandloomFactory.Core.Workflow;

public sealed record EmployeeListItem(Employee Employee, Money? CurrentRate);

public sealed record EmployeeDetail(Employee Employee, IReadOnlyList<RateChange> RateHistory, EmployeeSummary Summary);

/// <summary>Managing workers (admin only): add, edit, rates with history, disable/enable.</summary>
public sealed class EmployeeService(WorkflowContext context)
{
    /// <summary>Active workers first, then disabled ones; each alphabetical.</summary>
    public async Task<Result<IReadOnlyList<EmployeeListItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<IReadOnlyList<EmployeeListItem>>(ErrorCode.NotAllowed);
        }

        var today = (await context.TimeAsync(cancellationToken)).Today;
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        IReadOnlyList<EmployeeListItem> list = (await context.Store.GetEmployeesAsync(cancellationToken))
            .Where(e => e.IsWorker)
            .OrderByDescending(e => e.IsActive)
            .ThenBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new EmployeeListItem(e, rates.CurrentRate(e.Id, today)))
            .ToList();
        return Result.Success(list);
    }

    public async Task<Result<EmployeeDetail>> GetAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<EmployeeDetail>(ErrorCode.NotAllowed);
        }

        if (await FindWorkerAsync(employeeId, cancellationToken) is not { } employee)
        {
            return Result.Fail<EmployeeDetail>(ErrorCode.NotFound);
        }

        var today = (await context.TimeAsync(cancellationToken)).Today;
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var entries = await context.Store.GetEntriesAsync(Span(today), employeeId, cancellationToken);
        return Result.Success(new EmployeeDetail(employee, rates.History(employeeId).Reverse().ToList(), ReportBuilder.ForEmployee(employeeId, today, rates, entries)));
    }

    /// <param name="rateFrom">The first day the rate applies (entries before it can't be saved).</param>
    public async Task<Result<Employee>> CreateAsync(string fullName, string? phoneNumber, Money rate, DateOnly rateFrom, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<Employee>(ErrorCode.NotAllowed);
        }

        if (Validate(fullName, phoneNumber, out var name, out var phone) is { } error)
        {
            return Result.Fail<Employee>(error);
        }

        if (rate.Paise <= 0)
        {
            return Result.Fail<Employee>(ErrorCode.RateInvalid);
        }

        var now = context.Now;
        var existing = await context.Store.GetEmployeesAsync(cancellationToken);
        var employee = new Employee(Ids.New(), Employee.NextCode(existing.Select(e => e.Code)), name, phone, Role.Employee, true, now, now);
        var rateChange = new RateChange(RateChange.IdFor(employee.Id, rateFrom), employee.Id, rate, rateFrom, now);

        var changes = new ChangeSet();
        changes.Employees.Add(employee);
        changes.Rates.Add(rateChange);
        changes.Audit.Add(context.Audit(AuditActions.EmployeeCreated, "Employee", employee.Id, null, new { employee.Code, employee.FullName, employee.PhoneNumber }));
        changes.Audit.Add(context.Audit(AuditActions.RateChanged, "Employee", employee.Id, null, new { Rate = rate.ToString(), From = rateFrom }));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success(employee);
    }

    public async Task<Result<Employee>> UpdateAsync(string employeeId, string fullName, string? phoneNumber, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<Employee>(ErrorCode.NotAllowed);
        }

        if (await FindWorkerAsync(employeeId, cancellationToken) is not { } employee)
        {
            return Result.Fail<Employee>(ErrorCode.NotFound);
        }

        if (Validate(fullName, phoneNumber, out var name, out var phone) is { } error)
        {
            return Result.Fail<Employee>(error);
        }

        var updated = employee with { FullName = name, PhoneNumber = phone, UpdatedAt = context.Now };
        var changes = new ChangeSet();
        changes.Employees.Add(updated);
        changes.Audit.Add(context.Audit(AuditActions.EmployeeUpdated, "Employee", employee.Id, new { employee.FullName, employee.PhoneNumber }, new { updated.FullName, updated.PhoneNumber }));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success(updated);
    }

    /// <summary>Sets a new rate from a date on. A second change on the same date replaces the first.</summary>
    public async Task<Result> ChangeRateAsync(string employeeId, Money rate, DateOnly effectiveFrom, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        if (await FindWorkerAsync(employeeId, cancellationToken) is null)
        {
            return Result.Fail(ErrorCode.NotFound);
        }

        if (rate.Paise <= 0)
        {
            return Result.Fail(ErrorCode.RateInvalid);
        }

        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var before = rates.RateOn(employeeId, effectiveFrom);
        var changes = new ChangeSet();
        changes.Rates.Add(new RateChange(RateChange.IdFor(employeeId, effectiveFrom), employeeId, rate, effectiveFrom, context.Now));
        changes.Audit.Add(context.Audit(AuditActions.RateChanged, "Employee", employeeId, before is { } b ? new { Rate = b.ToString() } : null, new { Rate = rate.ToString(), From = effectiveFrom }));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success();
    }

    /// <summary>Disable (keeps all history; they can't log in or be entered for new days by default) or enable again.</summary>
    public async Task<Result> SetActiveAsync(string employeeId, bool active, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        if (await FindWorkerAsync(employeeId, cancellationToken) is not { } employee)
        {
            return Result.Fail(ErrorCode.NotFound);
        }

        if (employee.IsActive == active)
        {
            return Result.Success();
        }

        var changes = new ChangeSet();
        changes.Employees.Add(employee with { IsActive = active, UpdatedAt = context.Now });
        changes.Audit.Add(context.Audit(active ? AuditActions.EmployeeEnabled : AuditActions.EmployeeDisabled, "Employee", employeeId, null, employee.FullName));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success();
    }

    private async Task<Employee?> FindWorkerAsync(string employeeId, CancellationToken cancellationToken) =>
        (await context.Store.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == employeeId && e.IsWorker);

    /// <summary>The days needed for "this week" and "this month" together.</summary>
    internal static DateRange Span(DateOnly today)
    {
        var week = Periods.PayWeek(today);
        var month = Periods.Month(today);
        return new DateRange(week.Start < month.Start ? week.Start : month.Start, week.End > month.End ? week.End : month.End);
    }

    private static ErrorCode? Validate(string fullName, string? phoneNumber, out string name, out string? phone)
    {
        name = fullName.Trim();
        phone = string.IsNullOrWhiteSpace(phoneNumber) ? null : new string(phoneNumber.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (name.Length is 0 or > 80)
        {
            return ErrorCode.NameRequired;
        }

        if (phone is not null && (phone.TrimStart('+').Length is < 10 or > 13 || !phone.TrimStart('+').All(char.IsAsciiDigit)))
        {
            return ErrorCode.PhoneInvalid;
        }

        return null;
    }
}
