namespace HandloomFactory.Core.Common;

/// <summary>An inclusive range of days.</summary>
public readonly record struct DateRange(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly day) => day >= Start && day <= End;

    public int DayCount => End.DayNumber - Start.DayNumber + 1;

    public IEnumerable<DateOnly> Days()
    {
        for (var day = Start; day <= End; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}

/// <summary>
/// Pay periods (D7). The factory works Monday–Saturday and pays on Saturday at the end of the day; Sunday is a
/// holiday but may be worked. So a <b>pay week runs Sunday to Saturday</b>: Saturday's payment covers the Sunday
/// before it plus Monday–Saturday. Months and years are calendar months and years (reports only).
/// </summary>
public static class Periods
{
    /// <summary>The Sunday–Saturday pay week containing the day.</summary>
    public static DateRange PayWeek(DateOnly day)
    {
        var start = day.AddDays(-(int)day.DayOfWeek); // DayOfWeek.Sunday == 0
        return new DateRange(start, start.AddDays(6));
    }

    /// <summary>The Saturday on which the week containing the day is paid.</summary>
    public static DateOnly PayDay(DateOnly day) => PayWeek(day).End;

    public static DateRange Month(DateOnly day)
    {
        var start = new DateOnly(day.Year, day.Month, 1);
        return new DateRange(start, start.AddMonths(1).AddDays(-1));
    }

    public static DateRange Year(int year) => new(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
}
