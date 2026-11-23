using HandloomFactory.Core.Workflow;

namespace HandloomFactory.App.Services;

/// <summary>
/// "Start over on this phone" (0.6.0): erases the factory, people, entries, payments, history and phone links from this
/// phone's database, so the first screen comes back (set up a factory, "I'm a worker", or restore from a file).
/// The same database and key are kept, so the services that hold the store keep working.
/// </summary>
public static class StartOver
{
    public static async Task EraseAsync(AppHost host, WorkflowContext context)
    {
        await host.SyncStore.WipeAsync();
        host.Session.SignOut();
        context.Reset();
        Preferences.Default.Remove("login.last");
    }
}
