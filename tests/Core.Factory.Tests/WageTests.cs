using HandloomFactory.Core.Common;
using HandloomFactory.Core.Reports;
using HandloomFactory.Core.Security;

namespace HandloomFactory.Core.Factory.Tests;

public class WageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static Employee Worker(string id, string name, bool active = true) => new(id, "E" + id, name, null, Role.Employee, active, T0, T0);

    private static RateChange Rate(string employee, long rupees, DateOnly from) => new(RateChange.IdFor(employee, from), employee, Money.FromRupees(rupees), from, T0);

    private static ProductionEntry Entry(string employee, DateOnly day, int count) =>
        new(ProductionEntry.IdFor(employee, day), employee, day, count, EnteredBy.Employee, T0, T0);

    [Fact]
    public void Ramesh_example_ten_sarees_at_500_is_5000()
    {
        var rates = new RateBook([Rate("r", 500, D(11, 1))]);
        var entries = Enumerable.Range(0, 5).Select(i => Entry("r", D(11, 23).AddDays(i), 2)).ToList();

        var week = WageCalculator.SummarizeOne("r", entries, rates, Periods.PayWeek(D(11, 23)));

        Assert.Equal(10, week.Sarees);
        Assert.Equal(Money.FromRupees(5000), week.Earnings);
        Assert.Equal(Money.FromRupees(500), week.SingleRate);
    }

    [Fact]
    public void Rate_change_mid_week_applies_from_its_date_only()
    {
        var rates = new RateBook([Rate("r", 500, D(11, 1)), Rate("r", 550, D(11, 25))]);
        var entries = new[] { Entry("r", D(11, 24), 3), Entry("r", D(11, 25), 2) };

        var week = WageCalculator.SummarizeOne("r", entries, rates, Periods.PayWeek(D(11, 24)));

        Assert.Equal(Money.FromRupees((3 * 500) + (2 * 550)), week.Earnings);
        Assert.True(week.RateVaries);
        Assert.Null(week.SingleRate);
        Assert.Equal(Money.FromRupees(500), rates.RateOn("r", D(11, 24)));
        Assert.Null(rates.RateOn("r", D(10, 31)));
    }

    [Fact]
    public void Sunday_work_counts_toward_the_following_saturday()
    {
        var rates = new RateBook([Rate("r", 100, D(11, 1))]);
        var entries = new[] { Entry("r", D(11, 22), 1), Entry("r", D(11, 28), 1), Entry("r", D(11, 29), 5) };

        var week = WageCalculator.SummarizeOne("r", entries, rates, Periods.PayWeek(D(11, 28)));

        Assert.Equal(2, week.Sarees); // Sun 22 + Sat 28; Sun 29 is next week
    }

    [Theory]
    [InlineData(Role.Employee, 0, 3, ErrorCode.None)]
    [InlineData(Role.Employee, -1, 3, ErrorCode.NotAllowed)] // D9: worker can't do yesterday
    [InlineData(Role.Admin, -10, 3, ErrorCode.None)]
    [InlineData(Role.Admin, 1, 3, ErrorCode.FutureDate)]
    [InlineData(Role.Admin, 0, 51, ErrorCode.CountOutOfRange)]
    [InlineData(Role.Admin, 0, -1, ErrorCode.CountOutOfRange)]
    [InlineData(Role.Admin, 0, 0, ErrorCode.None)]
    public void Entry_rules(Role role, int dayOffset, int count, ErrorCode expected)
    {
        var today = D(11, 23);
        Assert.Equal(expected, EntryRules.Check(role, today.AddDays(dayOffset), count, today, 50, Money.FromRupees(500)));
    }

    [Fact]
    public void Entry_needs_a_rate_on_its_day() =>
        Assert.Equal(ErrorCode.NoRateOnDate, EntryRules.Check(Role.Admin, D(11, 20), 2, D(11, 23), 50, null));

    [Fact]
    public void Dashboard_totals_and_missing_today()
    {
        var employees = new[] { Worker("a", "Ramesh"), Worker("b", "Suma"), Worker("c", "Old", active: false) };
        var rates = new RateBook([Rate("a", 500, D(11, 1)), Rate("b", 400, D(11, 1)), Rate("c", 300, D(11, 1))]);
        var entries = new[]
        {
            Entry("a", D(11, 23), 3),
            Entry("a", D(11, 22), 1), // Sunday, same pay week
            Entry("b", D(11, 2), 10), // earlier this month
            Entry("c", D(11, 23), 2), // disabled worker still counts in totals
        };

        var dashboard = ReportBuilder.Dashboard(D(11, 23), employees, rates, entries);

        Assert.Equal(2, dashboard.ActiveEmployees);
        Assert.Equal(5, dashboard.TodaySarees);
        Assert.Equal(["Suma"], dashboard.NotEnteredToday);
        Assert.Equal(6, dashboard.WeekSarees);
        Assert.Equal(Money.FromRupees(2000 + 600), dashboard.WeekWages);
        Assert.Equal(16, dashboard.MonthSarees);
        Assert.Equal(Money.FromRupees(2000 + 4000 + 600), dashboard.MonthWages);
    }

    [Fact]
    public void Weekly_report_has_daily_columns_and_totals()
    {
        var employees = new[] { Worker("a", "Ramesh"), Worker("b", "Suma"), Worker("c", "Gone", active: false) };
        var rates = new RateBook([Rate("a", 500, D(11, 1)), Rate("b", 400, D(11, 1))]);
        var entries = new[] { Entry("a", D(11, 23), 3), Entry("a", D(11, 24), 2) };

        var report = ReportBuilder.Period(Periods.PayWeek(D(11, 23)), employees, rates, entries);

        Assert.Equal(7, report.Days.Count);
        Assert.Equal(["Ramesh", "Suma"], report.Rows.Select(r => r.Name)); // disabled without entries is hidden
        var ramesh = report.Rows[0];
        Assert.Equal([null, 3, 2, null, null, null, null], ramesh.DailyCounts);
        Assert.Equal(Money.FromRupees(2500), ramesh.Earnings);
        Assert.Equal(5, report.TotalSarees);
        Assert.Equal(Money.FromRupees(2500), report.TotalEarnings);
        Assert.Equal(0, report.Rows[1].Sarees);
    }

    [Fact]
    public void Employee_codes_increase()
    {
        Assert.Equal("E001", Employee.NextCode([]));
        Assert.Equal("E004", Employee.NextCode(["E001", "E003", "A001"]));
    }
}

public class PinTests
{
    [Fact]
    public void Correct_pin_verifies_and_wrong_pin_does_not()
    {
        var hash = Pins.Hash("1234", iterations: 1000);
        Assert.True(Pins.Verify("1234", hash));
        Assert.False(Pins.Verify("1235", hash));
        Assert.False(Pins.Verify("12345", hash));
        Assert.NotEqual(Pins.Hash("1234", 1000).Salt, hash.Salt);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("0000", true)]
    [InlineData("123", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)] // only ASCII digits
    public void Pins_are_four_ascii_digits(string pin, bool ok) => Assert.Equal(ok, Pins.IsWellFormed(pin));

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 120)]
    [InlineData(20, 900)]
    public void Lockout_doubles_after_five_failures(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PinLockout.WaitAfter(failures));
}
