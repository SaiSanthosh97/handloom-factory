namespace HandloomFactory.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("worker", typeof(Views.WorkerPage));
    }
}
