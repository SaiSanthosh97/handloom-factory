using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;

namespace HandloomFactory.Core.Workflow.Tests;

public class PaymentTests
{
    private static readonly DateOnly Sun = new(2026, 11, 22);
    private static readonly DateOnly Sat = new(2026, 11, 28);
    private static readonly DateOnly PrevSun = new(2026, 11, 15);

    [Fact]
    public async Task Week_shows_pending_then_paid_and_freezes()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.EnterAsync(ramesh, Phone.Today, 3);

        var week = (await phone.Payments.WeekAsync(Phone.Today)).Value!;
        var line = Assert.Single(week.Rows).Payment;
        Assert.Equal(PaymentStatus.Pending, line.Status);
        Assert.Equal(Money.FromRupees(1500), line.Total);
        Assert.Equal(new DateRange(Sun, Sat), week.Week);

        var paid = await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Phone.Today);
        Assert.True(paid.Ok);
        Assert.Equal(Money.FromRupees(1500), paid.Value!.Total);

        week = (await phone.Payments.WeekAsync(Phone.Today)).Value!;
        Assert.Equal(PaymentStatus.Paid, week.Rows[0].Payment.Status);
        Assert.Equal(Phone.Today, week.Rows[0].Payment.PaidDate);
        Assert.Equal(ErrorCode.AlreadyPaid, (await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Phone.Today)).Error?.Code);
        Assert.Contains(await phone.Store.GetAuditAsync(10), a => a.Action == AuditActions.PaymentMarked);
    }

    [Fact]
    public async Task Correction_after_payment_becomes_adjustment_on_next_week()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var lastWeekMonday = new DateOnly(2026, 11, 16);
        await phone.EnterAsync(ramesh, lastWeekMonday, 2);
        await phone.Payments.MarkPaidAsync(ramesh.Id, PrevSun, new DateOnly(2026, 11, 21));

        // Owner corrects last week's Monday 2 → 3 after paying: +₹500 owed.
        await phone.EnterAsync(ramesh, lastWeekMonday, 3);
        var sheet = (await phone.Production.GetDayAsync(lastWeekMonday)).Value!;
        Assert.True(sheet.Rows.Single().WeekPaid);

        var lastWeek = (await phone.Payments.WeekAsync(PrevSun)).Value!.Rows[0].Payment;
        Assert.Equal(Money.FromRupees(1000), lastWeek.Total); // frozen
        Assert.Equal(Money.FromRupees(500), lastWeek.CorrectedSincePaid);

        await phone.EnterAsync(ramesh, Phone.Today, 1);
        var thisWeek = (await phone.Payments.WeekAsync(Phone.Today)).Value!.Rows[0].Payment;
        Assert.Equal(Money.FromRupees(500), thisWeek.Earnings);
        Assert.Equal(Money.FromRupees(500), thisWeek.Adjustment);
        Assert.Equal(Money.FromRupees(1000), thisWeek.Total);

        // Once this week is paid, nothing is outstanding any more.
        await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Phone.Today);
        phone.Clock.Advance(TimeSpan.FromDays(7));
        var nextWeek = (await phone.Payments.WeekAsync(Phone.Today.AddDays(7))).Value!.Rows[0].Payment;
        Assert.Equal(PaymentStatus.Nothing, nextWeek.Status);
        Assert.Equal(Money.Zero, nextWeek.Adjustment);
    }

    [Fact]
    public async Task Over_payment_is_taken_back()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 16), 4);
        await phone.Payments.MarkPaidAsync(ramesh.Id, PrevSun, new DateOnly(2026, 11, 21));
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 16), 3); // was 4: paid ₹500 too much
        await phone.EnterAsync(ramesh, Phone.Today, 2);

        var thisWeek = (await phone.Payments.WeekAsync(Phone.Today)).Value!.Rows[0].Payment;
        Assert.Equal(Money.FromRupees(-500), thisWeek.Adjustment);
        Assert.Equal(Money.FromRupees(500), thisWeek.Total);
    }

    [Fact]
    public async Task Adjustment_goes_to_the_oldest_unpaid_week_only()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 100, from: new DateOnly(2026, 11, 1));
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 2), 1);  // week of 1 Nov: paid
        await phone.Payments.MarkPaidAsync(ramesh.Id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7));
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 2), 2);  // +₹100 correction
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 9), 1);  // week of 8 Nov: unpaid
        await phone.EnterAsync(ramesh, Phone.Today, 1);                // this week: unpaid

        var older = (await phone.Payments.WeekAsync(new DateOnly(2026, 11, 9))).Value!.Rows[0].Payment;
        var current = (await phone.Payments.WeekAsync(Phone.Today)).Value!.Rows[0].Payment;
        Assert.Equal(Money.FromRupees(100), older.Adjustment);
        Assert.Equal(Money.Zero, current.Adjustment);
    }

    [Fact]
    public async Task Mark_whole_week_undo_and_due()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        var suma = await phone.AddAsync("Suma", 400);
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 16), 2);
        await phone.EnterAsync(suma, new DateOnly(2026, 11, 17), 1);
        await phone.EnterAsync(ramesh, Phone.Today, 5); // this week: payday not yet reached

        var due = (await phone.Payments.DueAsync()).Value!;
        Assert.Equal(Money.FromRupees(1400), due.Total);
        Assert.Equal(2, due.Count);
        Assert.Equal(PrevSun, due.OldestWeekStart);

        Assert.Equal(2, (await phone.Payments.MarkWeekPaidAsync(PrevSun, new DateOnly(2026, 11, 21))).Value);
        Assert.Equal(0, (await phone.Payments.DueAsync()).Value!.Count);

        Assert.True((await phone.Payments.UnmarkAsync(suma.Id, PrevSun)).Ok);
        Assert.Equal(Money.FromRupees(400), (await phone.Payments.DueAsync()).Value!.Total);
        Assert.Equal(ErrorCode.NotPaid, (await phone.Payments.UnmarkAsync(suma.Id, PrevSun)).Error?.Code);
    }

    [Fact]
    public async Task Paid_date_and_nothing_to_pay_are_checked()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);

        Assert.Equal(ErrorCode.NothingToPay, (await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Phone.Today)).Error?.Code);
        await phone.EnterAsync(ramesh, Phone.Today, 1);
        Assert.Equal(ErrorCode.PaidDateInvalid, (await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Phone.Today.AddDays(1))).Error?.Code);
        Assert.Equal(ErrorCode.PaidDateInvalid, (await phone.Payments.MarkPaidAsync(ramesh.Id, Sun, Sun.AddDays(-1))).Error?.Code);
    }

    [Fact]
    public async Task History_lists_recent_weeks_newest_first()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 16), 2);
        await phone.Payments.MarkPaidAsync(ramesh.Id, PrevSun, new DateOnly(2026, 11, 21));
        await phone.EnterAsync(ramesh, Phone.Today, 1);

        var history = (await phone.Payments.HistoryAsync(ramesh.Id, 3)).Value!;
        Assert.Equal([Sun, PrevSun, new DateOnly(2026, 11, 8)], history.Select(h => h.Week.Start));
        Assert.Equal([PaymentStatus.Pending, PaymentStatus.Paid, PaymentStatus.Nothing], history.Select(h => h.Status));
    }

    [Fact]
    public async Task Monthly_and_yearly_reports()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 2), 2);
        await phone.EnterAsync(ramesh, Phone.Today, 3);

        var month = (await phone.Reports.MonthlyAsync(Phone.Today)).Value!;
        Assert.Equal(5, month.TotalSarees);
        Assert.Empty(month.Days); // no daily columns for long periods
        var year = (await phone.Reports.YearlyAsync(2026)).Value!;
        Assert.Equal(Money.FromRupees(2500), year.TotalEarnings);
    }
}

