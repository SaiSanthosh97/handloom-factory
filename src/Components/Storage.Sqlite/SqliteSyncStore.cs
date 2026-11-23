using HandloomFactory.Core.Security;
using HandloomFactory.Core.Sync;
using HandloomFactory.Core.Workflow;
using static HandloomFactory.Storage.Sqlite.SqlHelpers;

namespace HandloomFactory.Storage.Sqlite;

/// <summary>Sync bookkeeping in the encrypted database: role, signing key, channels (with their keys), outbox.</summary>
public sealed class SqliteSyncStore(FactoryDatabase db) : ISyncStore
{
    private readonly SqliteSettings _settings = new(db);

    public async Task<PhoneRole> GetRoleAsync(CancellationToken cancellationToken = default) =>
        Enum.TryParse<PhoneRole>(await _settings.GetAsync(SyncSettings.Role, cancellationToken), out var role) ? role : PhoneRole.None;

    public Task SetRoleAsync(PhoneRole role, CancellationToken cancellationToken = default) =>
        _settings.SetAsync(SyncSettings.Role, role == PhoneRole.None ? null : role.ToString(), cancellationToken);

    public async Task<SigningKey> GetSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        if (await _settings.GetAsync(SyncSettings.SigningKey, cancellationToken) is { } stored)
        {
            return SigningKey.Import(Convert.FromBase64String(stored));
        }

        var key = SigningKey.Create();
        await _settings.SetAsync(SyncSettings.SigningKey, Convert.ToBase64String(key.ExportPrivate()), cancellationToken);
        return key;
    }

    public Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<Channel>>(c => c.Query(
            """
            SELECT employee_id, relay_url, topic, channel_key, owner_key, worker_key, state, invite_id, invite_secret, invite_expires,
                   last_message_id, last_sync_at, last_seen_at, last_state_request_at, next_seq, peer_seq, created_at
            FROM sync_channel ORDER BY created_at;
            """,
            r => new Channel(
                r.GetString(0), r.GetString(1), r.GetString(2), (byte[])r[3], (byte[])r[4], r.IsDBNull(5) ? null : (byte[])r[5],
                (ChannelState)r.GetInt32(6), r.NullableString(7), r.IsDBNull(8) ? null : (byte[])r[8], Time(r.NullableString(9)),
                r.NullableString(10), Time(r.NullableString(11)), Time(r.NullableString(12)), Time(r.NullableString(13)),
                r.GetInt64(14), r.GetInt64(15), ParseUtc(r.GetString(16)))), cancellationToken);

    public Task SaveChannelAsync(Channel ch, CancellationToken cancellationToken = default) =>
        db.UseAsync(c => c.Run(
            """
            INSERT INTO sync_channel (employee_id, relay_url, topic, channel_key, owner_key, worker_key, state, invite_id, invite_secret,
                invite_expires, last_message_id, last_sync_at, last_seen_at, last_state_request_at, next_seq, peer_seq, created_at)
            VALUES ($id, $relay, $topic, $key, $owner, $worker, $state, $invite, $secret, $expires, $msg, $sync, $seen, $req, $next, $peer, $created)
            ON CONFLICT(employee_id) DO UPDATE SET relay_url = $relay, topic = $topic, channel_key = $key, owner_key = $owner,
                worker_key = $worker, state = $state, invite_id = $invite, invite_secret = $secret, invite_expires = $expires,
                last_message_id = $msg, last_sync_at = $sync, last_seen_at = $seen, last_state_request_at = $req, next_seq = $next,
                peer_seq = $peer, created_at = $created;
            """,
            null,
            ("$id", ch.EmployeeId), ("$relay", ch.RelayUrl), ("$topic", ch.Topic), ("$key", ch.Key), ("$owner", ch.OwnerKey), ("$worker", ch.WorkerKey),
            ("$state", (int)ch.State), ("$invite", ch.InviteId), ("$secret", ch.InviteSecret), ("$expires", Text(ch.InviteExpires)),
            ("$msg", ch.LastMessageId), ("$sync", Text(ch.LastSyncAt)), ("$seen", Text(ch.LastSeenAt)), ("$req", Text(ch.LastStateRequestAt)),
            ("$next", ch.NextSeq), ("$peer", ch.PeerSeq), ("$created", Utc(ch.CreatedAt))), cancellationToken);

    public Task DeleteChannelAsync(string employeeId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            c.Run("DELETE FROM outbox WHERE channel = $id;", t, ("$id", employeeId));
            c.Run("DELETE FROM sync_channel WHERE employee_id = $id;", t, ("$id", employeeId));
        }, cancellationToken);

    public Task<IReadOnlyList<(long Id, OutboxItem Item)>> GetOutboxAsync(string channel, CancellationToken cancellationToken = default) =>
        db.UseAsync<IReadOnlyList<(long, OutboxItem)>>(c => c.Query(
            "SELECT id, channel, type, data FROM outbox WHERE channel = $channel ORDER BY id;",
            r => (r.GetInt64(0), new OutboxItem(r.GetString(1), r.GetString(2), r.GetString(3))),
            ("$channel", channel)), cancellationToken);

    public Task DeleteOutboxAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        var list = ids.ToList();
        return db.InTransactionAsync((c, t) =>
        {
            foreach (var id in list)
            {
                c.Run("DELETE FROM outbox WHERE id = $id;", t, ("$id", id));
            }
        }, cancellationToken);
    }

    public Task AddOutboxAsync(IEnumerable<OutboxItem> items, CancellationToken cancellationToken = default)
    {
        var list = items.ToList();
        return db.InTransactionAsync((c, t) =>
        {
            foreach (var o in list)
            {
                c.Run("INSERT INTO outbox (channel, type, data) VALUES ($channel, $type, $data);", t, ("$channel", o.Channel), ("$type", o.Type), ("$data", o.Data));
            }
        }, cancellationToken);
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => _settings.GetAsync(key, cancellationToken);

    public Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default) => _settings.SetAsync(key, value, cancellationToken);

    /// <summary>Everything about the factory and the link goes; the phone's signing key stays (harmless, reused if linked again).</summary>
    public Task WipeAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync((c, t) =>
        {
            foreach (var table in new[] { "outbox", "sync_channel", "credential", "payment", "production_entry", "rate_change", "audit_log", "employee", "factory" })
            {
                c.Run($"DELETE FROM {table};", t);
            }

            c.Run("DELETE FROM settings WHERE key = $role OR key = $notify;", t, ("$role", SyncSettings.Role), ("$notify", SyncSettings.NotifyTopic));
        }, cancellationToken);

    private static string? Text(DateTimeOffset? value) => value is { } v ? Utc(v) : null;

    private static DateTimeOffset? Time(string? text) => text is null ? null : ParseUtc(text);
}
