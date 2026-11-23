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

        await GoToEntryScreenAsync(services.GetRequiredService<AccountService>());
    }

    /// <summary>Setup (new phone), choose a PIN (data imported for an owner without a PIN here), or login.</summary>
    public static async Task GoToEntryScreenAsync(AccountService accounts)
    {
        if (!await accounts.IsSetUpAsync())
        {
            // A worker's phone that scanned the QR but hasn't been confirmed yet goes back to waiting.
            var sync = IPlatformApplication.Current?.Services.GetService<Core.Sync.SyncEngine>();
            var joining = sync is not null && await sync.WorkerStateAsync() == Core.Sync.ChannelState.Joining;
            await Nav.GoAsync(joining ? "//join" : "//setup");
        }
        else if (await accounts.PersonNeedingPinAsync() is not null)
        {
            await Nav.GoAsync("//newpin");
        }
        else
        {
            await Nav.LoginAsync();
        }
    }
}

/// <summary>After importing data on a new phone: the owner chooses a PIN for this phone (PINs are never exported).</summary>
public sealed partial class NewPinViewModel(AccountService accounts, Dialogs dialogs) : ObservableObject
{
    [ObservableProperty]
    public partial string Intro { get; set; } = "";

    [ObservableProperty]
    public partial string Pin { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPin { get; set; } = "";

    public async Task LoadAsync() =>
        Intro = Show.Format(AppResources.NewPin_Intro, (await accounts.PersonNeedingPinAsync())?.FullName ?? "");

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await accounts.SetFirstPinAsync(Pin, ConfirmPin);
        if (!result.Ok)
        {
            await dialogs.ErrorAsync(result.Error);
            return;
        }

        await Nav.HomeAsync();
    }
}

/// <summary>First launch on the owner's phone: factory name, owner name, PIN twice, or restore from an export file.</summary>
public sealed partial class SetupViewModel(AccountService accounts, DataService data, Dialogs dialogs) : ObservableObject
{
    /// <summary>A worker's own phone: link it to the owner's phone with the QR code (TD-03).</summary>
    [RelayCommand]
    private Task JoinAsWorkerAsync() => Nav.GoAsync("//join");

    /// <summary>New phone, existing factory: import an export file instead of starting empty (D18).</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        var text = await DataFiles.PickAsync();
        if (text is null)
        {
            return;
        }

        var preview = data.Preview(text);
        if (!preview.Ok)
        {
            await dialogs.ErrorAsync(preview.Error);
            return;
        }

        var p = preview.Value!;
        var range = p.FirstDay is { } first && p.LastDay is { } last ? Show.Format(AppResources.Settings_ImportRange, Show.DayWithYear(first), Show.DayWithYear(last)) : "";
        var summary = Show.Format(AppResources.Settings_ImportSummary, p.FactoryName, p.ExportedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture), p.Workers, p.Entries, p.Payments, range);
        if (!await dialogs.ConfirmAsync(AppResources.Settings_ImportTitle, summary, AppResources.Settings_ImportReplace))
        {
            return;
        }

        var imported = await data.ImportAsync(p.Data);
        if (!imported.Ok)
        {
            await dialogs.ErrorAsync(imported.Error);
            return;
        }

        await StartViewModel.GoToEntryScreenAsync(accounts);
    }

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
public sealed partial class SettingsViewModel(AppHost host, AccountService accounts, UpdateService updates, Dialogs dialogs, WorkflowContext context, DataService data) : ObservableObject
{
    private static string AppVersion => UpdateService.CurrentVersion.ToString(3);

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Export all data (D18) and share the file.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await data.ExportAsync(AppVersion);
            if (!result.Ok)
            {
                await dialogs.ErrorAsync(result.Error);
                return;
            }

            await DataFiles.ShareAsync(result.Value!, (await context.TimeAsync()).Today);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Import (D18): pick a file, show what's in it, confirm, save a safety copy, replace everything, log in again.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var text = await DataFiles.PickAsync();
            if (text is null)
            {
                return;
            }

            var preview = data.Preview(text);
            if (!preview.Ok)
            {
                await dialogs.ErrorAsync(preview.Error);
                return;
            }

            var p = preview.Value!;
            var range = p.FirstDay is { } first && p.LastDay is { } last ? Show.Format(AppResources.Settings_ImportRange, Show.DayWithYear(first), Show.DayWithYear(last)) : "";
            var summary = Show.Format(AppResources.Settings_ImportSummary, p.FactoryName, p.ExportedAt.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture), p.Workers, p.Entries, p.Payments, range);
            if (!await dialogs.ConfirmAsync(AppResources.Settings_ImportTitle, summary, AppResources.Settings_ImportReplace))
            {
                return;
            }

            var current = await data.ExportAsync(AppVersion, audit: false);
            if (current.Ok)
            {
                await DataFiles.SaveSafetyCopyAsync(current.Value!);
            }

            var imported = await data.ImportAsync(p.Data);
            if (!imported.Ok)
            {
                await dialogs.ErrorAsync(imported.Error);
                return;
            }

            Dialogs.Toast(AppResources.Settings_Imported);
            await StartViewModel.GoToEntryScreenAsync(accounts);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    public partial string FactoryText { get; set; } = "";

    [ObservableProperty]
    public partial string UserText { get; set; } = "";

    public string VersionText => Show.Format(AppResources.Settings_Version, UpdateService.CurrentVersion.ToString(3));

    /// <summary>Export, import and factory details are for the owner's phone only.</summary>
    [ObservableProperty]
    public partial bool IsOwner { get; set; }

    public async Task LoadAsync()
    {
        context.Reset();
        var factory = await context.FactoryAsync();
        IsOwner = host.Session.IsAdmin;
        FactoryText = factory is null ? "" : IsOwner ? $"{factory.Name} · {factory.OwnerName}" : factory.Name;
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
