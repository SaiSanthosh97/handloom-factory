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

    /// <summary>
    /// After importing data on a new phone the owner has no PIN here yet (PINs are never exported). Returns that owner,
    /// or null when every active owner has a PIN.
    /// </summary>
    /// <remarks>
    /// On a worker's phone (which holds no owner record) it is the worker, right after linking (TD-03): the phone
    /// received their data from the owner and they choose their PIN.
    /// </remarks>
    public async Task<Employee?> PersonNeedingPinAsync(CancellationToken cancellationToken = default)
    {
        var people = (await context.Store.GetEmployeesAsync(cancellationToken)).Where(e => e.IsActive).ToList();
        var candidates = people.Any(e => e.Role == Role.Admin) ? people.Where(e => e.Role == Role.Admin).ToList() : people;
        foreach (var person in candidates)
        {
            if (await context.Store.GetCredentialAsync(person.Id, cancellationToken) is not null)
            {
                return null; // someone can already log in
            }
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>Sets the first PIN for an owner who has none on this phone (after an import) and logs them in.</summary>
    public async Task<Result> SetFirstPinAsync(string pin, string confirmPin, CancellationToken cancellationToken = default)
    {
        if (await PersonNeedingPinAsync(cancellationToken) is not { } owner)
        {
            return Result.Fail(ErrorCode.PinAlreadySet);
        }

        if (PinError(pin, confirmPin) is { } error)
        {
            return Result.Fail(error);
        }

        var changes = new ChangeSet();
        changes.Credentials.Add(new Credential(owner.Id, Pins.Hash(pin), 0, null));
        changes.Audit.Add(new AuditEntry(Ids.New(), context.Now, owner.Id, AuditActions.PinChanged, "Employee", owner.Id, null, null));
        await context.Store.CommitAsync(changes, cancellationToken);
        context.Session.SignIn(owner);
        return Result.Success();
    }

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

    /// <summary>Owner: change the factory name and their own name (Settings, 0.6.0).</summary>
    public async Task<Result> UpdateFactoryAsync(string factoryName, string ownerName, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin || await context.FactoryAsync(cancellationToken) is not { } factory || context.Session.User is not { } owner)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        var name = factoryName.Trim();
        var person = ownerName.Trim();
        if (name.Length is 0 or > 80 || person.Length is 0 or > 80)
        {
            return Result.Fail(ErrorCode.NameRequired);
        }

        var renamed = owner with { FullName = person, UpdatedAt = context.Now };
        var changes = new ChangeSet { Factory = factory with { Name = name, OwnerName = person } };
        changes.Employees.Add(renamed);
        changes.Audit.Add(context.Audit(AuditActions.FactoryUpdated, "Factory", factory.Id, new { factory.Name, factory.OwnerName }, new { Name = name, OwnerName = person }));
        await context.Store.CommitAsync(changes, cancellationToken);
        context.Reset();
        context.Session.SignIn(renamed);
        return Result.Success();
    }

    /// <summary>
    /// Owner: "I also weave sarees" (0.6.0). When on, the owner appears in the Workers list, day entry, reports and
    /// payments like any worker. The first time, a rate per saree is needed (from <paramref name="rateFrom"/>).
    /// Turning it off keeps the history; it only stops new days.
    /// </summary>
    public async Task<Result> SetOwnerWeavesAsync(bool weaves, Money? rate = null, DateOnly? rateFrom = null, CancellationToken cancellationToken = default)
    {
        if (!context.Session.IsAdmin || context.Session.User is not { } owner)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        var current = (await context.Store.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == owner.Id) ?? owner;
        var hasRate = (await context.Store.GetRateChangesAsync(cancellationToken)).Any(r => r.EmployeeId == owner.Id);
        var changes = new ChangeSet();
        if (weaves && rate is { } newRate)
        {
            if (newRate.Paise <= 0)
            {
                return Result.Fail(ErrorCode.RateInvalid);
            }

            var from = rateFrom ?? (await context.TimeAsync(cancellationToken)).Today;
            changes.Rates.Add(new RateChange(RateChange.IdFor(owner.Id, from), owner.Id, newRate, from, context.Now));
            changes.Audit.Add(context.Audit(AuditActions.RateChanged, "Employee", owner.Id, null, new { Rate = newRate.ToString(), From = from }));
        }
        else if (weaves && !hasRate)
        {
            return Result.Fail(ErrorCode.RateInvalid);
        }

        var updated = current with { Weaves = weaves, UpdatedAt = context.Now };
        changes.Employees.Add(updated);
        changes.Audit.Add(context.Audit(AuditActions.OwnerWeaves, "Employee", owner.Id, current.Weaves, weaves));
        await context.Store.CommitAsync(changes, cancellationToken);
        context.Session.SignIn(updated);
        return Result.Success();
    }

    /// <summary>Whether the owner already has a rate (so "I also weave" can be turned on again without asking).</summary>
    public async Task<bool> OwnerHasRateAsync(CancellationToken cancellationToken = default) =>
        context.Session.User is { } owner && (await context.Store.GetRateChangesAsync(cancellationToken)).Any(r => r.EmployeeId == owner.Id);

    /// <summary>The owner of this phone (for "Forgot PIN?"), or null on a worker's phone.</summary>
    public async Task<Employee?> OwnerAsync(CancellationToken cancellationToken = default) =>
        (await context.Store.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.IsOwner && e.IsActive);

    /// <summary>
    /// "Forgot PIN?" on the owner's phone: a new PIN without the old one. The app must first confirm the phone's own
    /// screen lock (fingerprint, pattern or phone PIN), which only the phone's owner knows (requirements: Owner forgot PIN).
    /// Workers who forget their PIN are re-linked by the owner instead.
    /// </summary>
    public async Task<Result> ResetOwnerPinAsync(string newPin, string confirmPin, CancellationToken cancellationToken = default)
    {
        if (await OwnerAsync(cancellationToken) is not { } owner)
        {
            return Result.Fail(ErrorCode.NotAllowed);
        }

        if (PinError(newPin, confirmPin) is { } error)
        {
            return Result.Fail(error);
        }

        var changes = new ChangeSet();
        changes.Credentials.Add(new Credential(owner.Id, Pins.Hash(newPin), 0, null));
        changes.Audit.Add(new AuditEntry(Ids.New(), context.Now, owner.Id, AuditActions.PinReset, "Employee", owner.Id, null, null));
        await context.Store.CommitAsync(changes, cancellationToken);
        context.Session.SignIn(owner);
        return Result.Success();
    }

    private static ErrorCode? PinError(string pin, string confirmPin) =>
        !Pins.IsWellFormed(pin) ? ErrorCode.PinMustBeFourDigits
        : pin != confirmPin ? ErrorCode.PinsDoNotMatch
        : null;
}
