using HandloomFactory.App.Services;
using HandloomFactory.App.ViewModels;
using HandloomFactory.App.Views;
using HandloomFactory.Core.Workflow;
using Microsoft.Extensions.Logging;

namespace HandloomFactory.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<AppHost>();
        services.AddSingleton<Dialogs>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<SessionLock>();

        // Use cases. The start screen opens the database before any of these is first used.
        services.AddSingleton(sp =>
        {
            var host = sp.GetRequiredService<AppHost>();
            return host.Open()
                ? new WorkflowContext(host.Store, host.Clock, host.Session)
                : throw new InvalidOperationException("The database must be opened by the start screen first.");
        });
        services.AddSingleton<AccountService>();
        services.AddSingleton<EmployeeService>();
        services.AddSingleton<ProductionService>();
        services.AddSingleton<ReportService>();

        // Screens: tabs are singletons (they keep their place); pushed and start pages are fresh each time.
        services.AddTransient<StartViewModel>();
        services.AddTransient<StartPage>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SetupPage>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginPage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<HomePage>();
        services.AddSingleton<EntryViewModel>();
        services.AddSingleton<EntryPage>();
        services.AddSingleton<WorkersViewModel>();
        services.AddSingleton<WorkersPage>();
        services.AddTransient<WorkerViewModel>();
        services.AddTransient<WorkerPage>();
        services.AddSingleton<ReportViewModel>();
        services.AddSingleton<ReportPage>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsPage>();

        return builder.Build();
    }
}
