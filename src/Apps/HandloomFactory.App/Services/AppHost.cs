using HandloomFactory.Core.Common;
using HandloomFactory.Core.Workflow;
using HandloomFactory.Device.Security;
using HandloomFactory.Storage.Sqlite;

namespace HandloomFactory.App.Services;

/// <summary>
/// Owns this phone's database. It is opened once at start; if the Android Keystore key was lost (factory reset, data
/// copied from another phone) the data can't be decrypted and the start screen offers to start fresh.
/// </summary>
public sealed class AppHost
{
    private static string Folder => Path.Combine(FileSystem.AppDataDirectory, "data");

    private readonly AndroidKeystoreVault _vault = new();
    private FactoryDatabase? _database;

    public bool OpenFailed { get; private set; }

    public FactoryDatabase Database => _database ?? throw new InvalidOperationException("The database is not open.");

    public IFactoryStore Store { get; private set; } = null!;

    public SqliteSettings Settings { get; private set; } = null!;

    public UserSession Session { get; } = new();

    public IClock Clock => SystemClock.Instance;

    /// <returns>False when the data can't be opened.</returns>
    public bool Open()
    {
        if (_database is not null)
        {
            return true;
        }

        try
        {
            _database = FactoryDatabaseFiles.Open(Folder, _vault);
            Store = new SqliteFactoryStore(_database);
            Settings = new SqliteSettings(_database);
            OpenFailed = false;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or WrongKeyException)
        {
            OpenFailed = true;
            return false;
        }
    }

    /// <summary>Deletes the unreadable data and its key, then opens a new empty database.</summary>
    public bool StartFresh()
    {
        _database?.Dispose();
        _database = null;
        _vault.Destroy();
        FactoryDatabaseFiles.Erase(Folder);
        return Open();
    }
}
