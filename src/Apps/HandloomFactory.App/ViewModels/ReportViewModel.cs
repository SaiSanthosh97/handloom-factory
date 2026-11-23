using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Reports;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

public enum ReportMode
{
    Week,
    Month,
    Year,
}

public sealed record DayCell(string Weekday, string Count);

/// <summary>One worker's card: name, sarees, rate, earnings; in week mode also the seven days and the payment.</summary>
public sealed record ReportCard(
    string EmployeeId,
    string Name,
    string Sarees,
    string Rate,
    string Earnings,
    IReadOnlyList<DayCell> Days,
    string PaymentText,
    string PaymentNote,
    bool IsPaid,
    bool CanMarkPaid,
    bool CanUndo,
    string Amount)
{
    public bool HasDays => Days.Count > 0;

    public bool HasPayment => PaymentText.Length > 0;

    public bool HasPaymentNote => PaymentNote.Length > 0;
}

/// <summary>
/// Reports (Week / Month / Year) and weekly payments (D7, D11). The week view shows each worker's days, what is to
/// pay (including corrections to earlier paid weeks) and lets the owner mark it paid with a paid date.
/// </summary>
public sealed partial class ReportViewModel(ReportService reports, PaymentService payments, Dialogs dialogs) : ObservableObject
{
    private DateOnly _day;
    private DateOnly _today;
    private DateRange _week;

    public ObservableCollection<ReportCard> Cards { get; } = [];

    [ObservableProperty]
    public partial ReportMode Mode { get; set; } = ReportMode.Week;

    [ObservableProperty]
    public partial string PeriodText { get; set; } = "";

    [ObservableProperty]
    public partial string SubText { get; set; } = "";

    [ObservableProperty]
    public partial string TotalsText { get; set; } = "";

    [ObservableProperty]
    public partial string PaymentTotalsText { get; set; } = "";

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsWeek { get; set; } = true;

    [ObservableProperty]
    public partial bool IsMonth { get; set; }

    [ObservableProperty]
    public partial bool IsYear { get; set; }

    [ObservableProperty]
    public partial bool CanMarkAll { get; set; }

    [ObservableProperty]
    public partial DateTime? PaidDate { get; set; }

    [ObservableProperty]
    public partial DateTime MaxPaidDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial DateTime MinPaidDate { get; set; } = DateTime.Today.AddDays(-6);

    /// <summary>Opens the week view at a given week (from the dashboard's "Wages to pay").</summary>
    public void ShowWeek(DateOnly day)
    {
        Mode = ReportMode.Week;
        _day = day;
    }

    public async Task LoadAsync()
    {
        _today = await reports.TodayAsync();
        if (_day == default)
        {
            _day = _today;
        }

        IsWeek = Mode == ReportMode.Week;
        IsMonth = Mode == ReportMode.Month;
        IsYear = Mode == ReportMode.Year;
        var result = Mode switch
        {
            ReportMode.Month => await reports.MonthlyAsync(_day),
            ReportMode.Year => await reports.YearlyAsync(_day.Year),
            _ => await reports.WeeklyAsync(_day),
        };
        if (!result.Ok)
        {
            return;
        }

        var report = result.Value!;
        WeekPayments? week = null;
        if (Mode == ReportMode.Week)
        {
            week = (await payments.WeekAsync(_day)).Value;
            _week = report.Period;
            MinPaidDate = _week.Start.ToDateTime(TimeOnly.MinValue);
            MaxPaidDate = (_today < _week.End ? _today : _week.End).ToDateTime(TimeOnly.MinValue);
            if (MaxPaidDate < MinPaidDate)
            {
                MaxPaidDate = MinPaidDate;
            }

            // Default paid date: payday (Saturday), or today if payday hasn't come yet.
            PaidDate = MaxPaidDate;
        }

        Display(report, week);
    }

    [RelayCommand]
    private Task SetModeAsync(string mode)
    {
        Mode = Enum.Parse<ReportMode>(mode);
        return LoadAsync();
    }

    [RelayCommand]
    private Task PreviousAsync()
    {
        _day = Mode switch
        {
            ReportMode.Month => _day.AddMonths(-1),
            ReportMode.Year => _day.AddYears(-1),
            _ => _day.AddDays(-7),
        };
        return LoadAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        _day = Mode switch
        {
            ReportMode.Month => _day.AddMonths(1),
            ReportMode.Year => _day.AddYears(1),
            _ => _day.AddDays(7),
        };
        return LoadAsync();
    }

    [RelayCommand]
    private Task CurrentAsync()
    {
        _day = _today;
        return LoadAsync();
    }

