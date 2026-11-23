using HandloomFactory.App.Services;

namespace HandloomFactory.App;

/// <summary>
/// App lifecycle: on every return to the app, the owner's PIN is asked again after 5 minutes away (D3), the app checks
/// for a new version (D16) and syncs (TD-03). If the owner unlinks this worker phone, it goes back to the first screen.
/// </summary>
public partial class App : Application
{
    private readonly SessionLock _lock;
    private readonly UpdateService _updates;
    private readonly IServiceProvider _services;

    public App(SessionLock sessionLock, UpdateService updates, IServiceProvider services)
    {
        InitializeComponent();
        _lock = sessionLock;
        _updates = updates;
        _services = services;
    }

    private SyncScheduler? Scheduler => _services.GetService<AppHost>()?.Open() == true ? _services.GetService<SyncScheduler>() : null;

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Created += (_, _) =>
        {
            if (Scheduler is { } scheduler)
            {
                scheduler.Unlinked += OnUnlinked;
                scheduler.Start();
                scheduler.SyncSoon(TimeSpan.FromSeconds(2));
            }
        };
        window.Stopped += (_, _) =>
        {
            _lock.Left();
            Scheduler?.Stop();
        };
        window.Resumed += async (_, _) =>
        {
            if (_lock.Returned())
            {
                await Nav.LoginAsync();
            }

            if (Scheduler is { } scheduler)
            {
                scheduler.Start();
                scheduler.SyncSoon(TimeSpan.Zero);
            }

            await _updates.CheckAndAnnounceAsync();
        };
        return window;
    }

    /// <summary>The owner unlinked this phone (worker disabled or re-linked): its data is gone, back to the start.</summary>
    private void OnUnlinked()
    {
        _services.GetRequiredService<AppHost>().Session.SignOut();
        _ = Nav.GoAsync("//setup");
    }
}
