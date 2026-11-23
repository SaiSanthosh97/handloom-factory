using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Sync;
using HandloomFactory.Core.Workflow;
using QRCoder;

namespace HandloomFactory.App.ViewModels;

/// <summary>
/// Owner: link a worker's phone (TD-03). Shows a one-time QR code (valid 15 minutes) that the worker's phone scans, or
/// the same code as text to send on WhatsApp. Checks every 3 seconds until the phone is linked.
/// </summary>
public sealed partial class LinkPhoneViewModel(SyncEngine sync, SyncScheduler scheduler, EmployeeService employees, Dialogs dialogs) : ObservableObject, IQueryAttributable
{
    private string _employeeId = "";
    private string _code = "";
    private CancellationTokenSource? _polling;

    [ObservableProperty]
    public partial string Title { get; set; } = AppResources.Link_Title;

    [ObservableProperty]
    public partial ImageSource? Qr { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLinked { get; set; }

    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query) =>
        _employeeId = query.TryGetValue("id", out var id) ? Uri.UnescapeDataString(id.ToString() ?? "") : "";

    public async Task StartAsync()
    {
        var worker = (await employees.GetAsync(_employeeId)).Value?.Employee;
        if (worker is null)
        {
            await Nav.BackAsync();
            return;
        }

        Title = Show.Format(AppResources.Link_TitleFor, worker.FullName);
        Invite invite;
        try
        {
            invite = await sync.CreateInviteAsync(_employeeId, NtfyRelayFactory.DefaultServer);
        }
        catch (InvalidOperationException)
        {
            await dialogs.ErrorAsync(new Error(ErrorCode.NotAllowed));
            await Nav.BackAsync();
            return;
        }

        _code = invite.ToText();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(_code, QRCodeGenerator.ECCLevel.L);
        var png = new PngByteQRCode(data).GetGraphic(8);
        Qr = ImageSource.FromStream(() => new MemoryStream(png));
        IsLinked = false;
        IsWaiting = true;
        Status = Show.Format(AppResources.Link_Waiting, worker.FullName);

        _polling?.Cancel();
        _polling = new CancellationTokenSource();
        _ = PollAsync(worker.FullName, invite.ExpiresAt, _polling.Token);
    }

    /// <summary>Leaving the screen before the phone linked cancels the unused invite.</summary>
    public async Task StopAsync()
    {
        _polling?.Cancel();
        if (!IsLinked)
        {
            await sync.CancelInviteAsync(_employeeId);
        }
    }

    [RelayCommand]
    private Task ShareCodeAsync() =>
        Share.Default.RequestAsync(new ShareTextRequest(Show.Format(AppResources.Link_ShareText, _code), AppResources.Link_Title));

    [RelayCommand]
    private Task DoneAsync() => Nav.BackAsync();

