using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HandloomFactory.Storage.Sqlite;

/// <summary>
/// The encrypted on-device database (adapted from Travel Vault). The whole file is encrypted by SQLite3 Multiple
/// Ciphers with a random 256-bit key held wrapped by the Android Keystore. One connection, one operation at a time.
/// </summary>
public sealed class FactoryDatabase : IDisposable
{
    private static int _initialized;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FactoryDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens (or creates) the database and brings the schema up to date.</summary>
    /// <exception cref="WrongKeyException">The file exists but was encrypted with another key (or is damaged).</exception>
    public static FactoryDatabase Open(string path, byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The database key must be 32 bytes.", nameof(key));
        }

        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            connection.Run($"PRAGMA hexkey = '{Convert.ToHexString(key)}';");
            connection.Run("SELECT count(*) FROM sqlite_master;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB
        {
            connection.Dispose();
            throw new WrongKeyException();
        }

        Migrations.Apply(connection);
        connection.Run("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;");
        return new FactoryDatabase(connection);
    }

    /// <summary>Runs work with exclusive use of the connection.</summary>
    public async Task<T> UseAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return work(_connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs work inside one transaction (all or nothing).</summary>
    public Task InTransactionAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken cancellationToken = default) =>
        UseAsync(c =>
        {
            using var transaction = c.BeginTransaction();
            work(c, transaction);
            transaction.Commit();
            return true;
        }, cancellationToken);

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}

public sealed class WrongKeyException() : Exception("The database could not be opened with this key.");

/// <summary>
/// Schema versions, applied in order and tracked in <c>PRAGMA user_version</c>. Never edit a released step: add one.
/// </summary>
internal static class Migrations
{
    private static readonly string[] Steps =
    [
        // 1 (v0.1.0): factory, people, rates, entries, audit
        """
        CREATE TABLE factory (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, owner_name TEXT NOT NULL, time_zone TEXT NOT NULL,
            max_per_day INTEGER NOT NULL, created_at TEXT NOT NULL);

        CREATE TABLE employee (
            id TEXT PRIMARY KEY, code TEXT NOT NULL UNIQUE, full_name TEXT NOT NULL, phone TEXT, role INTEGER NOT NULL,
            is_active INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);

        CREATE TABLE credential (
            employee_id TEXT PRIMARY KEY REFERENCES employee(id), pin_hash BLOB NOT NULL, pin_salt BLOB NOT NULL,
            pin_iterations INTEGER NOT NULL, failed_attempts INTEGER NOT NULL, locked_until TEXT);

        CREATE TABLE rate_change (
            id TEXT PRIMARY KEY, employee_id TEXT NOT NULL REFERENCES employee(id), rate_paise INTEGER NOT NULL,
            effective_from TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE INDEX ix_rate_change_employee ON rate_change (employee_id, effective_from);

        CREATE TABLE production_entry (
            id TEXT PRIMARY KEY, employee_id TEXT NOT NULL REFERENCES employee(id), entry_date TEXT NOT NULL,
            saree_count INTEGER NOT NULL, entered_by INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            UNIQUE (employee_id, entry_date));
        CREATE INDEX ix_production_entry_date ON production_entry (entry_date);

        CREATE TABLE audit_log (
            id TEXT PRIMARY KEY, at TEXT NOT NULL, actor_id TEXT, action TEXT NOT NULL, target_type TEXT NOT NULL,
            target_id TEXT NOT NULL, old_value TEXT, new_value TEXT);
        CREATE INDEX ix_audit_log_at ON audit_log (at);

        CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,

        // 2 (v0.2.0): weekly payments (only weeks marked paid are stored; pending weeks are computed)
        """
        CREATE TABLE payment (
            id TEXT PRIMARY KEY, employee_id TEXT NOT NULL REFERENCES employee(id), week_start TEXT NOT NULL,
            week_end TEXT NOT NULL, sarees INTEGER NOT NULL, earnings_paise INTEGER NOT NULL, adjustment_paise INTEGER NOT NULL,
            total_paise INTEGER NOT NULL, paid_date TEXT NOT NULL, marked_at TEXT NOT NULL,
            UNIQUE (employee_id, week_start));
        CREATE INDEX ix_payment_week ON payment (week_start);
        """,

        // 3 (v0.3.0): sync channels (one per linked worker) and the outbox
        """
        CREATE TABLE sync_channel (
            employee_id TEXT PRIMARY KEY, relay_url TEXT NOT NULL, topic TEXT NOT NULL, channel_key BLOB NOT NULL,
            owner_key BLOB NOT NULL, worker_key BLOB, state INTEGER NOT NULL, invite_id TEXT, invite_secret BLOB,
            invite_expires TEXT, last_message_id TEXT, last_sync_at TEXT, last_seen_at TEXT, last_state_request_at TEXT,
            next_seq INTEGER NOT NULL, peer_seq INTEGER NOT NULL, created_at TEXT NOT NULL);

        CREATE TABLE outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT, channel TEXT NOT NULL, type TEXT NOT NULL, data TEXT NOT NULL);
        CREATE INDEX ix_outbox_channel ON outbox (channel, id);
        """,
    ];

    public static int Latest => Steps.Length;

    public static void Apply(SqliteConnection connection)
    {
        using var read = connection.Command("PRAGMA user_version;");
        var version = Convert.ToInt32(read.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        if (version > Steps.Length)
        {
            throw new InvalidDataException("The database was created by a newer version of the app.");
        }

        for (var i = version; i < Steps.Length; i++)
        {
            using var transaction = connection.BeginTransaction();
            connection.Run(Steps[i] + $"\nPRAGMA user_version = {i + 1};", transaction);
            transaction.Commit();
        }
    }
}

internal static class SqlHelpers
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Run(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        using var command = connection.Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string, object?)[] parameters)
    {
        using var command = connection.Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    /// <summary>Sortable UTC text (ISO-8601, fixed width).</summary>
    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? NullableString(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
