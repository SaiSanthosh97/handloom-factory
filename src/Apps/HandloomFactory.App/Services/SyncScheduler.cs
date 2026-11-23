using HandloomFactory.App.Resources.Strings;
using HandloomFactory.Core.Sync;
using HandloomFactory.Device.BackgroundWork;

namespace HandloomFactory.App.Services;

/// <summary>
/// When sync runs (TD-03): right after a change is saved (a few seconds later, so quick edits go together), when the
/// app opens or comes back, every minute while it is open, and about every 15 minutes in the background (Android
/// JobScheduler, also when the app is closed). Nothing runs before this phone is linked to anyone.
/// </summary>
public sealed class SyncScheduler
{
    private readonly IServiceProvider _services;
    private readonly AppHost _host;
    private CancellationTokenSource? _soon;
    private IDispatcherTimer? _timer;

    public SyncScheduler(IServiceProvider services, AppHost host)
    {
        _services = services;
        _host = host;
        host.Queued += () => SyncSoon(TimeSpan.FromSeconds(3));
    }

    /// <summary>Raised after every run (screens refresh their "last synced" line and data).</summary>
    public event Action<SyncReport>? Synced;

    /// <summary>Raised when the owner unlinked this worker phone (its data is gone): go back to the first screen.</summary>
    public event Action? Unlinked;

    public SyncReport? Last { get; private set; }

    public DateTimeOffset? LastRun { get; private set; }

    private SyncEngine Engine => _services.GetRequiredService<SyncEngine>();

    /// <summary>Starts the foreground timer and the background job.</summary>
    public void Start()
    {
        BackgroundWork.SetEnabled(true);

        _timer ??= Application.Current?.Dispatcher.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromMinutes(1);
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    public void Stop() => _timer?.Stop();

    public void SyncSoon(TimeSpan delay)
    {
        _soon?.Cancel();
        _soon = new CancellationTokenSource();
        var token = _soon.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay, token);
            await RunAsync(token);
        }, token);
    }

    public async Task<SyncReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Open() || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return SyncReport.Nothing;
        }

        SyncReport report;
        try
        {
            report = await Engine.SyncAllAsync(cancellationToken);
            await Engine.SendWeeklyNoticeIfDueAsync(cancellationToken); // owner phone, Saturday evening, once
            await _services.GetRequiredService<UpdateNotifier>().CheckInBackgroundAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SyncReport.Nothing;
        }

        Last = report;
        LastRun = DateTimeOffset.Now;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Synced?.Invoke(report);
            if (report.Unlinked)
            {
                Dialogs.Toast(AppResources.Sync_Unlinked);
                Unlinked?.Invoke();
            }
        });
        return report;
    }

    private void OnTick(object? sender, EventArgs e) => SyncSoon(TimeSpan.Zero);
}
