using System.Security.Cryptography;
using System.Text;

namespace HandloomFactory.Core.Common;

/// <summary>Source of the current time. Tests use a manual clock so date rules (pay weeks, "today") are repeatable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A clock that only moves when told to. For tests.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan by) => UtcNow += by;

    public void Set(DateTimeOffset to) => UtcNow = to;
}

/// <summary>
/// "Today" and "now" in the factory's time zone. Every phone uses the factory's zone (not its own), so all phones
/// agree which day and which pay week an entry belongs to.
/// </summary>
public sealed class FactoryTime(IClock clock, TimeZoneInfo zone)
{
    public const string DefaultZoneId = "Asia/Kolkata";

    public IClock Clock { get; } = clock;

    public TimeZoneInfo Zone { get; } = zone;

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(Clock.UtcNow, Zone);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>Finds a zone by id, falling back to India Standard Time, then UTC.</summary>
    public static TimeZoneInfo FindZone(string? id)
    {
        foreach (var candidate in new[] { id, DefaultZoneId, "India Standard Time" })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Utc;
    }
}

/// <summary>Record ids.</summary>
public static class Ids
{
    /// <summary>A new random id: a time-ordered GUID (version 7), so later ids sort later in indexes.</summary>
    public static string New() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// A deterministic id built from natural-key parts (e.g. employee + date). Two phones that create "the same"
    /// record independently get the same id, which is how "one entry per employee per day" holds without a server.
    /// </summary>
    public static string For(string kind, params string[] parts)
    {
        var text = kind + "|" + string.Join('|', parts);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}
