using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

/// <summary>One worker's box on the day screen.</summary>
public sealed partial class EntryRow : ObservableObject
{
    public EntryRow(DaySheetRow row, Action changed)
    {
        _changed = changed;
        Employee = row.Employee;
        Saved = row.SareeCount;
        Text = row.SareeCount?.ToString(CultureInfo.InvariantCulture) ?? "";
        HasRate = row.Rate is not null;
        Note = row.Rate is { } rate
            ? Show.PerSaree(rate) + (row.EnteredBy == EnteredBy.Employee ? " · " + AppResources.Entry_ByWorker : "") + (row.WeekPaid ? " · " + AppResources.Entry_WeekPaid : "")
            : AppResources.Entry_NoRate;
    }

    private readonly Action _changed;

    public Employee Employee { get; }

    public int? Saved { get; }

    public string Name => Employee.IsActive ? Employee.FullName : $"{Employee.FullName} {AppResources.Entry_Disabled}";

    public string Note { get; }

    public bool HasRate { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    partial void OnTextChanged(string value) => _changed();

    /// <summary>The typed number; null for an empty box. False when the text isn't a whole number.</summary>
    public bool TryRead(out int? count)
    {
        count = null;
        var text = Text.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            count = n;
            return true;
        }

        return false;
    }

    public bool IsDirty => !TryRead(out var count) || count != Saved;
}

/// <summary>
/// The owner enters a whole day at once: every active worker with a number box (D9: any day up to today).
/// Defaults to today; arrows move a day back or forward.
/// </summary>
public sealed partial class EntryViewModel(ProductionService production, Dialogs dialogs) : ObservableObject
{
    public ObservableCollection<EntryRow> Rows { get; } = [];

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial DateTime MaxDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string DateTitle { get; set; } = "";

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasRows { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    private DateOnly _loaded;
    private DateOnly _today;
    private bool _loading;

    public bool IsDirty => Rows.Any(r => r.IsDirty);

    /// <summary>Opens today (or reloads the current day when coming back to the tab).</summary>
    public async Task LoadAsync(DateOnly? day = null)
    {
        _loading = true;
        try
        {
            var target = day ?? (_loaded == default ? (DateOnly?)null : _loaded);
            var result = await production.GetDayAsync(target ?? DateOnly.FromDateTime(DateTime.Today));
            if (!result.Ok)
            {
                return;
            }

            var sheet = result.Value!;
            if (target is null && sheet.Date != sheet.Today)
            {
                result = await production.GetDayAsync(sheet.Today); // the factory's "today", not the phone's
                sheet = result.Value!;
            }

            _today = sheet.Today;
            _loaded = sheet.Date;
            MaxDate = sheet.Today.ToDateTime(TimeOnly.MinValue);
            Date = sheet.Date.ToDateTime(TimeOnly.MinValue);
            DateTitle = sheet.IsToday ? $"{AppResources.Entry_Today} · {Show.DayWithYear(sheet.Date)}" : Show.DayWithYear(sheet.Date);
            CanGoNext = sheet.Date < sheet.Today;
            Rows.Clear();
            foreach (var row in sheet.Rows)
            {
                Rows.Add(new EntryRow(row, UpdateTotal));
            }

            HasRows = Rows.Count > 0;
            UpdateTotal();
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnDateChanged(DateTime? value)
    {
        if (!_loading && value is { } picked && DateOnly.FromDateTime(picked) != _loaded)
        {
            _ = MoveToAsync(DateOnly.FromDateTime(picked));
        }
    }

    [RelayCommand]
    private Task PreviousAsync() => MoveToAsync(_loaded.AddDays(-1));

    [RelayCommand]
    private Task NextAsync() => _loaded < _today ? MoveToAsync(_loaded.AddDays(1)) : Task.CompletedTask;

    [RelayCommand]
    private Task TodayAsync() => MoveToAsync(_today);

    /// <summary>Asks before throwing away typed numbers.</summary>
    public async Task<bool> ConfirmLeaveAsync() =>
        !IsDirty || await dialogs.ConfirmAsync(AppResources.Entry_DiscardTitle, AppResources.Entry_DiscardText, AppResources.Entry_Discard);

    private async Task MoveToAsync(DateOnly day)
    {
        if (!await ConfirmLeaveAsync())
        {
            _loading = true;
            Date = _loaded.ToDateTime(TimeOnly.MinValue);
            _loading = false;
            return;
        }

        await LoadAsync(day);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var counts = new Dictionary<string, int?>();
        foreach (var row in Rows)
        {
            if (!row.TryRead(out var count))
            {
                await dialogs.AlertAsync(AppResources.Common_Check, Show.Format(AppResources.Entry_NotANumber, row.Employee.FullName));
                return;
            }

            if (count != row.Saved)
            {
                counts[row.Employee.Id] = count;
            }
        }

        if (counts.Count == 0)
        {
            Dialogs.Toast(AppResources.Entry_NothingChanged);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await production.SaveDayAsync(_loaded, counts);
            if (!result.Ok)
            {
                await dialogs.ErrorAsync(result.Error);
                return;
            }

            Dialogs.Toast(AppResources.Entry_Saved);
            await LoadAsync(_loaded);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateTotal() =>
        TotalText = Show.Format(AppResources.Entry_Total, Rows.Sum(r => r.TryRead(out var c) ? c ?? 0 : 0));
}
