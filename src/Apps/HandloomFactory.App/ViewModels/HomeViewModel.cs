using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

/// <summary>The owner's dashboard: today, this pay week, this month, who hasn't been entered yet, update banner.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ReportService _reports;
    private readonly UpdateService _updates;
    private readonly Dialogs _dialogs;
    private readonly PaymentService _payments;
    private readonly ReportViewModel _reportPage;
    private DateOnly? _oldestDue;

    public HomeViewModel(AppHost host, ReportService reports, UpdateService updates, Dialogs dialogs, PaymentService payments, ReportViewModel reportPage)
    {
        _host = host;
        _reports = reports;
        _updates = updates;
        _dialogs = dialogs;
        _payments = payments;
        _reportPage = reportPage;
        updates.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshUpdateBanner);
    }

    [ObservableProperty]
    public partial string Greeting { get; set; } = "";

    [ObservableProperty]
    public partial string TodayTitle { get; set; } = "";

    [ObservableProperty]
    public partial string TodaySarees { get; set; } = "";

    [ObservableProperty]
    public partial string MissingText { get; set; } = "";

    [ObservableProperty]
    public partial bool AllEntered { get; set; }

    [ObservableProperty]
    public partial string WeekRange { get; set; } = "";

    [ObservableProperty]
    public partial string WeekSarees { get; set; } = "";

    [ObservableProperty]
    public partial string WeekWages { get; set; } = "";

    [ObservableProperty]
    public partial string PayDayText { get; set; } = "";

    [ObservableProperty]
    public partial string MonthName { get; set; } = "";

    [ObservableProperty]
    public partial string MonthSarees { get; set; } = "";

    [ObservableProperty]
    public partial string MonthWages { get; set; } = "";

    [ObservableProperty]
    public partial string WorkersText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasWorkers { get; set; } = true;

    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = "";

    public async Task LoadAsync()
    {
        RefreshUpdateBanner();
        var result = await _reports.DashboardAsync();
        if (!result.Ok)
        {
            return;
        }

        var d = result.Value!;
        Greeting = Show.Format(AppResources.Home_Hello, _host.Session.User?.FullName ?? "");
        TodayTitle = $"{AppResources.Home_Today} · {Show.Day(d.Today)}";
        TodaySarees = Show.Sarees(d.TodaySarees);
        HasWorkers = d.ActiveEmployees > 0;
        AllEntered = HasWorkers && d.NotEnteredToday.Count == 0;
        MissingText = d.NotEnteredToday.Count == 0 ? "" : Show.Format(AppResources.Home_NotEntered, string.Join(", ", d.NotEnteredToday));
        WeekRange = Show.Range(d.Week);
        WeekSarees = Show.Sarees(d.WeekSarees);
        WeekWages = Show.Format(AppResources.Home_Wages, d.WeekWages);
        PayDayText = Show.Format(AppResources.Home_PayDay, Show.Day(d.Week.End));
        MonthName = Show.Month(d.Today);
        MonthSarees = Show.Sarees(d.MonthSarees);
        MonthWages = Show.Format(AppResources.Home_Wages, d.MonthWages);
        WorkersText = Show.Format(AppResources.Home_ActiveWorkers, d.ActiveEmployees);

        var due = (await _payments.DueAsync()).Value;
        HasDue = due is { Count: > 0 };
        DueText = due is null ? "" : Show.Format(AppResources.Home_DueText, due.Total, due.Count);
        _oldestDue = due?.OldestWeekStart;
    }

    [ObservableProperty]
    public partial bool HasDue { get; set; }

    [ObservableProperty]
    public partial string DueText { get; set; } = "";

    /// <summary>Opens the payments (week report) at the oldest unpaid week.</summary>
    [RelayCommand]
    private Task OpenDueAsync()
    {
        if (_oldestDue is { } week)
        {
            _reportPage.ShowWeek(week);
        }

        return Nav.GoAsync("//main/reports");
    }

    private void RefreshUpdateBanner()
    {
        HasUpdate = _updates.Available is not null;
        UpdateText = _updates.Available is { } u ? Show.Format(AppResources.Update_Banner, u.Version.ToString(3)) : "";
    }

    [RelayCommand]
    private Task EnterTodayAsync() => Nav.GoAsync("//main/entry");

    [RelayCommand]
    private Task AddWorkersAsync() => Nav.GoAsync("//main/workers");

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_updates.Available is { } update)
        {
            await _updates.InstallAsync(update);
        }
        else
        {
            await _dialogs.AlertAsync(AppResources.Update_Title, Show.Format(AppResources.Update_UpToDate, UpdateService.CurrentVersion.ToString(3)));
        }
    }
}
