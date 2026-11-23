using HandloomFactory.App.Services;

namespace HandloomFactory.App;

/// <summary>
/// App lifecycle: on every return to the app, the owner's PIN is asked again after 5 minutes away (D3), and the app
/// checks for a new version (D16).
/// </summary>
public partial class App : Application
{
    private readonly SessionLock _lock;
    private readonly UpdateService _updates;

    public App(SessionLock sessionLock, UpdateService updates)
    {
        InitializeComponent();
        _lock = sessionLock;
        _updates = updates;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Stopped += (_, _) => _lock.Left();
        window.Resumed += async (_, _) =>
        {
            if (_lock.Returned())
            {
                await Nav.LoginAsync();
            }

            await _updates.CheckAndAnnounceAsync();
        };
        return window;
    }
}
