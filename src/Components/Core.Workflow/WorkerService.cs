using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Reports;

namespace HandloomFactory.Core.Workflow;

/// <summary>The worker's own dashboard (§ Dashboard Requirements, Employee).</summary>
/// <param name="TodayLocked">The owner already set today's number: the worker can't change it (D10).</param>
/// <param name="Weeks">Recent pay weeks, newest first, with payment status.</param>
public sealed record WorkerDashboard(Employee Worker, string FactoryName, EmployeeSummary Summary, bool TodayLocked, int MaxPerDay, IReadOnlyList<WeekPayment> Weeks);

/// <summary>What a worker sees on their own phone: only their own data (D5).</summary>
public sealed class WorkerService(WorkflowContext context, PaymentService payments)
{
    public async Task<Result<WorkerDashboard>> DashboardAsync(CancellationToken cancellationToken = default)
    {
        if (context.Session.User is not { IsWorker: true } worker)
        {
            return Result.Fail<WorkerDashboard>(ErrorCode.NotAllowed);
        }

        var factory = await context.FactoryAsync(cancellationToken);
        var today = (await context.TimeAsync(cancellationToken)).Today;
        var entries = await context.Store.GetEntriesAsync(EmployeeService.Span(today), worker.Id, cancellationToken);
        var rates = new RateBook(await context.Store.GetRateChangesAsync(cancellationToken));
        var summary = ReportBuilder.ForEmployee(worker.Id, today, rates, entries);
        var locked = entries.FirstOrDefault(e => e.Date == today)?.EnteredBy == EnteredBy.Admin;
        var weeks = (await payments.HistoryAsync(worker.Id, 12, cancellationToken)).Value ?? [];
        return Result.Success(new WorkerDashboard(worker, factory?.Name ?? "", summary, locked, factory?.MaxSareesPerDay ?? FactoryInfo.DefaultMaxSareesPerDay, weeks));
    }
}
