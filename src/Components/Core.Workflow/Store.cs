using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Security;

namespace HandloomFactory.Core.Workflow;

/// <summary>A person's PIN and wrong-PIN state on this phone. Never exported or synced.</summary>
public sealed record Credential(string EmployeeId, PinHash Pin, int FailedAttempts, DateTimeOffset? LockedUntil);

/// <summary>
/// Everything one use case writes, saved together in one transaction (unit of work): the data change and its audit
/// entry either both happen or neither does.
/// </summary>
public sealed class ChangeSet
{
    public FactoryInfo? Factory { get; set; }

    public List<Employee> Employees { get; } = [];

    public List<RateChange> Rates { get; } = [];

    public List<ProductionEntry> Entries { get; } = [];

    public List<ProductionEntry> DeletedEntries { get; } = [];

    public List<Payment> Payments { get; } = [];

    public List<Payment> DeletedPayments { get; } = [];

    /// <summary>Sync messages to send because of this change (written in the same transaction, sent later).</summary>
    public List<OutboxItem> Outbox { get; } = [];

    public List<Credential> Credentials { get; } = [];

    public List<AuditEntry> Audit { get; } = [];
}

/// <summary>Storage used by the use cases (repository). Implemented by Storage.Sqlite.</summary>
public interface IFactoryStore
{
    Task<FactoryInfo?> GetFactoryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RateChange>> GetRateChangesAsync(CancellationToken cancellationToken = default);

    /// <param name="employeeId">Only this employee's entries, or everyone's when null.</param>
    Task<IReadOnlyList<ProductionEntry>> GetEntriesAsync(DateRange range, string? employeeId = null, CancellationToken cancellationToken = default);

    /// <param name="employeeId">Only this employee's payments, or everyone's when null.</param>
    Task<IReadOnlyList<Payment>> GetPaymentsAsync(string? employeeId = null, CancellationToken cancellationToken = default);

    Task<Credential?> GetCredentialAsync(string employeeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Import (D18): replaces every factory record with <paramref name="data"/> in one transaction. PINs are kept only
    /// for people who are still in the new data; settings are kept.
    /// </summary>
    Task ReplaceAllAsync(FactoryData data, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit, CancellationToken cancellationToken = default);

    Task CommitAsync(ChangeSet changes, CancellationToken cancellationToken = default);
}

/// <summary>Who is logged in on this phone right now.</summary>
public sealed class UserSession
{
    public Employee? User { get; private set; }

    public bool IsSignedIn => User is not null;

    public bool IsAdmin => User?.Role == Role.Admin;

    public event Action? Changed;

    public void SignIn(Employee user)
    {
        User = user;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        User = null;
        Changed?.Invoke();
    }
}

/// <summary>
/// One record to send to one phone (TD-03): <paramref name="Channel"/> is the worker's employee id (each worker has
/// one private channel with the owner). <paramref name="Type"/> and <paramref name="Data"/> are the sync record.
/// </summary>
public sealed record OutboxItem(string Channel, string Type, string Data);