public class DataExchangeTests
{
    private const string Version = "0.2.0";

    private static async Task<(Phone Phone, Employee Ramesh)> FactoryWithHistoryAsync()
    {
        var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        await phone.AddAsync("Suma", 450);
        await phone.Employees.ChangeRateAsync(ramesh.Id, Money.FromRupees(550), Phone.Today);
        await phone.EnterAsync(ramesh, new DateOnly(2026, 11, 16), 2);
        await phone.Payments.MarkPaidAsync(ramesh.Id, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 21));
        await phone.EnterAsync(ramesh, Phone.Today, 3);
        return (phone, ramesh);
    }

    [Fact]
    public async Task Export_then_import_on_a_new_phone_restores_everything_except_pins()
    {
        var (oldPhone, ramesh) = await FactoryWithHistoryAsync();
        using var _ = oldPhone;
        var file = (await oldPhone.Data.ExportAsync(Version)).Value!;
        Assert.DoesNotContain("pin", file, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"kind\": \"handloom-factory-data\"", file, StringComparison.Ordinal);

        // A new phone: set up (as the app requires an owner to import), then import.
        using var newPhone = new Phone();
        await newPhone.Accounts.SetUpFactoryAsync("Temp", "Temp", "1111", "1111");
        var preview = newPhone.Data.Preview(file);
        Assert.True(preview.Ok);
        Assert.Equal(2, preview.Value!.Workers);
        Assert.Equal(2, preview.Value.Entries);
        Assert.Equal(1, preview.Value.Payments);

        Assert.True((await newPhone.Data.ImportAsync(preview.Value.Data)).Ok);
        Assert.False(newPhone.Session.IsSignedIn);

        // The imported owner has no PIN on this phone yet; the temporary owner is gone.
        var owner = await newPhone.Accounts.PersonNeedingPinAsync();
        Assert.Equal("Santhosh", owner?.FullName);
        Assert.Empty(await newPhone.Accounts.LoginChoicesAsync());
        Assert.True((await newPhone.Accounts.SetFirstPinAsync("2468", "2468")).Ok);
        Assert.Null(await newPhone.Accounts.PersonNeedingPinAsync());

        // Everything is back: workers, rate history, entries, payments, audit.
        Assert.Equal(["Ramesh", "Suma"], (await newPhone.Employees.ListAsync()).Value!.Select(i => i.Employee.FullName));
        var detail = (await newPhone.Employees.GetAsync(ramesh.Id)).Value!;
        Assert.Equal(2, detail.RateHistory.Count);
        var lastWeek = (await newPhone.Payments.WeekAsync(new DateOnly(2026, 11, 16))).Value!.Rows.Single(r => r.Employee.Id == ramesh.Id).Payment;
        Assert.Equal(PaymentStatus.Paid, lastWeek.Status);
        Assert.Equal(Money.FromRupees(1000), lastWeek.Total);
        Assert.Equal((await oldPhone.Reports.WeeklyAsync(Phone.Today)).Value!.TotalEarnings, (await newPhone.Reports.WeeklyAsync(Phone.Today)).Value!.TotalEarnings);
        var actions = (await newPhone.Store.GetAuditAsync(100)).Select(a => a.Action).ToList();
        Assert.Contains(AuditActions.PaymentMarked, actions);
        Assert.Contains(AuditActions.DataImported, actions);
    }

    [Fact]
    public async Task Restore_on_a_phone_that_is_not_set_up()
    {
        var (oldPhone, _) = await FactoryWithHistoryAsync();
        using var _1 = oldPhone;
        var file = (await oldPhone.Data.ExportAsync(Version)).Value!;

        using var fresh = new Phone();
        var preview = fresh.Data.Preview(file);
        Assert.True(preview.Ok);
        Assert.True((await fresh.Data.ImportAsync(preview.Value!.Data)).Ok);
        Assert.True(await fresh.Accounts.IsSetUpAsync());
        Assert.NotNull(await fresh.Accounts.PersonNeedingPinAsync());

        // Once set up, a phone can't be overwritten by someone who isn't logged in as the owner.
        Assert.Equal(ErrorCode.NotAllowed, (await fresh.Data.ImportAsync(preview.Value.Data)).Error?.Code);
    }

    [Fact]
    public async Task Reimport_on_the_same_phone_keeps_the_owner_pin()
    {
        var (phone, _) = await FactoryWithHistoryAsync();
        using var __ = phone;
        var file = (await phone.Data.ExportAsync(Version)).Value!;
        var owner = phone.Owner;

        await phone.EnterAsync((await phone.Store.GetEmployeesAsync()).Single(e => e.FullName == "Suma"), Phone.Today, 9); // changed after export
        Assert.True((await phone.Data.ImportAsync(phone.Data.Preview(file).Value!.Data)).Ok);

        Assert.Null(await phone.Accounts.PersonNeedingPinAsync());
        Assert.True((await phone.Accounts.LoginAsync(owner.Id, "1234")).Success);
        Assert.Equal(3, (await phone.Reports.WeeklyAsync(Phone.Today)).Value!.TotalSarees); // Suma's 9 is gone again
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"something\": 1}")]
    [InlineData("{\"kind\": \"handloom-factory-data\", \"formatVersion\": 1}")]
    public async Task Bad_files_are_refused_and_change_nothing(string text)
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        await phone.AddAsync("Ramesh", 500);

        Assert.Equal(ErrorCode.ImportInvalid, phone.Data.Preview(text).Error?.Code);
        Assert.Single((await phone.Employees.ListAsync()).Value!);
    }

    [Fact]
    public async Task Files_from_a_newer_app_are_refused()
    {
        var (phone, _) = await FactoryWithHistoryAsync();
        using var __ = phone;
        var file = (await phone.Data.ExportAsync(Version)).Value!.Replace("\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal);
        Assert.Equal(ErrorCode.ImportTooNew, phone.Data.Preview(file).Error?.Code);
    }

    [Fact]
    public async Task Workers_cannot_export_or_import()
    {
        var (phone, ramesh) = await FactoryWithHistoryAsync();
        using var _ = phone;
        phone.Session.SignIn(ramesh);
        Assert.Equal(ErrorCode.NotAllowed, (await phone.Data.ExportAsync(Version)).Error?.Code);
    }
}

public class AuditTests
{
    [Fact]
    public async Task Change_history_names_who_did_what_newest_first_owner_only()
    {
        using var phone = new Phone();
        await phone.SetUpAsync();
        var ramesh = await phone.AddAsync("Ramesh", 500);
        phone.Clock.Advance(TimeSpan.FromMinutes(1));
        await phone.EnterAsync(ramesh, Phone.Today, 3);

        var audit = new AuditService(phone.Context);
        var lines = (await audit.RecentAsync()).Value!;
        Assert.Equal(AuditActions.EntrySaved, lines[0].Action);
        Assert.Equal("Santhosh", lines[0].Who);
        Assert.Contains("Ramesh 2026-11-23: 3", lines[0].After, StringComparison.Ordinal);
        Assert.Equal(AuditActions.FactorySetUp, lines[^1].Action);

        phone.Session.SignIn(ramesh);
        Assert.Equal(ErrorCode.NotAllowed, (await audit.RecentAsync()).Error?.Code);
    }
}
