using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow;

/// <summary>One line of the change history, with the person's name instead of an id.</summary>
public sealed record AuditLine(DateTimeOffset At, string Who, string Action, string? Before, string? After);

/// <summary>The change history (§ Security: audit logs), for the owner.</summary>
public sealed class AuditService(WorkflowContext context)
{
    public async Task<Result<IReadOnlyList<AuditLine>>> RecentAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin)
        {
            return Result.Fail<IReadOnlyList<AuditLine>>(ErrorCode.NotAllowed);
        }

        var names = (await context.Store.GetEmployeesAsync(cancellationToken)).ToDictionary(e => e.Id, e => e.FullName);
        IReadOnlyList<AuditLine> lines = (await context.Store.GetAuditAsync(limit, cancellationToken))
            .Select(a => new AuditLine(a.At, a.ActorId is { } id && names.TryGetValue(id, out var n) ? n : "", a.Action, a.OldValue, a.NewValue))
            .ToList();
        return Result.Success(lines);
    }
}