    [RelayCommand]
    private async Task MarkPaidAsync(ReportCard card)
    {
        var paidOn = DateOnly.FromDateTime(PaidDate ?? DateTime.Today);
        if (!await dialogs.ConfirmAsync(AppResources.Report_ConfirmPaidTitle, Show.Format(AppResources.Report_ConfirmPaid, card.Amount, card.Name, Show.Day(paidOn)), AppResources.Report_MarkPaid))
        {
            return;
        }

        var result = await payments.MarkPaidAsync(card.EmployeeId, _week.Start, paidOn);
        await AfterAsync(result);
    }

    [RelayCommand]
    private async Task MarkAllPaidAsync()
    {
        var paidOn = DateOnly.FromDateTime(PaidDate ?? DateTime.Today);
        var week = (await payments.WeekAsync(_week.Start)).Value;
        if (week is null || !await dialogs.ConfirmAsync(AppResources.Report_ConfirmPaidTitle, Show.Format(AppResources.Report_ConfirmAllPaid, week.PendingTotal, Show.Day(paidOn)), AppResources.Report_MarkAllPaid))
        {
            return;
        }

        var result = await payments.MarkWeekPaidAsync(_week.Start, paidOn);
        await AfterAsync(result);
    }

    [RelayCommand]
    private async Task UndoPaidAsync(ReportCard card)
    {
        if (!await dialogs.ConfirmAsync(AppResources.Report_ConfirmUndoTitle, Show.Format(AppResources.Report_ConfirmUndo, card.Name), AppResources.Report_Undo))
        {
            return;
        }

        await AfterAsync(await payments.UnmarkAsync(card.EmployeeId, _week.Start));
    }

    private async Task AfterAsync(Result result)
    {
        if (!result.Ok)
        {
            await dialogs.ErrorAsync(result.Error);
            return;
        }

        Dialogs.Toast(AppResources.Report_MarkedPaid);
        await LoadAsync();
    }

    private void Display(PeriodReport report, WeekPayments? week)
    {
        PeriodText = Mode switch
        {
            ReportMode.Month => Show.Month(report.Period.Start),
            ReportMode.Year => report.Period.Start.Year.ToString(CultureInfo.CurrentCulture),
            _ => Show.Range(report.Period),
        };
        SubText = Mode == ReportMode.Week ? Show.Format(AppResources.Report_PayDay, Show.DayWithYear(report.Period.End)) : "";
        TotalsText = Show.Format(AppResources.Report_Totals, report.TotalSarees, report.TotalEarnings);
        PaymentTotalsText = week is null ? "" : Show.Format(AppResources.Report_WeekTotals, week.PendingTotal, week.PaidTotal);
        CanGoNext = report.Period.End < _today;
        CanMarkAll = week?.Rows.Count(r => r.Payment.Status == PaymentStatus.Pending) > 1;

        var lines = week?.Rows.ToDictionary(r => r.Employee.Id, r => r.Payment) ?? [];
        Cards.Clear();
        foreach (var row in report.Rows)
        {
            var days = report.Days.Select((d, i) => new DayCell(Show.ShortWeekday(d), row.DailyCounts[i]?.ToString(CultureInfo.InvariantCulture) ?? "–")).ToList();
            var rate = row.RateVaries ? AppResources.Report_Varies : row.Rate is { } r ? Show.PerSaree(r) : AppResources.Entry_NoRate;
            var (text, note, paid, canPay, canUndo) = lines.TryGetValue(row.EmployeeId, out var line) ? PaymentLine(line) : ("", "", false, false, false);
            Cards.Add(new ReportCard(row.EmployeeId, row.Name, Show.Sarees(row.Sarees), rate, row.Earnings.ToString(), days, text, note, paid, canPay, canUndo, line?.Total.ToString() ?? ""));
        }

        IsEmpty = Cards.Count == 0;
    }

    private static (string Text, string Note, bool Paid, bool CanPay, bool CanUndo) PaymentLine(WeekPayment line) => line.Status switch
    {
        PaymentStatus.Paid => (
            Show.Format(AppResources.Report_PaidOn, line.Total, Show.Day(line.PaidDate!.Value)),
            line.CorrectedSincePaid != Money.Zero ? Show.Format(AppResources.Report_CorrectedSincePaid, Signed(line.CorrectedSincePaid)) : "",
            true,
            false,
            true),
        PaymentStatus.Pending => (
            Show.Format(AppResources.Report_Pending, line.Total),
            line.Adjustment != Money.Zero ? Show.Format(AppResources.Report_Adjustment, Signed(line.Adjustment)) : "",
            false,
            true,
            false),
        _ => (AppResources.Report_NothingToPay, "", false, false, false),
    };

    private static string Signed(Money amount) => amount.IsNegative ? amount.ToString() : "+" + amount;
}
