using System.Text.Json;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow;

/// <summary>What every use case needs: the store, the clock, the logged-in user and the factory's time zone.</summary>
public sealed class WorkflowContext(IFactoryStore store, IClock clock, UserSession session)
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);
    private FactoryInfo? _factory;

    public IFactoryStore Store { get; } = store;

    public IClock Clock { get; } = clock;

    public UserSession Session { get; } = session;

    public DateTimeOffset Now => Clock.UtcNow;

    /// <returns>The factory, or null before setup.</returns>
    public async Task<FactoryInfo?> FactoryAsync(CancellationToken cancellationToken = default) =>
        _factory ??= await Store.GetFactoryAsync(cancellationToken);

    /// <summary>"Today" in the factory's time zone (or India before setup).</summary>
    public async Task<FactoryTime> TimeAsync(CancellationToken cancellationToken = default) =>
        new(Clock, FactoryTime.FindZone((await FactoryAsync(cancellationToken))?.TimeZoneId));

    /// <summary>Forget the cached factory (after setup or import).</summary>
    public void Reset() => _factory = null;

    public AuditEntry Audit(string action, string targetType, string targetId, object? oldValue, object? newValue) =>
        new(Ids.New(), Now, Session.User?.Id, action, targetType, targetId, Describe(oldValue), Describe(newValue));

    private static string? Describe(object? value) => value switch
    {
        null => null,
        string text => text,
        _ => JsonSerializer.Serialize(value, value.GetType(), AuditJson),
    };
}
