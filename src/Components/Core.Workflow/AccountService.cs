using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Security;

namespace HandloomFactory.Core.Workflow;

/// <summary>Outcome of a PIN attempt.</summary>
public sealed record LoginOutcome(bool Success, int AttemptsLeft, DateTimeOffset? LockedUntil);

/// <summary>Factory setup (first launch), PIN login and PIN change (D2).</summary>
public sealed class AccountService(WorkflowContext context)
{
    public async Task<bool> IsSetUpAsync(CancellationToken cancellationToken = default) =>
        await context.FactoryAsync(cancellationToken) is not null;

    /// <summary>First launch on the owner's phone: the factory, and the owner as its admin with a PIN.</summary>
    public async Task<Result<Employee>> SetUpFactoryAsync(string factoryName, string ownerName, string pin, string confirmPin, string? timeZoneId = null, CancellationToken cancellationToken = default)
    {
        if (await IsSetUpAsync(cancellationToken))
        {
            return Result.Fail<Employee>(ErrorCode.AlreadySetUp);
        }

        var name = factoryName.Trim();
        var owner = ownerName.Trim();
        if (name.Length == 0 || owner.Length == 0)
        {
            return Result.Fail<Employee>(ErrorCode.NameRequired);
        }

        if (PinError(pin, confirmPin) is { } error)
        {
            return Result.Fail<Employee>(error);
        }

        var now = context.Now;
        var zone = FactoryTime.FindZone(timeZoneId);
        var factory = new FactoryInfo(Ids.New(), name, owner, zone.Id, FactoryInfo.DefaultMaxSareesPerDay, now);
        var admin = new Employee(Ids.New(), "A001", owner, null, Role.Admin, true, now, now);
        var changes = new ChangeSet { Factory = factory };
        changes.Employees.Add(admin);
        changes.Credentials.Add(new Credential(admin.Id, Pins.Hash(pin), 0, null));
        changes.Audit.Add(new AuditEntry(Ids.New(), now, admin.Id, AuditActions.FactorySetUp, "Factory", factory.Id, null, name));
        await context.Store.CommitAsync(changes, cancellationToken);
        context.Reset();
        context.Session.SignIn(admin);
        return Result.Success(admin);
    }

    /// <summary>The names shown on the login screen: active people who have a PIN on this phone.</summary>
    public async Task<IReadOnlyList<Employee>> LoginChoicesAsync(CancellationToken cancellationToken = default)
    {
        var people = new List<Employee>();
        foreach (var employee in await context.Store.GetEmployeesAsync(cancellationToken))
        {
            if (employee.IsActive && await context.Store.GetCredentialAsync(employee.Id, cancellationToken) is not null)
            {
                people.Add(employee);
            }
        }

        return people.OrderByDescending(e => e.Role).ThenBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<LoginOutcome> LoginAsync(string employeeId, string pin, CancellationToken cancellationToken = default)
    {
        var employee = (await context.Store.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == employeeId);
        var credential = await context.Store.GetCredentialAsync(employeeId, cancellationToken);
        if (employee is not { IsActive: true } || credential is null)
        {
            return new LoginOutcome(false, 0, null);
        }

        var now = context.Now;
        if (credential.LockedUntil is { } until && until > now)
        {
            return new LoginOutcome(false, 0, until);
        }

        var changes = new ChangeSet();
        if (Pins.Verify(pin, credential.Pin))
        {
            changes.Credentials.Add(credential with { FailedAttempts = 0, LockedUntil = null });
            await context.Store.CommitAsync(changes, cancellationToken);
            context.Session.SignIn(employee);
            return new LoginOutcome(true, PinLockout.FreeAttempts, null);
        }

        var failures = credential.FailedAttempts + 1;
        var wait = PinLockout.WaitAfter(failures);
        var lockedUntil = wait > TimeSpan.Zero ? now + wait : (DateTimeOffset?)null;
        changes.Credentials.Add(credential with { FailedAttempts = failures, LockedUntil = lockedUntil });
        await context.Store.CommitAsync(changes, cancellationToken);
        return new LoginOutcome(false, Math.Max(0, PinLockout.FreeAttempts - failures), lockedUntil);
    }

    public void Logout() => context.Session.SignOut();

    /// <summary>The logged-in person changes their own PIN (the current PIN is required).</summary>
    public async Task<Result> ChangePinAsync(string currentPin, string newPin, string confirmPin, CancellationToken cancellationToken = default)
    {
        if (context.Session.User is not { } user || await context.Store.GetCredentialAsync(user.Id, cancellationToken) is not { } credential)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        if (!Pins.Verify(currentPin, credential.Pin))
        {
            return Result.Fail(ErrorCode.WrongPin);
        }

        if (PinError(newPin, confirmPin) is { } error)
        {
            return Result.Fail(error);
        }

        var changes = new ChangeSet();
        changes.Credentials.Add(new Credential(user.Id, Pins.Hash(newPin), 0, null));
        changes.Audit.Add(context.Audit(AuditActions.PinChanged, "Employee", user.Id, null, null));
        await context.Store.CommitAsync(changes, cancellationToken);
        return Result.Success();
    }

    private static ErrorCode? PinError(string pin, string confirmPin) =>
        !Pins.IsWellFormed(pin) ? ErrorCode.PinMustBeFourDigits
        : pin != confirmPin ? ErrorCode.PinsDoNotMatch
        : null;
}
