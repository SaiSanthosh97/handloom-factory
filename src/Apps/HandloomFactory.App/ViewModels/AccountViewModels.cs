using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.ViewModels;

/// <summary>Decides where the app starts: "can't open data", first-launch setup, or the PIN login.</summary>
/// <remarks>The use cases are resolved only after the database is open (or started fresh), because they keep its store.</remarks>
public sealed class StartViewModel(AppHost host, IServiceProvider services, Dialogs dialogs)
{
    public async Task RouteAsync()
    {
        if (!host.Open())
        {
            if (!await dialogs.ConfirmAsync(AppResources.Start_DbErrorTitle, AppResources.Start_DbErrorText, AppResources.Start_StartFresh) || !host.StartFresh())
            {
                Application.Current?.Quit();
                return;
            }
        }

        var accounts = services.GetRequiredService<AccountService>();
        await Nav.GoAsync(await accounts.IsSetUpAsync() ? "//login" : "//setup");
    }
}

/// <summary>First launch on the owner's phone: factory name, owner name, PIN twice.</summary>
public sealed partial class SetupViewModel(AccountService accounts, Dialogs dialogs) : ObservableObject
{
    [ObservableProperty]
    public partial string FactoryName { get; set; } = "";

    [ObservableProperty]
    public partial string OwnerName { get; set; } = "";

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPin { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await accounts.SetUpFactoryAsync(FactoryName, OwnerName, Pin, ConfirmPin, TimeZoneInfo.Local.Id);
            if (!result.Ok)
            {
                await dialogs.ErrorAsync(result.Error);
                return;
            }

            LoginViewModel.RememberLastUser(result.Value!.Id);
            await Nav.HomeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class PersonItem(Employee employee) : ObservableObject
{
    public Employee Employee { get; } = employee;

    public string Name => Employee.FullName;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Login (D2): tap your name, type your 4-digit PIN. It logs in as soon as the 4th digit is typed.</summary>
public sealed partial class LoginViewModel(AccountService accounts) : ObservableObject
{
    public ObservableCollection<PersonItem> People { get; } = [];

    [ObservableProperty]
    public partial PersonItem? Selected { get; set; }

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowPeople { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Raised after a wrong PIN, so the page puts the keyboard back on the PIN box.</summary>
    public event Action? TryAgain;

    public static void RememberLastUser(string employeeId) => Preferences.Default.Set("login.last", employeeId);

    public async Task LoadAsync()
    {
        Pin = "";
        Message = "";
        People.Clear();
        foreach (var person in await accounts.LoginChoicesAsync())
        {
            People.Add(new PersonItem(person));
        }

        var last = Preferences.Default.Get("login.last", "");
        Choose(People.FirstOrDefault(p => p.Employee.Id == last) ?? People.FirstOrDefault());
        ShowPeople = People.Count > 1;
    }

    [RelayCommand]
    private void Choose(PersonItem? person)
    {
        foreach (var p in People)
        {
            p.IsSelected = p == person;
        }

        Selected = person;
        Pin = "";
        Message = "";
    }

    partial void OnPinChanged(string value)
    {
        if (value.Length == Core.Security.Pins.Length)
        {
            // Run after Android has finished handling the 4th key press: clearing the box from inside that key event
            // crashes Android's password field (IndexOutOfBoundsException in setSpan), found in the emulator test.
            Application.Current!.Dispatcher.Dispatch(() => _ = LoginAsync());
        }
    }

    private async Task LoginAsync()
    {
        if (IsBusy || Selected is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await accounts.LoginAsync(Selected.Employee.Id, Pin);
            if (outcome.Success)
            {
                RememberLastUser(Selected.Employee.Id);
                Pin = "";
                await Nav.HomeAsync();
                return;
            }

            Pin = "";
            Message = outcome.LockedUntil is { } until
                ? Show.Format(AppResources.Login_Locked, Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds)))
                : outcome.AttemptsLeft > 0 ? Show.Format(AppResources.Login_WrongPin, outcome.AttemptsLeft) : AppResources.Login_WrongPinNoCount;
            TryAgain?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Settings: factory, version and updates, change PIN, log out.</summary>
public sealed partial class SettingsViewModel(AppHost host, AccountService accounts, UpdateService updates, Dialogs dialogs, WorkflowContext context) : ObservableObject
{
    [ObservableProperty]
    public partial string FactoryText { get; set; } = "";

    [ObservableProperty]
    public partial string UserText { get; set; } = "";

    public string VersionText => Show.Format(AppResources.Settings_Version, UpdateService.CurrentVersion.ToString(3));

    public async Task LoadAsync()
    {
        var factory = await context.FactoryAsync();
        FactoryText = factory is null ? "" : $"{factory.Name} · {factory.OwnerName}";
        UserText = Show.Format(AppResources.Settings_LoggedInAs, host.Session.User?.FullName ?? "");
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            if (await updates.CheckNowAsync() is { } update)
            {
                await updates.OfferAsync(update);
            }
            else
            {
                await dialogs.AlertAsync(AppResources.Update_Title, Show.Format(AppResources.Update_UpToDate, UpdateService.CurrentVersion.ToString(3)));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_CheckFailed);
        }
    }

    [RelayCommand]
    private async Task ChangePinAsync()
    {
        var current = await dialogs.PromptAsync(AppResources.Settings_ChangePin, AppResources.Settings_CurrentPin, Keyboard.Numeric, 4);
        if (current is null)
        {
            return;
        }

        var next = await dialogs.PromptAsync(AppResources.Settings_ChangePin, AppResources.Settings_NewPin, Keyboard.Numeric, 4);
        if (next is null)
        {
            return;
        }

        var again = await dialogs.PromptAsync(AppResources.Settings_ChangePin, AppResources.Settings_ConfirmPin, Keyboard.Numeric, 4);
        if (again is null)
        {
            return;
        }

        var result = await accounts.ChangePinAsync(current, next, again);
        if (result.Ok)
        {
            Dialogs.Toast(AppResources.Settings_PinChanged);
        }
        else
        {
            await dialogs.ErrorAsync(result.Error);
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        accounts.Logout();
        await Nav.LoginAsync();
    }
}
