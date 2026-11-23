namespace HandloomFactory.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("workerdetail", typeof(Views.WorkerPage));
        Routing.RegisterRoute("linkphone", typeof(Views.LinkPhonePage));
    }
}
