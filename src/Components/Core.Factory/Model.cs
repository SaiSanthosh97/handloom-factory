using HandloomFactory.Core.Common;

namespace HandloomFactory.Core.Factory;

public enum Role
{
    Employee = 0,
    Admin = 1,
}

/// <summary>Who typed an entry. Admin corrections win over the worker's own edits (D10).</summary>
public enum EnteredBy
{
    Employee = 0,
    Admin = 1,
}

/// <summary>The factory this phone manages. One per owner phone.</summary>
public sealed record FactoryInfo(
    string Id,
    string Name,
    string OwnerName,
    string TimeZoneId,
    int MaxSareesPerDay,
    DateTimeOffset CreatedAt)
{
    public const int DefaultMaxSareesPerDay = 50;
}

/// <summary>
/// A person who can log in. The owner is an employee with <see cref="Role.Admin"/>; workers have
/// <see cref="Role.Employee"/>. Employees are disabled, never deleted, so their history stays.
/// </summary>
/// <param name="Weaves">The owner also weaves (0.6.0): they get a rate and are counted and paid like a worker, but keep
/// the owner's login and rights. Always true in effect for workers.</param>
public sealed record Employee(
    string Id,
    string Code,
    string FullName,
    string? PhoneNumber,
    Role Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Weaves = false)
{
    /// <summary>Makes sarees: counted in day entry, reports and payments (workers, and the owner when they weave).</summary>
    public bool IsWorker => Role == Role.Employee || Weaves;

    public bool IsOwner => Role == Role.Admin;

    /// <summary>The next code after the existing ones: E001, E002, …</summary>
    public static string NextCode(IEnumerable<string> existingCodes)
    {
        var max = 0;
        foreach (var code in existingCodes)
        {
            if (code.Length > 1 && code[0] == 'E' && int.TryParse(code.AsSpan(1), out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return $"E{max + 1:000}";
    }
}

/// <summary>
/// One step in an employee's rate history: from <see cref="EffectiveFrom"/> (inclusive) until the next change, each
/// saree pays <see cref="Rate"/>. One change per employee per day: the id is derived from both (D6).
/// </summary>
public sealed record RateChange(
    string Id,
    string EmployeeId,
    Money Rate,
    DateOnly EffectiveFrom,
    DateTimeOffset CreatedAt)
{
    public static string IdFor(string employeeId, DateOnly effectiveFrom) =>
        Ids.For("rate", employeeId, effectiveFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// Sarees one employee produced on one day. The id is derived from employee + date, so there is exactly one entry
/// per employee per day on every phone, without a server.
/// </summary>
public sealed record ProductionEntry(
    string Id,
    string EmployeeId,
    DateOnly Date,
    int SareeCount,
    EnteredBy EnteredBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static string IdFor(string employeeId, DateOnly date) =>
        Ids.For("entry", employeeId, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>An append-only record of who changed what, shown to the owner.</summary>
public sealed record AuditEntry(
    string Id,
    DateTimeOffset At,
    string? ActorId,
    string Action,
    string TargetType,
    string TargetId,
    string? OldValue,
    string? NewValue);

public static class AuditActions
{
    public const string FactorySetUp = "FactorySetUp";
    public const string EmployeeCreated = "EmployeeCreated";
    public const string EmployeeUpdated = "EmployeeUpdated";
    public const string EmployeeDisabled = "EmployeeDisabled";
    public const string EmployeeEnabled = "EmployeeEnabled";
    public const string RateChanged = "RateChanged";
    public const string EntrySaved = "EntrySaved";
    public const string EntryDeleted = "EntryDeleted";
    public const string PinChanged = "PinChanged";
    public const string PaymentMarked = "PaymentMarked";
    public const string PaymentUnmarked = "PaymentUnmarked";
    public const string DataExported = "DataExported";
    public const string DataImported = "DataImported";
    public const string PhoneLinked = "PhoneLinked";
    public const string FactoryUpdated = "FactoryUpdated";
    public const string OwnerWeaves = "OwnerWeaves";
    public const string PinReset = "PinReset";
}
