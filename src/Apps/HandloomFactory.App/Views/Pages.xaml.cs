using HandloomFactory.App.Services;
using HandloomFactory.App.ViewModels;

namespace HandloomFactory.App.Views;

/// <summary>Routes once to setup / login when the app starts.</summary>
public partial class StartPage : ContentPage
{
    private readonly StartViewModel _viewModel;
    private bool _routed;

    public StartPage(StartViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_routed)
        {
            _routed = true;
            await _viewModel.RouteAsync();
        }
    }
}

public partial class SetupPage : ContentPage
{
    public SetupPage(SetupViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel;

    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        viewModel.TryAgain += FocusPin;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
        FocusPin();
    }

    /// <summary>
    /// Opens the keyboard on the PIN box. Delayed a little: focusing while the page is still being shown (or inside
    /// the key event of a wrong PIN) is ignored by Android (found in the emulator test).
    /// </summary>
    private void FocusPin() => Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(300), () =>
    {
        PinEntry.Focus();
        if (PinEntry.Handler?.PlatformView is Android.Views.View view
            && view.Context?.GetSystemService(Android.Content.Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager keyboard)
        {
            keyboard.ShowSoftInput(view, Android.Views.InputMethods.ShowFlags.Implicit);
        }
    });

    /// <summary>Android back on the login screen leaves the app instead of returning to a logged-out page.</summary>
    protected override bool OnBackButtonPressed()
    {
        Application.Current?.Quit();
        return true;
    }
}

/// <summary>Signed-in screens reload their data every time they appear; without a login they go to the login screen.</summary>
public static class SignIn
{
    public static bool EnsureSignedIn()
    {
        if (IPlatformApplication.Current?.Services.GetService<AppHost>() is { Session.IsSignedIn: true })
        {
            return true;
        }

        _ = Nav.LoginAsync();
        return false;
    }
}

public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;
    private readonly UpdateService _updates;

    public HomePage(HomeViewModel viewModel, UpdateService updates)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _updates = updates;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
            await _updates.CheckAndAnnounceAsync();
        }
    }
}

public partial class EntryPage : ContentPage
{
    private readonly EntryViewModel _viewModel;

    public EntryPage(EntryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
        }
    }
}

public partial class WorkersPage : ContentPage
{
    private readonly WorkersViewModel _viewModel;

    public WorkersPage(WorkersViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
        }
    }
}

public partial class WorkerPage : ContentPage
{
    private readonly WorkerViewModel _viewModel;

    public WorkerPage(WorkerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
        }
    }
}

public partial class ReportPage : ContentPage
{
    private readonly ReportViewModel _viewModel;

    public ReportPage(ReportViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
        }
    }
}

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (SignIn.EnsureSignedIn())
        {
            await _viewModel.LoadAsync();
        }
    }
}

/// <summary>After an import on a new phone: the owner chooses a PIN here.</summary>
public partial class NewPinPage : ContentPage
{
    private readonly NewPinViewModel _viewModel;

    public NewPinPage(NewPinViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }
}
