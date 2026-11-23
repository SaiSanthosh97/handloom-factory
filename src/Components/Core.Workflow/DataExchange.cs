using System.Text.Json;
using System.Text.Json.Serialization;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow;

/// <summary>
/// Everything about the factory, as written to an export file (D18): factory, people, rate history, every entry,
/// every payment and the audit log. Never contains PINs or keys.
/// </summary>
public sealed record FactoryData(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    string AppVersion,
    FactoryInfo Factory,
    IReadOnlyList<Employee> Employees,
    IReadOnlyList<RateChange> Rates,
    IReadOnlyList<ProductionEntry> Entries,
    IReadOnlyList<Payment> Payments,
    IReadOnlyList<AuditEntry> Audit)
{
    /// <summary>Raise when the file layout changes; older apps then refuse newer files instead of misreading them.</summary>
    public const int CurrentFormat = 1;

    /// <summary>"handloom-factory-data" marks the file so other JSON files are refused. Set when writing.</summary>
    public string? Kind { get; init; }

    public const string FileKind = "handloom-factory-data";
}

/// <summary>What the owner sees before confirming an import.</summary>
public sealed record ImportPreview(FactoryData Data, string FactoryName, int Workers, int Entries, int Payments, DateOnly? FirstDay, DateOnly? LastDay, DateTimeOffset ExportedAt);

/// <summary>Reads and writes export files: indented, readable JSON (D18: plain, not password-protected).</summary>
public static class FactoryDataFile
{
    private static JsonSerializerOptions Options => FactoryJson.Options;

    public static string Write(FactoryData data) => JsonSerializer.Serialize(data with { Kind = FactoryData.FileKind }, Options);

    /// <returns>The data, or an error: not our file, made by a newer app, or damaged.</returns>
    public static Result<FactoryData> Read(string json)
    {
        FactoryData? data;
        try
        {
            data = JsonSerializer.Deserialize<FactoryData>(json, Options);
        }
        catch (JsonException)
        {
            return Result.Fail<FactoryData>(ErrorCode.ImportInvalid);
        }

        if (data is null || data.Kind != FactoryData.FileKind)
        {
            return Result.Fail<FactoryData>(ErrorCode.ImportInvalid);
        }

        if (data.FormatVersion > FactoryData.CurrentFormat)
        {
            return Result.Fail<FactoryData>(ErrorCode.ImportTooNew);
        }

        return Validate(data) is { } problem ? Result.Fail<FactoryData>(ErrorCode.ImportInvalid, problem) : Result.Success(data);
    }

    /// <summary>Checks the file is complete and consistent before anything on the phone is replaced.</summary>
    private static string? Validate(FactoryData data)
    {
        if (data.Factory is null || data.Employees is null || data.Rates is null || data.Entries is null || data.Payments is null || data.Audit is null)
        {
            return "missing section";
        }

        var ids = data.Employees.Select(e => e.Id).ToHashSet();
        if (ids.Count != data.Employees.Count || data.Employees.Select(e => e.Code).Distinct().Count() != data.Employees.Count)
        {
            return "duplicate employee";
        }

        if (!data.Employees.Any(e => e.Role == Role.Admin))
        {
            return "no owner";
        }

        if (data.Rates.Any(r => !ids.Contains(r.EmployeeId) || r.Rate.Paise <= 0)
            || data.Entries.Any(e => !ids.Contains(e.EmployeeId) || e.SareeCount < 0)
            || data.Payments.Any(p => !ids.Contains(p.EmployeeId)))
        {
            return "record for an unknown employee or a bad value";
        }

        if (data.Entries.Select(e => (e.EmployeeId, e.Date)).Distinct().Count() != data.Entries.Count
            || data.Payments.Select(p => (p.EmployeeId, p.WeekStart)).Distinct().Count() != data.Payments.Count)
        {
            return "duplicate entry or payment";
        }

        return null;
    }

}

/// <summary>One JSON setup for export files and sync messages, so a record looks the same in both.</summary>
public static class FactoryJson
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Compact form for sync records (export files are indented).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MoneyConverter());
        return options;
    }

    /// <summary>Money as a plain number of paise (50000 = ₹500), so files and messages stay exact and readable.</summary>
    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Paise);
    }
}
