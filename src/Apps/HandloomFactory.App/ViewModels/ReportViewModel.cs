using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Reports;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

public sealed record DayCell(string Weekday, string Count);

/// <summary>One worker's card in the weekly report: name, total, rate, earnings and the seven days.</summary>
public sealed record ReportCard(string Name, string Sarees, string Rate, string Earnings, IReadOnlyList<DayCell> Days);

/// <summary>Weekly report (Sunday–Saturday, paid Saturday). Arrows move a week back or forward.</summary>
public sealed partial class ReportViewModel(ReportService reports) : ObservableObject
{
    private DateOnly _weekDay;
    private DateOnly _today;

    public ObservableCollection<ReportCard> Cards { get; } = [];

    [ObservableProperty]
    public partial string WeekText { get; set; } = "";

    [ObservableProperty]
    public partial string PayDayText { get; set; } = "";

    [ObservableProperty]
    public partial string TotalsText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        _today = await reports.TodayAsync();
        if (_weekDay == default)
        {
            _weekDay = _today;
        }

        var result = await reports.WeeklyAsync(_weekDay);
        if (!result.Ok)
        {
            return;
        }

        Display(result.Value!);
    }

    [RelayCommand]
    private Task PreviousAsync()
    {
        _weekDay = _weekDay.AddDays(-7);
        return LoadAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        _weekDay = _weekDay.AddDays(7);
        return LoadAsync();
    }

    [RelayCommand]
    private Task ThisWeekAsync()
    {
        _weekDay = _today;
        return LoadAsync();
    }

    private void Display(PeriodReport report)
    {
        WeekText = Show.Range(report.Period);
        PayDayText = Show.Format(AppResources.Report_PayDay, Show.DayWithYear(report.Period.End));
        TotalsText = Show.Format(AppResources.Report_Totals, report.TotalSarees, report.TotalEarnings);
        CanGoNext = report.Period.End < _today;
        Cards.Clear();
        foreach (var row in report.Rows)
        {
            var days = report.Days.Select((d, i) => new DayCell(Show.ShortWeekday(d), row.DailyCounts[i]?.ToString(CultureInfo.InvariantCulture) ?? "–")).ToList();
            var rate = row.RateVaries ? AppResources.Report_Varies : row.Rate is { } r ? Show.PerSaree(r) : AppResources.Entry_NoRate;
            Cards.Add(new ReportCard(row.Name, Show.Sarees(row.Sarees), rate, row.Earnings.ToString(), days));
        }

        IsEmpty = Cards.Count == 0;
    }
}
