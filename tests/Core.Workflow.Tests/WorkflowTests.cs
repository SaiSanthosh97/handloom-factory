using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Security;
using HandloomFactory.Storage.Sqlite;

namespace HandloomFactory.Core.Workflow.Tests;

/// <summary>A phone: a real encrypted database in a temp folder, a manual clock set to Mon 2026-11-23 10:00 IST.</summary>
public sealed class Phone : IDisposable
{
    public Phone()
    {
        Folder = Path.Combine(Path.GetTempPath(), "hf-tests", Guid.NewGuid().ToString("N"));
        Database = FactoryDatabaseFiles.Open(Folder, Vault);
        Store = new SqliteFactoryStore(Database);
        Context = new WorkflowContext(Store, Clock, Session);
        Accounts = new AccountService(Context);
        Employees = new EmployeeService(Context);
        Production = new ProductionService(Context);
        Reports = new ReportService(Context);
        Payments = new PaymentService(Context);
        Data = new DataService(Context);
    }

    public PaymentService Payments { get; }

    public DataService Data { get; }

    public Employee Owner => Store.GetEmployeesAsync().Result.Single(e => e.Role == Role.Admin);

    public Task EnterAsync(Employee worker, DateOnly day, int? count) =>
        Production.SaveDayAsync(day, new Dictionary<string, int?> { [worker.Id] = count });

    public static DateOnly Today => new(2026, 11, 23);

    public string Folder { get; }

    public InMemoryKeyVault Vault { get; } = new();

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(5.5)));

    public UserSession Session { get; } = new();

    public FactoryDatabase Database { get; }

    public SqliteFactoryStore Store { get; }

    public WorkflowContext Context { get; }

    public AccountService Accounts { get; }

    public EmployeeService Employees { get; }

    public ProductionService Production { get; }

    public ReportService Reports { get; }

    public async Task<Employee> SetUpAsync()
    {
        var result = await Accounts.SetUpFactoryAsync("Sri Lakshmi Handlooms", "Santhosh", "1234", "1234", "Asia/Kolkata");
        Assert.True(result.Ok, result.Error?.ToString());
        return result.Value!;
    }

    public async Task<Employee> AddAsync(string name, long rupees, DateOnly? from = null)
    {
        var result = await Employees.CreateAsync(name, null, Money.FromRupees(rupees), from ?? new DateOnly(2026, 11, 1));
        Assert.True(result.Ok, result.Error?.ToString());
        return result.Value!;
    }

    public void Dispose()
    {
        Database.Dispose();
        FactoryDatabaseFiles.Erase(Folder);
    }
}

public class AccountTests
{
    [Fact]
    public async Task Setup_creates_factory_and_signs_in_the_owner()
    {
        using var phone = new Phone();
        Assert.False(await phone.Accounts.IsSetUpAsync());

        var owner = await phone.SetUpAsync();

        Assert.True(await phone.Accounts.IsSetUpAsync());
        Assert.Equal(Role.Admin, owner.Role);
        Assert.True(phone.Session.IsAdmin);
        Assert.Equal(ErrorCode.AlreadySetUp, (await phone.Accounts.SetUpFactoryAsync("X", "Y", "1111", "1111")).Error?.Code);
        Assert.Contains((await phone.Store.GetAuditAsync(10)), a => a.Action == AuditActions.FactorySetUp);
    }

    [Theory]
    [InlineData("", "Owner", "1234", "1234", ErrorCode.NameRequired)]
    [InlineData("Factory", "Owner", "123", "123", ErrorCode.PinMustBeFourDigits)]
    [InlineData("Factory", "Owner", "1234", "4321", ErrorCode.PinsDoNotMatch)]
    public async Task Setup_validates(string factory, string owner, string pin, string confirm, ErrorCode expected)
    {
        using var phone = new Phone();
        Assert.Equal(expected, (await phone.Accounts.SetUpFactoryAsync(factory, owner, pin, confirm)).Error?.Code);
        Assert.False(await phone.Accounts.IsSetUpAsync());
    }

