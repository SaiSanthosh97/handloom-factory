using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow;

/// <summary>
/// Export all data to one file and import it again (D18). Import <b>replaces everything</b> on the phone; the app saves
/// an automatic copy of the current data first (see the app's DataFiles), so a mistaken import can be undone.
/// </summary>
public sealed class DataService(WorkflowContext context)
{
    private static readonly DateRange AllTime = new(DateOnly.MinValue, DateOnly.MaxValue);

    /// <summary>Everything as an export file's text.</summary>
    /// <param name="audit">False for the automatic safety copy (it doesn't add an "exported" line to the log).</param>
    public async Task<Result<string>> ExportAsync(string appVersion, bool audit = true, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<string>(ErrorCode.NotAllowed);
        }

        if (await context.FactoryAsync(cancellationToken) is not { } factory)
        {
            return Result.Fail<string>(ErrorCode.NotFound);
        }

        if (audit)
        {
            var changes = new ChangeSet();
            changes.Audit.Add(context.Audit(AuditActions.DataExported, "Factory", factory.Id, null, null));
            await context.Store.CommitAsync(changes, cancellationToken);
        }

        var store = context.Store;
        var data = new FactoryData(
            FactoryData.CurrentFormat,
            context.Now,
            appVersion,
            factory,
            await store.GetEmployeesAsync(cancellationToken),
            await store.GetRateChangesAsync(cancellationToken),
            await store.GetEntriesAsync(AllTime, null, cancellationToken),
            await store.GetPaymentsAsync(null, cancellationToken),
            (await store.GetAuditAsync(int.MaxValue, cancellationToken)).Reverse().ToList());
        return Result.Success(FactoryDataFile.Write(data));
    }

    /// <summary>Reads a file and summarises it for the confirmation screen. Nothing is changed yet.</summary>
    public Result<ImportPreview> Preview(string fileText)
    {
        // Allowed for the owner, and on a phone that isn't set up yet ("Restore from a data file" on a new phone).
        if (!context.Session.IsAdmin && context.Session.IsSignedIn)
        {
            return Result.Fail<ImportPreview>(ErrorCode.NotAllowed);
        }

        var read = FactoryDataFile.Read(fileText);
        if (!read.Ok)
        {
            return Result.Fail<ImportPreview>(read.Error!.Code, read.Error.Detail);
        }

        var d = read.Value!;
        var days = d.Entries.Select(e => e.Date).ToList();
        return Result.Success(new ImportPreview(
            d,
            d.Factory.Name,
            d.Employees.Count(e => e.IsWorker),
            d.Entries.Count,
            d.Payments.Count,
            days.Count > 0 ? days.Min() : null,
            days.Count > 0 ? days.Max() : null,
            d.ExportedAt));
    }

    /// <summary>
    /// Replaces everything with the file's data and logs out. If the owner in the file has no PIN on this phone
    /// (a new phone), the app asks them to choose one (<see cref="AccountService.OwnerNeedingPinAsync"/>).
    /// </summary>
    public async Task<Result> ImportAsync(FactoryData data, CancellationToken cancellationToken = default)
    {
        // The owner, or a brand-new phone (nothing set up, nobody to protect).
        if (!context.Session.IsAdmin && await context.FactoryAsync(cancellationToken) is not null)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        var imported = data with
        {
            Audit = [.. data.Audit, context.Audit(AuditActions.DataImported, "Factory", data.Factory.Id, null, $"{data.Factory.Name}, exported {data.ExportedAt:yyyy-MM-dd HH:mm}")],
        };
        await context.Store.ReplaceAllAsync(imported, cancellationToken);
        context.Reset();
        context.Session.SignOut();
        return Result.Success();
    }
}
