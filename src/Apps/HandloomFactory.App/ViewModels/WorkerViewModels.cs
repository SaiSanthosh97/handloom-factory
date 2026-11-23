using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

public sealed record WorkerListItem(string Id, string Name, string Detail, bool IsActive);

/// <summary>The Workers tab: active workers first, then disabled ones.</summary>
public sealed partial class WorkersViewModel(EmployeeService employees) : ObservableObject
{
    public ObservableCollection<WorkerListItem> Workers { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public async Task LoadAsync()
    {
        var result = await employees.ListAsync();
        Workers.Clear();
        foreach (var item in result.Value ?? [])
        {
            var e = item.Employee;
            var rate = item.CurrentRate is { } r ? Show.PerSaree(r) : AppResources.Entry_NoRate;
            Workers.Add(new WorkerListItem(e.Id, e.FullName, e.IsActive ? $"{e.Code} · {rate}" : $"{e.Code} · {AppResources.Workers_Disabled}", e.IsActive));
        }

        IsEmpty = Workers.Count == 0;
    }

    [RelayCommand]
    private Task AddAsync() => Nav.GoAsync("worker");

    [RelayCommand]
    private Task OpenAsync(WorkerListItem item) => Nav.GoAsync($"worker?id={Uri.EscapeDataString(item.Id)}");
}

/// <summary>Add a worker, or view/edit one: name, phone, rate (with history), this week/month, disable/enable.</summary>
public sealed partial class WorkerViewModel(EmployeeService employees, ReportService reports, PaymentService payments, Dialogs dialogs) : ObservableObject, IQueryAttributable
{
    private string? _id;

    public ObservableCollection<string> RateHistory { get; } = [];

    /// <summary>The last 12 pay weeks, newest first: sarees, earnings and whether paid.</summary>
    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty]
    public partial bool IsNew { get; set; } = true;

    [ObservableProperty]
    public partial string Title { get; set; } = AppResources.Worker_NewTitle;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Phone { get; set; } = "";

    [ObservableProperty]
    public partial string RateText { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? RateFrom { get; set; }

    [ObservableProperty]
    public partial string CodeText { get; set; } = "";

    [ObservableProperty]
    public partial string TodayText { get; set; } = "";

    [ObservableProperty]
    public partial string WeekText { get; set; } = "";

    [ObservableProperty]
    public partial string MonthText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;

    [ObservableProperty]
    public partial string NewRateText { get; set; } = "";

    [ObservableProperty]
    public partial DateTime? NewRateFrom { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query) =>
        _id = query.TryGetValue("id", out var id) ? Uri.UnescapeDataString(id.ToString() ?? "") : null;

    public async Task LoadAsync()
    {
        var today = await reports.TodayAsync();
        if (_id is null)
        {
            IsNew = true;
            Title = AppResources.Worker_NewTitle;
            RateFrom = Periods.PayWeek(today).Start.ToDateTime(TimeOnly.MinValue); // so this week's days can be entered
            return;
        }

        var result = await employees.GetAsync(_id);
        if (!result.Ok)
        {
            await dialogs.ErrorAsync(result.Error);
            await Nav.BackAsync();
            return;
        }

        var d = result.Value!;
        IsNew = false;
        Title = d.Employee.FullName;
        Name = d.Employee.FullName;
        Phone = d.Employee.PhoneNumber ?? "";
        IsActive = d.Employee.IsActive;
        CodeText = Show.Format(AppResources.Worker_Code, d.Employee.Code);
        TodayText = Show.Format(AppResources.Worker_Today, d.Summary.TodaySarees is { } t ? Show.Sarees(t) : AppResources.Worker_NotEntered);
        WeekText = Show.Format(AppResources.Worker_ThisWeek, d.Summary.ThisWeek.Sarees, d.Summary.ThisWeek.Earnings);
        MonthText = Show.Format(AppResources.Worker_ThisMonth, d.Summary.ThisMonth.Sarees, d.Summary.ThisMonth.Earnings);
        NewRateText = d.Summary.CurrentRate?.ToPlainString() ?? "";
        NewRateFrom = today.ToDateTime(TimeOnly.MinValue);
        RateHistory.Clear();
        foreach (var change in d.RateHistory)
        {
            RateHistory.Add(Show.Format(AppResources.Worker_RateFromItem, change.Rate, Show.DayWithYear(change.EffectiveFrom)));
        }

        History.Clear();
        foreach (var week in (await payments.HistoryAsync(_id)).Value ?? [])
        {
            var state = week.Status switch
            {
                PaymentStatus.Paid => Show.Format(AppResources.Worker_HistoryPaid, week.Total, Show.Day(week.PaidDate!.Value)),
                PaymentStatus.Pending => Show.Format(AppResources.Worker_HistoryPending, week.Total),
                _ => AppResources.Worker_HistoryNothing,
            };
            var line = week.Status == PaymentStatus.Nothing
                ? Show.Format(AppResources.Worker_HistoryLine, Show.Range(week.Week), state)
                : Show.Format(AppResources.Worker_HistoryLine, Show.Range(week.Week), $"{Show.Sarees(week.Sarees)} · {state}");
            History.Add(line);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (IsNew)
            {
                if (!Money.TryParse(RateText, out var rate) || rate.Paise <= 0)
                {
                    await dialogs.ErrorAsync(new Error(ErrorCode.RateInvalid));
                    return;
                }

                var created = await employees.CreateAsync(Name, Phone, rate, DateOnly.FromDateTime(RateFrom ?? DateTime.Today));
                if (!created.Ok)
                {
                    await dialogs.ErrorAsync(created.Error);
                    return;
                }
            }
            else
            {
                var updated = await employees.UpdateAsync(_id!, Name, Phone);
                if (!updated.Ok)
                {
                    await dialogs.ErrorAsync(updated.Error);
                    return;
                }
            }

            Dialogs.Toast(AppResources.Worker_Saved);
            await Nav.BackAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangeRateAsync()
    {
        if (!Money.TryParse(NewRateText, out var rate) || rate.Paise <= 0)
        {
            await dialogs.ErrorAsync(new Error(ErrorCode.RateInvalid));
            return;
        }

        var result = await employees.ChangeRateAsync(_id!, rate, DateOnly.FromDateTime(NewRateFrom ?? DateTime.Today));
        if (!result.Ok)
        {
            await dialogs.ErrorAsync(result.Error);
            return;
        }

        Dialogs.Toast(AppResources.Worker_RateChanged);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (IsActive && !await dialogs.ConfirmAsync(Show.Format(AppResources.Worker_DisableTitle, Name), Show.Format(AppResources.Worker_DisableText, Name), AppResources.Worker_Disable))
        {
            return;
        }

        var result = await employees.SetActiveAsync(_id!, !IsActive);
        if (!result.Ok)
        {
            await dialogs.ErrorAsync(result.Error);
            return;
        }

        await LoadAsync();
    }
}