    [Fact]
    public async Task Login_with_pin_and_lockout_after_five_wrong()
    {
        using var phone = new Phone();
        var owner = await phone.SetUpAsync();
        phone.Accounts.Logout();
        Assert.Equal([owner.Id], (await phone.Accounts.LoginChoicesAsync()).Select(e => e.Id));

        for (var i = 1; i <= 4; i++)
        {
            var wrong = await phone.Accounts.LoginAsync(owner.Id, "0000");
            Assert.False(wrong.Success);
            Assert.Equal(5 - i, wrong.AttemptsLeft);
            Assert.Null(wrong.LockedUntil);
        }

        var locked = await phone.Accounts.LoginAsync(owner.Id, "0000");
        Assert.Equal(phone.Clock.UtcNow.AddSeconds(30), locked.LockedUntil);

        // Even the right PIN is refused while locked…
        Assert.False((await phone.Accounts.LoginAsync(owner.Id, "1234")).Success);
        Assert.False(phone.Session.IsSignedIn);

        // …and accepted after the wait, which also resets the counter.
        phone.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await phone.Accounts.LoginAsync(owner.Id, "1234")).Success);
        Assert.True(phone.Session.IsAdmin);
        Assert.Equal(0, (await phone.Store.GetCredentialAsync(owner.Id))!.FailedAttempts);
    }

    [Fact]
    public async Task Change_pin_requires_the_current_pin()
    {
        using var phone = new Phone();
        var owner = await phone.SetUpAsync();

        Assert.Equal(ErrorCode.WrongPin, (await phone.Accounts.ChangePinAsync("9999", "5678", "5678")).Error?.Code);
        Assert.True((await phone.Accounts.ChangePinAsync("1234", "5678", "5678")).Ok);

        phone.Accounts.Logout();
        Assert.False((await phone.Accounts.LoginAsync(owner.Id, "1234")).Success);
        Assert.True((await phone.Accounts.LoginAsync(owner.Id, "5678")).Success);
    }

    [Fact]
    public async Task Data_survives_reopening_and_the_file_is_encrypted()
    {
        var phone = new Phone();
        await phone.SetUpAsync();
        await phone.AddAsync("Ramesh", 500);
        phone.Database.Dispose();

        // Reopen with the same vault: the data is there.
        using (var reopened = FactoryDatabaseFiles.Open(phone.Folder, phone.Vault))
        {
            var store = new SqliteFactoryStore(reopened);
            Assert.Contains(await store.GetEmployeesAsync(), e => e.FullName == "Ramesh");
        }

        // The file on disk contains no readable names.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(phone.Folder, FactoryDatabaseFiles.DatabaseFile));
        Assert.DoesNotContain("Ramesh", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // A different key vault can't open it.
        Assert.Throws<InvalidDataException>(() => FactoryDatabaseFiles.Open(phone.Folder, new InMemoryKeyVault()));
        FactoryDatabaseFiles.Erase(phone.Folder);
    }
}

public class EmployeeTests
{
    [Fact]
    public async Task Add_edit_disable_enable()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var suma = await phone.AddAsync("Suma", 450);

        Assert.Equal("E001", ramesh.Code);
        Assert.Equal("E002", suma.Code);

        var updated = await phone.Employees.UpdateAsync(ramesh.Id, " Ramesh K ", "98765 43210");
        Assert.Equal("Ramesh K", updated.Value!.FullName);
        Assert.Equal("9876543210", updated.Value.PhoneNumber);
        Assert.Equal(ErrorCode.PhoneInvalid, (await phone.Employees.UpdateAsync(ramesh.Id, "R", "123")).Error?.Code);

        Assert.True((await phone.Employees.SetActiveAsync(suma.Id, false)).Ok);
        var list = (await phone.Employees.ListAsync()).Value!;
        Assert.Equal(["Ramesh K", "Suma"], list.Select(i => i.Employee.FullName)); // active first, owner not listed
        Assert.False(list[1].Employee.IsActive);
        Assert.Equal(Money.FromRupees(500), list[0].CurrentRate);

        Assert.True((await phone.Employees.SetActiveAsync(suma.Id, true)).Ok);
        var actions = (await phone.Store.GetAuditAsync(50)).Select(a => a.Action).ToList();
        Assert.Contains(AuditActions.EmployeeDisabled, actions);
        Assert.Contains(AuditActions.EmployeeEnabled, actions);
    }

    [Fact]
    public async Task Rate_must_be_positive()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        Assert.Equal(ErrorCode.RateInvalid, (await phone.Employees.CreateAsync("X", null, Money.Zero, Phone.Today)).Error?.Code);
        Assert.Equal(ErrorCode.NameRequired, (await phone.Employees.CreateAsync("  ", null, Money.FromRupees(1), Phone.Today)).Error?.Code);
    }

    [Fact]
    public async Task Rate_change_keeps_history_and_past_earnings()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.Production.SaveDayAsync(new DateOnly(2026, 11, 22), new Dictionary<string, int?> { [ramesh.Id] = 2 });

        Assert.True((await phone.Employees.ChangeRateAsync(ramesh.Id, Money.FromRupees(600), Phone.Today)).Ok);
        await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?> { [ramesh.Id] = 1 });

        var detail = (await phone.Employees.GetAsync(ramesh.Id)).Value!;
        Assert.Equal([Money.FromRupees(600), Money.FromRupees(500)], detail.RateHistory.Select(r => r.Rate)); // newest first
        Assert.Equal(Money.FromRupees(600), detail.Summary.CurrentRate);
        Assert.Equal(Money.FromRupees((2 * 500) + 600), detail.Summary.ThisWeek.Earnings);
        Assert.Equal(1, detail.Summary.TodaySarees);
    }

    [Fact]
    public async Task Workers_cannot_manage_employees()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        phone.Session.SignIn(ramesh);

        Assert.Equal(ErrorCode.NotAllowed, (await phone.Employees.ListAsync()).Error?.Code);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Employees.CreateAsync("X", null, Money.FromRupees(1), Phone.Today)).Error?.Code);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Reports.DashboardAsync()).Error?.Code);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?>())).Error?.Code);
    }
}

