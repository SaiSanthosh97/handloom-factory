using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;
using HandloomFactory.Storage.Sqlite;

namespace HandloomFactory.Core.Sync.Tests;

/// <summary>
/// An in-memory stand-in for ntfy.sh: topics, increasing message ids, "since" polling, and messages that EXPIRE
/// after 12 hours like on ntfy.sh. Can be switched off to simulate no internet.
/// </summary>
public sealed class InMemoryRelay(IClock clock) : IRelay, IRelayFactory
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(12);

    private readonly Dictionary<string, List<RelayMessage>> _topics = [];
    private long _next;

    public bool Online { get; set; } = true;

    public List<(string Topic, string Title, string Message)> Notifications { get; } = [];

    public IRelay For(string serverUrl) => this;

    public IReadOnlyList<RelayMessage> Messages(string topic) => _topics.TryGetValue(topic, out var list) ? list : [];

    public Task<string> PublishAsync(string topic, string text, CancellationToken cancellationToken = default)
    {
        CheckOnline();
        if (text.Length > NtfyRelay.MaxMessageLength)
        {
            throw new ArgumentException("Too long for ntfy.");
        }

        var id = (++_next).ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
        Topic(topic).Add(new RelayMessage(id, clock.UtcNow, text));
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<RelayMessage>> FetchAsync(string topic, string? since, CancellationToken cancellationToken = default)
    {
        CheckOnline();
        var list = Topic(topic);
        list.RemoveAll(m => clock.UtcNow - m.Time > Retention);
        IReadOnlyList<RelayMessage> result = since is null ? [.. list] : [.. list.Where(m => string.CompareOrdinal(m.Id, since) > 0)];
        return Task.FromResult(result);
    }

    public Task NotifyAsync(string topic, string title, string message, CancellationToken cancellationToken = default)
    {
        CheckOnline();
        Notifications.Add((topic, title, message));
        return Task.CompletedTask;
    }

    private List<RelayMessage> Topic(string topic) => _topics.TryGetValue(topic, out var list) ? list : _topics[topic] = [];

    private void CheckOnline()
    {
        if (!Online)
        {
            throw new HttpRequestException("offline");
        }
    }
}

/// <summary>One complete phone: its own encrypted database, sync store, outbox, use cases and sync engine.</summary>
public sealed class TestPhone : IDisposable
{
    public TestPhone(ManualClock clock, InMemoryRelay relay)
    {
        Folder = Path.Combine(Path.GetTempPath(), "hf-sync-tests", Guid.NewGuid().ToString("N"));
        Database = FactoryDatabaseFiles.Open(Folder, new InMemoryKeyVault());
        Inner = new SqliteFactoryStore(Database);
        SyncStore = new SqliteSyncStore(Database);
        Store = new OutboxStore(Inner, SyncStore);
        Context = new WorkflowContext(Store, clock, Session);
        Accounts = new AccountService(Context);
        Employees = new EmployeeService(Context);
        Production = new ProductionService(Context);
        Payments = new PaymentService(Context);
        Reports = new ReportService(Context);
        Data = new DataService(Context);
        Worker = new WorkerService(Context, Payments);
        Sync = new SyncEngine(Inner, SyncStore, relay, clock, "0.3.0");
    }

    public string Folder { get; }

    public FactoryDatabase Database { get; }

    public SqliteFactoryStore Inner { get; }

    public SqliteSyncStore SyncStore { get; }

    public OutboxStore Store { get; }

    public UserSession Session { get; } = new();

    public WorkflowContext Context { get; }

    public AccountService Accounts { get; }

    public EmployeeService Employees { get; }

    public ProductionService Production { get; }

    public PaymentService Payments { get; }

    public ReportService Reports { get; }

    public DataService Data { get; }

    public WorkerService Worker { get; }

    public SyncEngine Sync { get; }

    public Task<SyncReport> SyncAsync() => Sync.SyncAllAsync();

    public async Task<IReadOnlyList<ProductionEntry>> EntriesAsync() =>
        await Inner.GetEntriesAsync(new DateRange(DateOnly.MinValue, DateOnly.MaxValue));

    public void Dispose()
    {
        Database.Dispose();
        FactoryDatabaseFiles.Erase(Folder);
    }
}

/// <summary>A factory: the owner's phone, workers Ramesh and Suma, and phones for them. Monday 23 Nov 2026, 10:00 IST.</summary>
public sealed class Factory : IDisposable
{
    public const string Relay = "https://ntfy.example";

    public Factory()
    {
        Relay1 = new InMemoryRelay(Clock);
        Owner = new TestPhone(Clock, Relay1);
    }

    public static DateOnly Today => new(2026, 11, 23);

    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(5.5)));

    public InMemoryRelay Relay1 { get; }

    public TestPhone Owner { get; }

    public List<TestPhone> Phones { get; } = [];

    public Employee Ramesh { get; private set; } = null!;

    public Employee Suma { get; private set; } = null!;

    public async Task<Factory> SetUpAsync()
    {
        Assert.True((await Owner.Accounts.SetUpFactoryAsync("Sri Lakshmi Handlooms", "Santhosh", "1234", "1234", "Asia/Kolkata")).Ok);
        Ramesh = (await Owner.Employees.CreateAsync("Ramesh", null, Money.FromRupees(500), new DateOnly(2026, 11, 1))).Value!;
        Suma = (await Owner.Employees.CreateAsync("Suma", null, Money.FromRupees(450), new DateOnly(2026, 11, 1))).Value!;
        return this;
    }

    public TestPhone NewPhone()
    {
        var phone = new TestPhone(Clock, Relay1);
        Phones.Add(phone);
        return phone;
    }

    /// <summary>Owner shows the QR, the worker's phone scans it, both sync, the worker chooses PIN 1111 and logs in.</summary>
    public async Task<TestPhone> LinkAsync(Employee worker)
    {
        var phone = NewPhone();
        var invite = await Owner.Sync.CreateInviteAsync(worker.Id, Relay);
        Assert.True((await phone.Sync.JoinAsync(invite.ToText())).Ok);
        await Owner.SyncAsync();   // accepts the link, sends the snapshot
        await phone.SyncAsync();   // receives it: linked
        Assert.Equal(ChannelState.Linked, await phone.Sync.WorkerStateAsync());
        Assert.True((await phone.Accounts.SetFirstPinAsync("1111", "1111")).Ok);
        return phone;
    }

    public void Dispose()
    {
        Owner.Dispose();
        foreach (var phone in Phones)
        {
            phone.Dispose();
        }
    }
}
