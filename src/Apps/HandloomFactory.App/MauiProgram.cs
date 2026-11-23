using HandloomFactory.App.Resources.Strings;
using HandloomFactory.App.Services;
using HandloomFactory.App.ViewModels;
using HandloomFactory.App.Views;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Sync;
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
        services.AddSingleton<UpdateNotifier>();

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
        services.AddSingleton<PaymentService>();
        services.AddSingleton<DataService>();
        services.AddSingleton<WorkerService>();
        services.AddSingleton<AuditService>();

        // Sync (0.3.0): the engine writes to the inner store (records from other phones are not echoed back).
        services.AddSingleton<IRelayFactory>(sp => new NtfyRelayFactory(sp.GetRequiredService<HttpClient>(), "HandloomFactory"));
        services.AddSingleton(sp =>
        {
            var host = sp.GetRequiredService<AppHost>();
            host.Open();
            return new SyncEngine(host.Inner, host.SyncStore, sp.GetRequiredService<IRelayFactory>(), host.Clock, AppInfo.Current.VersionString)
            {
                SubmittedText = (name, count) => Show.Format(AppResources.Notify_Submitted, name, count),
                WeeklyText = (total, workers) => Show.Format(AppResources.Notify_Weekly, total, workers),
            };
        });
        services.AddSingleton<SyncScheduler>();

        // Screens: tabs are singletons (they keep their place); pushed and start pages are fresh each time.
        services.AddTransient<StartViewModel>();
        services.AddTransient<StartPage>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SetupPage>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginPage>();
        services.AddTransient<NewPinViewModel>();
        services.AddTransient<NewPinPage>();
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
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsPage>(); // on the owner's and the worker's tabs
        services.AddTransient<LinkPhoneViewModel>();
        services.AddTransient<LinkPhonePage>();
        services.AddTransient<JoinViewModel>();
        services.AddTransient<JoinPage>();
        services.AddTransient<AuditViewModel>();
        services.AddTransient<AuditPage>();
        services.AddSingleton<WorkerHomeViewModel>();
        services.AddSingleton<WorkerHomePage>();

        var app = builder.Build();

        // Also runs when Android starts the app only for the background job (no screen).
        Device.BackgroundWork.BackgroundWork.Initialize(async token =>
        {
            if (app.Services.GetRequiredService<AppHost>().Open())
            {
                await app.Services.GetRequiredService<SyncScheduler>().RunAsync(token);
            }
        });
        return app;
    }
}
