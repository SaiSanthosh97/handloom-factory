using System.Text.Json;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>
/// Wraps the factory store so that every local change also queues the sync records it causes, <b>in the same
/// transaction</b> (the outbox): a change can never be saved without being queued, so nothing is lost offline.
/// Who gets what (D5):
/// <list type="bullet">
/// <item>Owner phone: a worker's entries, rates, payments and own employee record go to that worker's channel only;
/// factory details go to every linked worker. Disabling a worker queues "revoke".</item>
/// <item>Worker phone: only the worker's own entries go to the owner.</item>
/// </list>
/// Records arriving FROM sync are written to the inner store directly, so they are never echoed back.
/// </summary>
public sealed class OutboxStore(IFactoryStore inner, ISyncStore sync) : IFactoryStore
{
    /// <summary>Raised after a commit that queued something, so the app can sync soon.</summary>
    public event Action? Queued;

    public IFactoryStore Inner => inner;

    public async Task CommitAsync(ChangeSet changes, CancellationToken cancellationToken = default)
    {
        var role = await sync.GetRoleAsync(cancellationToken);
        if (role != PhoneRole.None)
        {
            var linked = (await sync.GetChannelsAsync(cancellationToken)).Where(c => c.IsLinked).Select(c => c.EmployeeId).ToHashSet();
            if (linked.Count > 0)
            {
                changes.Outbox.AddRange(role == PhoneRole.Owner ? ForWorkers(changes, linked) : FromWorker(changes, linked.First()));
            }
        }

        await inner.CommitAsync(changes, cancellationToken);
        if (changes.Outbox.Count > 0)
        {
            Queued?.Invoke();
        }
    }

    internal static IEnumerable<OutboxItem> ForWorkers(ChangeSet changes, ISet<string> linked)
    {
        if (changes.Factory is { } factory)
        {
            foreach (var channel in linked)
            {
                yield return Item(channel, RecordTypes.Factory, factory);
            }
        }

        foreach (var e in changes.Employees.Where(e => linked.Contains(e.Id)))
        {
            yield return Item(e.Id, RecordTypes.Employee, e);
            if (!e.IsActive)
            {
                yield return new OutboxItem(e.Id, RecordTypes.Revoke, "{}");
            }
        }

        foreach (var r in changes.Rates.Where(r => linked.Contains(r.EmployeeId)))
        {
            yield return Item(r.EmployeeId, RecordTypes.Rate, r);
        }

        foreach (var e in changes.Entries.Where(e => linked.Contains(e.EmployeeId)))
        {
            yield return Item(e.EmployeeId, RecordTypes.Entry, e);
        }

        foreach (var e in changes.DeletedEntries.Where(e => linked.Contains(e.EmployeeId)))
        {
            yield return Item(e.EmployeeId, RecordTypes.EntryDeleted, e);
        }

        foreach (var p in changes.Payments.Where(p => linked.Contains(p.EmployeeId)))
        {
            yield return Item(p.EmployeeId, RecordTypes.Payment, p);
        }

        foreach (var p in changes.DeletedPayments.Where(p => linked.Contains(p.EmployeeId)))
        {
            yield return Item(p.EmployeeId, RecordTypes.PaymentDeleted, p);
        }
    }

    internal static IEnumerable<OutboxItem> FromWorker(ChangeSet changes, string self) =>
        changes.Entries.Where(e => e.EmployeeId == self && e.EnteredBy == EnteredBy.Employee).Select(e => Item(self, RecordTypes.Entry, e));

    internal static OutboxItem Item<T>(string channel, string type, T record) =>
        new(channel, type, JsonSerializer.Serialize(record, FactoryJson.Compact));

    public Task<FactoryInfo?> GetFactoryAsync(CancellationToken cancellationToken = default) => inner.GetFactoryAsync(cancellationToken);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken = default) => inner.GetEmployeesAsync(cancellationToken);

    public Task<IReadOnlyList<RateChange>> GetRateChangesAsync(CancellationToken cancellationToken = default) => inner.GetRateChangesAsync(cancellationToken);

    public Task<IReadOnlyList<ProductionEntry>> GetEntriesAsync(DateRange range, string? employeeId = null, CancellationToken cancellationToken = default) =>
        inner.GetEntriesAsync(range, employeeId, cancellationToken);

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(string? employeeId = null, CancellationToken cancellationToken = default) => inner.GetPaymentsAsync(employeeId, cancellationToken);

    public Task<Credential?> GetCredentialAsync(string employeeId, CancellationToken cancellationToken = default) => inner.GetCredentialAsync(employeeId, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit, CancellationToken cancellationToken = default) => inner.GetAuditAsync(limit, cancellationToken);

    public Task ReplaceAllAsync(FactoryData data, CancellationToken cancellationToken = default) => inner.ReplaceAllAsync(data, cancellationToken);
}
