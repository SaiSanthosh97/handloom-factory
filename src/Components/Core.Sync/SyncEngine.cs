using System.Text.Json;
using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>What one sync run did (shown as "last synced" and used by tests).</summary>
public sealed record SyncReport(int Received, int Sent, bool Unlinked, string? Error)
{
    public static readonly SyncReport Nothing = new(0, 0, false, null);

    public SyncReport Add(SyncReport other) =>
        new(Received + other.Received, Sent + other.Sent, Unlinked || other.Unlinked, Error ?? other.Error);
}

/// <summary>
/// Owner-hub sync through the relay (D1, D5, TD-03). The owner's phone has one channel per linked worker; a worker's
/// phone has exactly one. Every run, for each channel: fetch new messages → verify and apply → answer requests →
/// ask for everything again if away longer than the relay keeps messages → send the outbox → check in.
/// </summary>
/// <param name="data">The factory store WITHOUT the outbox wrapper: records arriving from sync must not be echoed back.</param>
public sealed partial class SyncEngine(IFactoryStore data, ISyncStore store, IRelayFactory relays, IClock clock, string appVersion)
{
    /// <summary>ntfy.sh keeps messages about 12 h; after 11 h away a phone asks for everything again.</summary>
    public static readonly TimeSpan AwayLimit = TimeSpan.FromHours(11);

    public static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How far back a snapshot for a worker's phone goes (their history screen shows 12 weeks).</summary>
    public const int SnapshotDays = 400;

    private static readonly TimeSpan CheckInEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan StateRequestEvery = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string AppVersion { get; } = appVersion;

