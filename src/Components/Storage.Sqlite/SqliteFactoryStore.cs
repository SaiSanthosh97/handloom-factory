using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;
using Microsoft.Data.Sqlite;
using static HandloomFactory.Storage.Sqlite.SqlHelpers;

namespace HandloomFactory.Storage.Sqlite;

/// <summary>The factory store on SQLite. Each <see cref="CommitAsync"/> is one transaction.</summary>
public sealed class SqliteFactoryStore(FactoryDatabase db) : IFactoryStore
{
    public Task<FactoryInfo?> GetFactoryAsync(CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query(
            "SELECT id, name, owner_name, time_zone, max_per_day, created_at FROM factory LIMIT 1;",
            r => new FactoryInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), ParseUtc(r.GetString(5)))).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<Employee>> GetEmployeesAsync(CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Employee>>(c => c.Query(
            "SELECT id, code, full_name, phone, role, is_active, created_at, updated_at, weaves FROM employee ORDER BY code;",
            r => new Employee(r.GetString(0), r.GetString(1), r.GetString(2), r.NullableString(3), (Role)r.GetInt32(4), r.GetInt32(5) != 0, ParseUtc(r.GetString(6)), ParseUtc(r.GetString(7)), r.GetInt32(8) != 0)), cancellationToken);

    public Task<IReadOnlyList<RateChange>> GetRateChangesAsync(CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<RateChange>>(c => c.Query(
            "SELECT id, employee_id, rate_paise, effective_from, created_at FROM rate_change ORDER BY employee_id, effective_from;",
            r => new RateChange(r.GetString(0), r.GetString(1), new Money(r.GetInt64(2)), ParseDay(r.GetString(3)), ParseUtc(r.GetString(4)))), cancellationToken);

    public Task<IReadOnlyList<ProductionEntry>> GetEntriesAsync(DateRange range, string? employeeId = null, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<ProductionEntry>>(c => c.Query(
            """
            SELECT id, employee_id, entry_date, saree_count, entered_by, created_at, updated_at FROM production_entry
            WHERE entry_date BETWEEN $from AND $to AND ($employee IS NULL OR employee_id = $employee)
            ORDER BY entry_date, employee_id;
            """,
            r => new ProductionEntry(r.GetString(0), r.GetString(1), ParseDay(r.GetString(2)), r.GetInt32(3), (EnteredBy)r.GetInt32(4), ParseUtc(r.GetString(5)), ParseUtc(r.GetString(6))),
            ("$from", Day(range.Start)), ("$to", Day(range.End)), ("$employee", employeeId)), cancellationToken);

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(string? employeeId = null, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Payment>>(c => c.Query(
            """
            SELECT id, employee_id, week_start, week_end, sarees, earnings_paise, adjustment_paise, total_paise, paid_date, marked_at
            FROM payment WHERE ($employee IS NULL OR employee_id = $employee) ORDER BY week_start, employee_id;
            """,
            r => new Payment(r.GetString(0), r.GetString(1), ParseDay(r.GetString(2)), ParseDay(r.GetString(3)), r.GetInt32(4),
                new Money(r.GetInt64(5)), new Money(r.GetInt64(6)), new Money(r.GetInt64(7)), ParseDay(r.GetString(8)), ParseUtc(r.GetString(9))),
            ("$employee", employeeId)), cancellationToken);

    /// <summary>Deletes and rewrites everything in ONE transaction: a failure leaves the phone's data exactly as it was.</summary>
    public Task ReplaceAllAsync(FactoryData data, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            // Keep the PINs (and so the rows) of people who are in the new data: re-importing on the same phone keeps
            // the owner's PIN. Everyone else's PIN goes.
            c.Run("CREATE TEMP TABLE IF NOT EXISTS import_keep (id TEXT PRIMARY KEY); DELETE FROM import_keep;", t);
            foreach (var employee in data.Employees)
            {
                c.Run("INSERT OR IGNORE INTO import_keep (id) VALUES ($id);", t, ("$id", employee.Id));
            }

            c.Run("DELETE FROM credential WHERE employee_id NOT IN (SELECT id FROM import_keep);", t);
            foreach (var table in new[] { "payment", "production_entry", "rate_change", "audit_log", "factory" })
            {
                c.Run($"DELETE FROM {table};", t);
            }

            c.Run("DELETE FROM employee WHERE id NOT IN (SELECT id FROM import_keep);", t);
            c.Run("DELETE FROM import_keep;", t);

            var changes = new ChangeSet { Factory = data.Factory };
            changes.Employees.AddRange(data.Employees);
            changes.Rates.AddRange(data.Rates);
            changes.Entries.AddRange(data.Entries);
            changes.Payments.AddRange(data.Payments);
            changes.Audit.AddRange(data.Audit);
            Write(c, t, changes);
        }, cancellationToken);

    public Task<Credential?> GetCredentialAsync(string employeeId, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query(
            "SELECT employee_id, pin_hash, pin_salt, pin_iterations, failed_attempts, locked_until FROM credential WHERE employee_id = $id;",
            r => new Credential(r.GetString(0), new PinHash((byte[])r[1], (byte[])r[2], r.GetInt32(3)), r.GetInt32(4), r.IsDBNull(5) ? null : ParseUtc(r.GetString(5))),
            ("$id", employeeId)).FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<AuditEntry>>(c => c.Query(
            "SELECT id, at, actor_id, action, target_type, target_id, old_value, new_value FROM audit_log ORDER BY at DESC, id DESC LIMIT $limit;",
            r => new AuditEntry(r.GetString(0), ParseUtc(r.GetString(1)), r.NullableString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.NullableString(6), r.NullableString(7)),
            ("$limit", limit)), cancellationToken);

    public Task CommitAsync(ChangeSet changes, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) => Write(c, t, changes), cancellationToken);

    private static void Write(SqliteConnection c, SqliteTransaction t, ChangeSet changes)
    {
            if (changes.Factory is { } f)
            {
                c.Run(
                    """
                    INSERT INTO factory (id, name, owner_name, time_zone, max_per_day, created_at) VALUES ($id, $name, $owner, $zone, $max, $created)
                    ON CONFLICT(id) DO UPDATE SET name = $name, owner_name = $owner, time_zone = $zone, max_per_day = $max;
                    """,
                    t, ("$id", f.Id), ("$name", f.Name), ("$owner", f.OwnerName), ("$zone", f.TimeZoneId), ("$max", f.MaxSareesPerDay), ("$created", Utc(f.CreatedAt)));
            }

            foreach (var e in changes.Employees)
            {
                c.Run(
                    """
                    INSERT INTO employee (id, code, full_name, phone, role, is_active, created_at, updated_at, weaves)
                    VALUES ($id, $code, $name, $phone, $role, $active, $created, $updated, $weaves)
                    ON CONFLICT(id) DO UPDATE SET code = $code, full_name = $name, phone = $phone, role = $role, is_active = $active, updated_at = $updated, weaves = $weaves;
                    """,
                    t, ("$id", e.Id), ("$code", e.Code), ("$name", e.FullName), ("$phone", e.PhoneNumber), ("$role", (int)e.Role), ("$active", e.IsActive ? 1 : 0), ("$created", Utc(e.CreatedAt)), ("$updated", Utc(e.UpdatedAt)), ("$weaves", e.Weaves ? 1 : 0));
            }

            foreach (var r in changes.Rates)
            {
                c.Run(
                    """
                    INSERT INTO rate_change (id, employee_id, rate_paise, effective_from, created_at) VALUES ($id, $employee, $rate, $from, $created)
                    ON CONFLICT(id) DO UPDATE SET rate_paise = $rate, created_at = $created;
                    """,
                    t, ("$id", r.Id), ("$employee", r.EmployeeId), ("$rate", r.Rate.Paise), ("$from", Day(r.EffectiveFrom)), ("$created", Utc(r.CreatedAt)));
            }

            foreach (var e in changes.Entries)
            {
                c.Run(
                    """
                    INSERT INTO production_entry (id, employee_id, entry_date, saree_count, entered_by, created_at, updated_at)
                    VALUES ($id, $employee, $date, $count, $by, $created, $updated)
                    ON CONFLICT(id) DO UPDATE SET saree_count = $count, entered_by = $by, updated_at = $updated;
                    """,
                    t, ("$id", e.Id), ("$employee", e.EmployeeId), ("$date", Day(e.Date)), ("$count", e.SareeCount), ("$by", (int)e.EnteredBy), ("$created", Utc(e.CreatedAt)), ("$updated", Utc(e.UpdatedAt)));
            }

            foreach (var id in changes.DeletedEntries.Select(e => e.Id))
            {
                c.Run("DELETE FROM production_entry WHERE id = $id;", t, ("$id", id));
            }

            foreach (var cr in changes.Credentials)
            {
                c.Run(
                    """
                    INSERT INTO credential (employee_id, pin_hash, pin_salt, pin_iterations, failed_attempts, locked_until)
                    VALUES ($id, $hash, $salt, $iterations, $failed, $locked)
                    ON CONFLICT(employee_id) DO UPDATE SET pin_hash = $hash, pin_salt = $salt, pin_iterations = $iterations,
                        failed_attempts = $failed, locked_until = $locked;
                    """,
                    t, ("$id", cr.EmployeeId), ("$hash", cr.Pin.Hash), ("$salt", cr.Pin.Salt), ("$iterations", cr.Pin.Iterations), ("$failed", cr.FailedAttempts), ("$locked", cr.LockedUntil is { } l ? Utc(l) : null));
            }

            foreach (var a in changes.Audit)
            {
                c.Run(
                    """
                    INSERT INTO audit_log (id, at, actor_id, action, target_type, target_id, old_value, new_value)
                    VALUES ($id, $at, $actor, $action, $type, $target, $old, $new);
                    """,
                    t, ("$id", a.Id), ("$at", Utc(a.At)), ("$actor", a.ActorId), ("$action", a.Action), ("$type", a.TargetType), ("$target", a.TargetId), ("$old", a.OldValue), ("$new", a.NewValue));
            }

            foreach (var p in changes.Payments)
            {
                c.Run(
                    """
                    INSERT INTO payment (id, employee_id, week_start, week_end, sarees, earnings_paise, adjustment_paise, total_paise, paid_date, marked_at)
                    VALUES ($id, $employee, $start, $end, $sarees, $earnings, $adjustment, $total, $paid, $marked)
                    ON CONFLICT(id) DO UPDATE SET sarees = $sarees, earnings_paise = $earnings, adjustment_paise = $adjustment,
                        total_paise = $total, paid_date = $paid, marked_at = $marked;
                    """,
                    t, ("$id", p.Id), ("$employee", p.EmployeeId), ("$start", Day(p.WeekStart)), ("$end", Day(p.WeekEnd)), ("$sarees", p.Sarees),
                    ("$earnings", p.Earnings.Paise), ("$adjustment", p.Adjustment.Paise), ("$total", p.Total.Paise), ("$paid", Day(p.PaidDate)), ("$marked", Utc(p.MarkedAt)));
            }

            foreach (var id in changes.DeletedPayments.Select(p => p.Id))
            {
                c.Run("DELETE FROM payment WHERE id = $id;", t, ("$id", id));
            }

            // The sync outbox is written in the same transaction as the change it reports (TD-03).
            foreach (var o in changes.Outbox)
            {
                c.Run("INSERT INTO outbox (channel, type, data) VALUES ($channel, $type, $data);", t, ("$channel", o.Channel), ("$type", o.Type), ("$data", o.Data));
            }
    }
}

/// <summary>Small app preferences (last update check, language…) kept in the encrypted database.</summary>
public sealed class SqliteSettings(FactoryDatabase db)
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Query("SELECT value FROM settings WHERE key = $key;", r => r.GetString(0), ("$key", key)).FirstOrDefault(), cancellationToken);

    public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => value is null
            ? c.Run("DELETE FROM settings WHERE key = $key;", null, ("$key", key))
            : c.Run("INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value;", null, ("$key", key), ("$value", value)), cancellationToken);
}

/// <summary>Opens this phone's database: the key file (wrapped by the platform key vault) and the encrypted file.</summary>
public static class FactoryDatabaseFiles
{
    public const string DatabaseFile = "factory.db";
    public const string KeyFile = "factory.key";

    public static FactoryDatabase Open(string folder, IKeyVault vault)
    {
        Directory.CreateDirectory(folder);
        var key = DatabaseKey.LoadOrCreate(Path.Combine(folder, KeyFile), vault);
        return FactoryDatabase.Open(Path.Combine(folder, DatabaseFile), key);
    }

    /// <summary>For a damaged install: removes the database and its key so the app can start fresh.</summary>
    public static void Erase(string folder)
    {
        SqliteConnection.ClearAllPools();
        foreach (var name in new[] { DatabaseFile, DatabaseFile + "-wal", DatabaseFile + "-shm", KeyFile })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