public class ProductionTests
{
    [Fact]
    public async Task Owner_enters_a_day_for_everyone_and_the_dashboard_adds_up()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var suma = await phone.AddAsync("Suma", 400);

        var saved = await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?> { [ramesh.Id] = 3, [suma.Id] = null });
        Assert.Equal(1, saved.Value);

        var sheet = (await phone.Production.GetDayAsync(Phone.Today)).Value!;
        Assert.True(sheet.IsToday);
        Assert.Equal(3, sheet.Rows.Single(r => r.Employee.Id == ramesh.Id).SareeCount);
        Assert.Null(sheet.Rows.Single(r => r.Employee.Id == suma.Id).SareeCount);

        var dashboard = (await phone.Reports.DashboardAsync()).Value!;
        Assert.Equal(3, dashboard.TodaySarees);
        Assert.Equal(["Suma"], dashboard.NotEnteredToday);
        Assert.Equal(Money.FromRupees(1500), dashboard.WeekWages);
    }

    [Fact]
    public async Task One_entry_per_day_edit_and_clear()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var day = new Dictionary<string, int?> { [ramesh.Id] = 3 };

        await phone.Production.SaveDayAsync(Phone.Today, day);
        day[ramesh.Id] = 4;
        await phone.Production.SaveDayAsync(Phone.Today, day);
        Assert.Equal(0, (await phone.Production.SaveDayAsync(Phone.Today, day)).Value); // nothing changed

        var entries = await phone.Store.GetEntriesAsync(new DateRange(Phone.Today, Phone.Today));
        Assert.Equal(4, Assert.Single(entries).SareeCount);

        day[ramesh.Id] = null;
        Assert.Equal(1, (await phone.Production.SaveDayAsync(Phone.Today, day)).Value);
        Assert.Empty(await phone.Store.GetEntriesAsync(new DateRange(Phone.Today, Phone.Today)));
        Assert.Contains(await phone.Store.GetAuditAsync(20), a => a.Action == AuditActions.EntryDeleted);
    }

    [Fact]
    public async Task Invalid_row_saves_nothing_and_names_the_employee()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var suma = await phone.AddAsync("Suma", 400);

        var result = await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?> { [ramesh.Id] = 3, [suma.Id] = 99 });

        Assert.Equal(ErrorCode.CountOutOfRange, result.Error?.Code);
        Assert.Equal("Suma", result.Error?.Detail);
        Assert.Empty(await phone.Store.GetEntriesAsync(new DateRange(Phone.Today, Phone.Today)));
    }

    [Fact]
    public async Task Owner_can_fill_past_days_but_not_future_or_before_the_first_rate()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500, from: new DateOnly(2026, 11, 20));

        Assert.True((await phone.Production.SaveDayAsync(new DateOnly(2026, 11, 20), new Dictionary<string, int?> { [ramesh.Id] = 2 })).Ok);
        Assert.Equal(ErrorCode.FutureDate, (await phone.Production.SaveDayAsync(Phone.Today.AddDays(1), new Dictionary<string, int?> { [ramesh.Id] = 2 })).Error?.Code);
        Assert.Equal(ErrorCode.NoRateOnDate, (await phone.Production.SaveDayAsync(new DateOnly(2026, 11, 19), new Dictionary<string, int?> { [ramesh.Id] = 2 })).Error?.Code);
    }

    [Fact]
    public async Task Worker_saves_only_today_and_admin_value_wins()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        phone.Session.SignIn(ramesh);

        var own = await phone.Production.SaveOwnTodayAsync(3);
        Assert.Equal(EnteredBy.Employee, own.Value!.EnteredBy);
        Assert.True((await phone.Production.SaveOwnTodayAsync(4)).Ok); // can edit the same day

        // Admin corrects to 2: the worker can no longer change today's value (D10).
        phone.Session.SignIn((await phone.Store.GetEmployeesAsync()).Single(e => e.Role == Role.Admin));
        await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?> { [ramesh.Id] = 2 });
        phone.Session.SignIn(ramesh);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Production.SaveOwnTodayAsync(5)).Error?.Code);

        var entry = Assert.Single(await phone.Store.GetEntriesAsync(new DateRange(Phone.Today, Phone.Today)));
        Assert.Equal(2, entry.SareeCount);
        Assert.Equal(EnteredBy.Admin, entry.EnteredBy);
    }

    [Fact]
    public async Task Weekly_report_through_the_service()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.Production.SaveDayAsync(new DateOnly(2026, 11, 22), new Dictionary<string, int?> { [ramesh.Id] = 1 }); // Sunday
        await phone.Production.SaveDayAsync(Phone.Today, new Dictionary<string, int?> { [ramesh.Id] = 3 });

        var report = (await phone.Reports.WeeklyAsync(Phone.Today)).Value!;

        Assert.Equal(new DateOnly(2026, 11, 22), report.Period.Start);
        Assert.Equal(4, report.TotalSarees);
        Assert.Equal(Money.FromRupees(2000), report.TotalEarnings);
        Assert.Equal([1, 3, null, null, null, null, null], report.Rows[0].DailyCounts);
    }
}
