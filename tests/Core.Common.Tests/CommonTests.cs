using HandloomFactory.Core.Common;

namespace HandloomFactory.Core.Common.Tests;

public class PeriodsTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-11-22", "2026-11-22", "2026-11-28")] // Sunday starts the pay week
    [InlineData("2026-11-23", "2026-11-22", "2026-11-28")] // Monday
    [InlineData("2026-11-28", "2026-11-22", "2026-11-28")] // Saturday (payday) is included
    [InlineData("2026-11-29", "2026-11-29", "2026-12-05")] // next Sunday belongs to the next payment
    [InlineData("2027-01-01", "2026-12-27", "2027-01-02")] // crosses the year
    public void Pay_week_runs_Sunday_to_Saturday(string day, string start, string end)
    {
        var week = Periods.PayWeek(D(day));
        Assert.Equal(D(start), week.Start);
        Assert.Equal(D(end), week.End);
        Assert.Equal(DayOfWeek.Saturday, Periods.PayDay(D(day)).DayOfWeek);
    }

    [Fact]
    public void Month_and_year_are_calendar_periods()
    {
        Assert.Equal(new DateRange(D("2028-02-01"), D("2028-02-29")), Periods.Month(D("2028-02-15")));
        Assert.Equal(new DateRange(D("2026-01-01"), D("2026-12-31")), Periods.Year(2026));
        Assert.Equal(7, Periods.PayWeek(D("2026-11-23")).Days().Count());
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData(0, "₹0")]
    [InlineData(50000, "₹500")]
    [InlineData(2100000, "₹21,000")]
    [InlineData(12345650, "₹1,23,456.50")]
    [InlineData(1234567800, "₹1,23,45,678")]
    [InlineData(-50050, "-₹500.50")]
    public void Formats_with_indian_grouping(long paise, string expected)
    {
        Assert.Equal(expected, new Money(paise).ToString());
    }

    [Theory]
    [InlineData("500", 50000)]
    [InlineData("500.5", 50050)]
    [InlineData(" ₹1,200.75 ", 120075)]
    [InlineData("0", 0)]
    public void Parses_typed_amounts(string text, long paise)
    {
        Assert.True(Money.TryParse(text, out var money));
        Assert.Equal(paise, money.Paise);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.234")]
    public void Rejects_bad_amounts(string text) => Assert.False(Money.TryParse(text, out _));

    [Fact]
    public void Arithmetic_is_exact()
    {
        var rate = new Money(45050); // ₹450.50
        Assert.Equal(new Money(450500), rate * 10);
        Assert.Equal("450.50", rate.ToPlainString());
        Assert.Equal("500", Money.FromRupees(500).ToPlainString());
    }
}

public class TimeAndIdsTests
{
    [Fact]
    public void Today_follows_the_factory_zone_not_UTC()
    {
        // 20:00 UTC on the 22nd is already 01:30 on the 23rd in India.
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero));
        var time = new FactoryTime(clock, FactoryTime.FindZone("Asia/Kolkata"));
        Assert.Equal(new DateOnly(2026, 11, 23), time.Today);
    }

    [Fact]
    public void Unknown_zone_falls_back_to_india()
    {
        var zone = FactoryTime.FindZone("Not/AZone");
        Assert.Equal(TimeSpan.FromHours(5.5), zone.BaseUtcOffset);
    }

    [Fact]
    public void Deterministic_ids_are_stable_and_distinct()
    {
        Assert.Equal(Ids.For("entry", "a", "2026-11-23"), Ids.For("entry", "a", "2026-11-23"));
        Assert.NotEqual(Ids.For("entry", "a", "2026-11-23"), Ids.For("entry", "a", "2026-11-24"));
        Assert.Equal(32, Ids.For("x").Length);
        Assert.NotEqual(Ids.New(), Ids.New());
    }
}