    /// <summary>Syncs every channel. Safe to call from the background job and the screens at the same time.</summary>
    public async Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var role = await EnsureRoleAsync(cancellationToken);
            var report = SyncReport.Nothing;
            foreach (var channel in await store.GetChannelsAsync(cancellationToken))
            {
                report = report.Add(await SyncChannelCoreAsync(role, channel, cancellationToken));
                if (report.Unlinked)
                {
                    break;
                }
            }

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Syncs one worker's channel (the owner's "waiting for the phone" screen polls this).</summary>
    public async Task<SyncReport> SyncChannelAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var role = await EnsureRoleAsync(cancellationToken);
            return (await store.GetChannelsAsync(cancellationToken)).FirstOrDefault(c => c.EmployeeId == employeeId) is { } channel
                ? await SyncChannelCoreAsync(role, channel, cancellationToken)
                : SyncReport.Nothing;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Channel?> ChannelAsync(string employeeId, CancellationToken cancellationToken = default) =>
        (await store.GetChannelsAsync(cancellationToken)).FirstOrDefault(c => c.EmployeeId == employeeId);

    public Task<PhoneRole> RoleAsync(CancellationToken cancellationToken = default) => store.GetRoleAsync(cancellationToken);

    /// <summary>Records waiting to be sent (shown as "Not sent yet").</summary>
    public async Task<int> PendingAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var channel in await store.GetChannelsAsync(cancellationToken))
        {
            count += (await store.GetOutboxAsync(channel.EmployeeId, cancellationToken)).Count;
        }

        return count;
    }

    /// <summary>A phone with a factory and an owner, and no role yet, is the owner's phone.</summary>
    private async Task<PhoneRole> EnsureRoleAsync(CancellationToken cancellationToken)
    {
        var role = await store.GetRoleAsync(cancellationToken);
        if (role == PhoneRole.None && await data.GetFactoryAsync(cancellationToken) is not null
            && (await data.GetEmployeesAsync(cancellationToken)).Any(e => e.Role == Role.Admin))
        {
            role = PhoneRole.Owner;
            await store.SetRoleAsync(role, cancellationToken);
        }

        return role;
    }

    private async Task<SyncReport> SyncChannelCoreAsync(PhoneRole role, Channel channel, CancellationToken cancellationToken)
    {
        var relay = relays.For(channel.RelayUrl);
        try
        {
            var messages = await relay.FetchAsync(channel.Topic, channel.LastMessageId, cancellationToken);
            var (updated, received, unlinked) = role == PhoneRole.Owner
                ? await OwnerReceiveAsync(relay, channel, messages, cancellationToken)
                : await WorkerReceiveAsync(relay, channel, messages, cancellationToken);
            if (unlinked)
            {
                return new SyncReport(received, 0, true, null);
            }

            if (updated is null)
            {
                return new SyncReport(received, 0, false, null); // channel closed (revoked by the owner)
            }

            channel = updated with { LastMessageId = messages.Count > 0 ? messages[^1].Id : updated.LastMessageId };
            var sent = 0;
            if (channel.IsLinked)
            {
                var now = clock.UtcNow;
                if ((channel.LastSyncAt is null || now - channel.LastSyncAt > AwayLimit)
                    && (channel.LastStateRequestAt is null || now - channel.LastStateRequestAt > StateRequestEvery))
                {
                    channel = await SendAsync(relay, channel, role, Kinds.StateRequest, new StateRequestBody(Ids.New()), cancellationToken);
                    channel = channel with { LastStateRequestAt = now };
                    sent++;
                }

                (channel, var flushed, var closed) = await FlushAsync(relay, channel, role, cancellationToken);
                sent += flushed;
                if (closed)
                {
                    return new SyncReport(received, sent, false, null);
                }

                if (role == PhoneRole.Worker && (channel.LastSeenAt is null || now - channel.LastSeenAt > CheckInEvery))
                {
                    // On the worker's phone LastSeenAt is "last check-in sent".
                    channel = await SendAsync(relay, channel, role, Kinds.CheckIn, new CheckInBody(AppVersion), cancellationToken);
                    channel = channel with { LastSeenAt = now };
                    sent++;
                }

                channel = channel with { LastSyncAt = now };
            }

            await store.SaveChannelAsync(channel, cancellationToken);
            return new SyncReport(received, sent, false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SyncReport(0, 0, false, ex.Message);
        }
    }

    /// <summary>Encodes, signs and publishes one message; returns the channel with the next sequence number.</summary>
    private async Task<Channel> SendAsync<T>(IRelay relay, Channel channel, PhoneRole role, string kind, T body, CancellationToken cancellationToken)
    {
        using var key = await store.GetSigningKeyAsync(cancellationToken);
        var envelope = Envelope.Create(channel.EmployeeId, role == PhoneRole.Owner ? Senders.Owner : Senders.Worker, channel.NextSeq, clock.UtcNow, kind, body);
        await relay.PublishAsync(channel.Topic, WireCodec.Encode(envelope, channel.Key, key), cancellationToken);
        channel = channel with { NextSeq = channel.NextSeq + 1 };
        await store.SaveChannelAsync(channel, cancellationToken); // never reuse a sequence number, even if a later step fails
        return channel;
    }

    /// <summary>Sends records, packed into as few messages as fit (each under the relay's size limit).</summary>
    private async Task<(Channel Channel, int Messages)> SendRecordsAsync(IRelay relay, Channel channel, PhoneRole role, List<SyncRecord> records, bool full, DateOnly? since, CancellationToken cancellationToken)
    {
        if (records.Count == 0 && !full)
        {
            return (channel, 0);
        }

        using var key = await store.GetSigningKeyAsync(cancellationToken);
        var snapshotId = full ? Ids.New() : null;
        var batches = WireCodec.Pack(records, batch =>
        {
            try
            {
                WireCodec.Encode(Envelope.Create(channel.EmployeeId, Senders.Owner, long.MaxValue, clock.UtcNow, Kinds.Changes, new ChangesBody(batch, full, since, snapshotId, 99, 99)), channel.Key, key);
                return true;
            }
            catch (MessageTooLargeException)
            {
                return false;
            }
        });

        for (var i = 0; i < batches.Count; i++)
        {
            channel = await SendAsync(relay, channel, role, Kinds.Changes, new ChangesBody(batches[i], full, since, snapshotId, i + 1, batches.Count), cancellationToken);
        }

        return (channel, batches.Count);
    }

    /// <summary>Sends everything queued for the channel; a queued "revoke" also unlinks the worker's phone and closes the channel.</summary>
    private async Task<(Channel Channel, int Messages, bool Closed)> FlushAsync(IRelay relay, Channel channel, PhoneRole role, CancellationToken cancellationToken)
    {
        var queued = await store.GetOutboxAsync(channel.EmployeeId, cancellationToken);
        if (queued.Count == 0)
        {
            return (channel, 0, false);
        }

        var records = queued.Where(q => q.Item.Type is not (RecordTypes.Revoke or RecordTypes.Snapshot)).Select(q => new SyncRecord(q.Item.Type, q.Item.Data)).ToList();
        (channel, var sent) = await SendRecordsAsync(relay, channel, role, records, full: false, since: null, cancellationToken);
        if (role == PhoneRole.Owner && queued.Any(q => q.Item.Type == RecordTypes.Snapshot))
        {
            channel = await SendSnapshotAsync(relay, channel, cancellationToken);
            sent++;
        }

        await store.DeleteOutboxAsync(queued.Select(q => q.Id), cancellationToken);

        if (role == PhoneRole.Owner && queued.Any(q => q.Item.Type == RecordTypes.Revoke))
        {
            await SendAsync(relay, channel, role, Kinds.Revoked, new { }, cancellationToken);
            await store.DeleteChannelAsync(channel.EmployeeId, cancellationToken);
            return (channel, sent + 1, true);
        }

        return (channel, sent, false);
    }

    private static T? Read<T>(SyncRecord record) => JsonSerializer.Deserialize<T>(record.Data, FactoryJson.Compact);
}