    private async Task PollAsync(string name, DateTimeOffset expires, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                await sync.SyncChannelAsync(_employeeId, token);
                if (await sync.ChannelAsync(_employeeId, token) is { IsLinked: true })
                {
                    IsLinked = true;
                    IsWaiting = false;
                    Qr = null;
                    Status = Show.Format(AppResources.Link_Done, name);
                    scheduler.SyncSoon(TimeSpan.Zero);
                    return;
                }

                if (DateTimeOffset.UtcNow > expires)
                {
                    IsWaiting = false;
                    Qr = null;
                    Status = AppResources.Link_Expired;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Worker: link this phone to the owner's by scanning the QR code (camera photo, read on the phone) or pasting the
/// code from WhatsApp; then wait until the owner's phone confirms and sends the worker's data.
/// </summary>
public sealed partial class JoinViewModel(SyncEngine sync, SyncScheduler scheduler, Dialogs dialogs, IServiceProvider services) : ObservableObject
{
    private CancellationTokenSource? _polling;

    [ObservableProperty]
    public partial bool IsWaiting { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public async Task LoadAsync()
    {
        IsWaiting = await sync.WorkerStateAsync() == ChannelState.Joining;
        if (IsWaiting)
        {
            StartPolling();
        }
    }

    public void Stop() => _polling?.Cancel();

    [RelayCommand]
    private async Task ScanAsync()
    {
        try
        {
            if (await MediaPicker.Default.CapturePhotoAsync() is not { } photo)
            {
                return;
            }

            await using var stream = await photo.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var texts = await Device.Vision.QrReader.ReadAsync(buffer.ToArray());
            var code = texts.FirstOrDefault(t => Invite.TryParse(t) is not null);
            if (code is null)
            {
                await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_NoQr);
                return;
            }

            await UseAsync(code);
        }
        catch (PermissionException)
        {
            await dialogs.AlertAsync(AppResources.Join_Title, AppResources.Join_NoCamera);
        }
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (Invite.TryParse(text) is null)
        {
            text = await dialogs.PromptAsync(AppResources.Join_Title, AppResources.Join_PastePrompt, maxLength: 2000);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            await UseAsync(text);
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        Stop();
        await sync.CancelJoinAsync();
        IsWaiting = false;
        await Nav.GoAsync("//setup");
    }

    private async Task UseAsync(string text)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await sync.JoinAsync(text);
            if (!result.Ok)
            {
                await dialogs.ErrorAsync(result.Error);
                return;
            }

            IsWaiting = true;
            StartPolling();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void StartPolling()
    {
        _polling?.Cancel();
        _polling = new CancellationTokenSource();
        var token = _polling.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    await scheduler.RunAsync(token);
                    if (await sync.WorkerStateAsync(token) == ChannelState.Linked)
                    {
                        await MainThread.InvokeOnMainThreadAsync(() => StartViewModel.GoToEntryScreenAsync(services.GetRequiredService<AccountService>()));
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }
}

/// <summary>
/// The worker's own home screen: a big box for today's sarees, this week and month with earnings, recent weeks with
/// payment status, and whether everything has reached the owner's phone.
/// </summary>
public sealed partial class WorkerHomeViewModel : ObservableObject
{
    private readonly WorkerService _worker;
    private readonly ProductionService _production;
    private readonly SyncEngine _sync;
    private readonly SyncScheduler _scheduler;
    private readonly UpdateService _updates;
    private readonly Dialogs _dialogs;

    public WorkerHomeViewModel(WorkerService worker, ProductionService production, SyncEngine sync, SyncScheduler scheduler, UpdateService updates, Dialogs dialogs)
    {
        _worker = worker;
        _production = production;
        _sync = sync;
        _scheduler = scheduler;
        _updates = updates;
        _dialogs = dialogs;
        scheduler.Synced += report => _ = LoadAsync();
        updates.Changed += () => MainThread.BeginInvokeOnMainThread(() =>
        {
            HasUpdate = updates.Available is not null;
            UpdateText = updates.Available is { } u ? Show.Format(AppResources.Update_Banner, u.Version.ToString(3)) : "";
        });
    }

    public ObservableCollection<string> Weeks { get; } = [];

    [ObservableProperty]
    public partial string Greeting { get; set; } = "";

    [ObservableProperty]
    public partial string TodayTitle { get; set; } = "";

    [ObservableProperty]
    public partial string TodayText { get; set; } = "";

    [ObservableProperty]
    public partial bool TodayLocked { get; set; }

    [ObservableProperty]
    public partial string WeekText { get; set; } = "";

    [ObservableProperty]
    public partial string WeekMoney { get; set; } = "";

    [ObservableProperty]
    public partial string MonthText { get; set; } = "";

    [ObservableProperty]
    public partial string MonthMoney { get; set; } = "";

    [ObservableProperty]
    public partial string SyncText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    private bool _typing;

    public async Task LoadAsync()
    {
        var result = await _worker.DashboardAsync();
        if (!result.Ok)
        {
            return;
        }

        var d = result.Value!;
        var s = d.Summary;
        Greeting = Show.Format(AppResources.Home_Hello, d.Worker.FullName);
        TodayTitle = $"{AppResources.Home_Today} · {Show.Day(s.Today)}";
        if (!_typing)
        {
            TodayText = s.TodaySarees?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        TodayLocked = d.TodayLocked;
        WeekText = $"{AppResources.Home_ThisWeek} · {Show.Range(s.Week)}";
        WeekMoney = $"{Show.Sarees(s.ThisWeek.Sarees)} · {s.ThisWeek.Earnings}";
        MonthText = Show.Month(s.Today);
        MonthMoney = $"{Show.Sarees(s.ThisMonth.Sarees)} · {s.ThisMonth.Earnings}";
        Weeks.Clear();
        foreach (var week in d.Weeks.Where(w => w.Status != PaymentStatus.Nothing))
        {
            var state = week.Status == PaymentStatus.Paid
                ? Show.Format(AppResources.Worker_HistoryPaid, week.Total, Show.Day(week.PaidDate!.Value))
                : Show.Format(AppResources.Worker_HistoryPending, week.Total);
            Weeks.Add(Show.Format(AppResources.Worker_HistoryLine, Show.Range(week.Week), $"{Show.Sarees(week.Sarees)} · {state}"));
        }

        var pending = await _sync.PendingAsync();
        SyncText = pending > 0 ? Show.Format(AppResources.Sync_Pending, pending) : AppResources.Sync_AllSent;
        HasUpdate = _updates.Available is not null;
    }

    partial void OnTodayTextChanged(string value) => _typing = true;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!int.TryParse(TodayText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            await _dialogs.AlertAsync(AppResources.Common_Check, AppResources.WorkerHome_TypeNumber);
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _production.SaveOwnTodayAsync(count);
            if (!result.Ok)
            {
                await _dialogs.ErrorAsync(result.Error?.Code == ErrorCode.NotAllowed ? new Error(ErrorCode.TodaySetByOwner) : result.Error);
                return;
            }

            _typing = false;
            Dialogs.Toast(AppResources.Entry_Saved);
            _scheduler.SyncSoon(TimeSpan.Zero);
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        var report = await _scheduler.RunAsync();
        if (report.Error is not null)
        {
            Dialogs.Toast(AppResources.Sync_Offline);
        }

        await LoadAsync();
        IsRefreshing = false;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_updates.Available is { } update)
        {
            await _updates.InstallAsync(update);
        }
    }
}
