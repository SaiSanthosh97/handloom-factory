using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>The owner's side: invites, unlinking, receiving workers' entries, snapshots for workers' phones.</summary>
public sealed partial class SyncEngine
{
    /// <summary>
    /// Creates (or replaces) a worker's channel and returns the one-time invite to show as a QR code (valid 15 min).
    /// If the worker already had a linked phone, that phone is told it is unlinked first (re-link: new phone or
    /// forgotten PIN).
    /// </summary>
    public async Task<Invite> CreateInviteAsync(string employeeId, string relayUrl, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var role = await EnsureRoleAsync(cancellationToken);
            if (role != PhoneRole.Owner)
            {
                throw new InvalidOperationException("Only the owner's phone creates invites.");
            }

            var factory = await data.GetFactoryAsync(cancellationToken) ?? throw new InvalidOperationException("No factory.");
            var worker = (await data.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == employeeId && e.Role == Role.Employee && e.IsActive)
                ?? throw new InvalidOperationException("No such active worker.");

            if ((await store.GetChannelsAsync(cancellationToken)).FirstOrDefault(c => c.EmployeeId == employeeId) is { IsLinked: true } old)
            {
                try
                {
                    await SendAsync(relays.For(old.RelayUrl), old, role, Kinds.Revoked, new { }, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // Offline: the old phone keeps its copy but can never sync again (new topic and key).
                }
            }

            using var key = await store.GetSigningKeyAsync(cancellationToken);
            var now = clock.UtcNow;
            var secret = AeadBox.NewKey();
            var channel = new Channel(employeeId, relayUrl, RandomText.New(), AeadBox.NewKey(), key.PublicKey, null, ChannelState.Inviting,
                RandomText.New(16), secret, now + InviteLifetime, null, null, null, null, 1, 0, now);
            await store.SaveChannelAsync(channel, cancellationToken);

            return new Invite(1, factory.Name, employeeId, worker.FullName, relayUrl, channel.Topic, Base64Url.Encode(channel.Key),
                Base64Url.Encode(key.PublicKey), channel.InviteId!, Base64Url.Encode(secret), (now + InviteLifetime).ToUnixTimeMilliseconds());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The owner closed the QR screen before the phone linked: forget the unused invite.</summary>
    public async Task CancelInviteAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        if (await ChannelAsync(employeeId, cancellationToken) is { State: ChannelState.Inviting })
        {
            await store.DeleteChannelAsync(employeeId, cancellationToken);
        }
    }

    /// <summary>Unlinks a worker's phone now: it is told so (it wipes its copy) and the channel is closed.</summary>
    public async Task UnlinkAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await ChannelAsync(employeeId, cancellationToken) is not { } channel)
            {
                return;
            }

            if (channel.IsLinked)
            {
                try
                {
                    await SendAsync(relays.For(channel.RelayUrl), channel, PhoneRole.Owner, Kinds.Revoked, new { }, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // Offline: closing the channel still cuts the phone off from all future data.
                }
            }

            await store.DeleteChannelAsync(employeeId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>After an import replaced the data: send every linked worker's phone a fresh snapshot.</summary>
    /// <remarks>Queued in the outbox, so it survives being offline; the snapshot is built when it is sent.</remarks>
    public async Task ResendAllAsync(CancellationToken cancellationToken = default) =>
        await store.AddOutboxAsync(
            (await store.GetChannelsAsync(cancellationToken)).Where(c => c.IsLinked).Select(c => new OutboxItem(c.EmployeeId, RecordTypes.Snapshot, "{}")),
            cancellationToken);

    private async Task<(Channel? Channel, int Received, bool Unlinked)> OwnerReceiveAsync(IRelay relay, Channel channel, IReadOnlyList<RelayMessage> messages, CancellationToken cancellationToken)
    {
        var received = 0;
        foreach (var message in messages)
        {
            if (channel.State == ChannelState.Inviting)
            {
                if (await TryAcceptLinkAsync(relay, channel, message, cancellationToken) is { } linked)
                {
                    channel = linked;
                    received++;
                }

                continue;
            }

            if (WireCodec.Decode(message.Text, channel.EmployeeId, channel.Key, channel.WorkerKey) is not { } decoded
                || decoded.Envelope.From != Senders.Worker || decoded.Envelope.Seq <= channel.PeerSeq)
            {
                continue; // our own message, a replay, or something not from this worker's phone
            }

            var envelope = decoded.Envelope;
            channel = channel with { PeerSeq = envelope.Seq, LastSeenAt = clock.UtcNow };
            received++;
            switch (envelope.Kind)
            {
                case Kinds.Changes when envelope.BodyAs<ChangesBody>() is { } body:
                    await ApplyFromWorkerAsync(channel.EmployeeId, body.Records, cancellationToken);
                    break;
                case Kinds.StateRequest:
                    channel = await SendSnapshotAsync(relay, channel, cancellationToken);
                    break;
            }
        }

        return (channel, received, false);
    }

    /// <summary>
    /// A worker's phone asks to link: the invite must be this channel's, unused and unexpired, and the phone must prove
    /// it knows the invite secret. Then its key is pinned and it gets its snapshot.
    /// </summary>
    private async Task<Channel?> TryAcceptLinkAsync(IRelay relay, Channel channel, RelayMessage message, CancellationToken cancellationToken)
    {
        if (WireCodec.Decode(message.Text, channel.EmployeeId, channel.Key, senderKey: null) is not { } decoded
            || decoded.Envelope.Kind != Kinds.Link || decoded.Envelope.From != Senders.Worker
            || decoded.Envelope.BodyAs<LinkBody>() is not { } link
            || link.InviteId != channel.InviteId || channel.InviteSecret is null
            || channel.InviteExpires is not { } expires || clock.UtcNow > expires
            || Base64Url.TryDecode(link.WorkerKey) is not { } workerKey
            || Base64Url.TryDecode(link.Proof) is not { } proof
            || !Proofs.Check(channel.InviteSecret, Invite.ProofBytes(link.InviteId, link.WorkerKey), proof)
            || !Signatures.Verify(workerKey, decoded.Box, decoded.Signature)
            || Base64Url.TryDecode(link.WorkerAgreement) is not { } workerHalf)
        {
            return null;
        }

        // New channel key from a key agreement with THIS phone: whoever else saw the QR can't read anything from now on.
        var (ownerPrivate, ownerPublic) = KeyAgreement.Create();
        if (KeyAgreement.Derive(ownerPrivate, workerHalf, channel.Key) is not { } newKey)
        {
            return null;
        }

        channel = channel with { PeerSeq = decoded.Envelope.Seq };
        channel = await SendAsync(relay, channel, PhoneRole.Owner, Kinds.Linked, new LinkedBody(link.WorkerKey, Base64Url.Encode(ownerPublic)), cancellationToken);

        var now = clock.UtcNow;
        channel = channel with
        {
            Key = newKey,
            WorkerKey = workerKey,
            State = ChannelState.Linked,
            InviteId = null,
            InviteSecret = null,
            InviteExpires = null,
            PeerSeq = decoded.Envelope.Seq,
            LastSeenAt = now,
            LastSyncAt = now,
        };
        await store.SaveChannelAsync(channel, cancellationToken);

        var worker = (await data.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == channel.EmployeeId);
        var changes = new ChangeSet();
        changes.Audit.Add(new AuditEntry(Ids.New(), now, null, AuditActions.PhoneLinked, "Employee", channel.EmployeeId, null, $"{worker?.FullName}: app {link.AppVersion}"));
        await data.CommitAsync(changes, cancellationToken);
        return await SendSnapshotAsync(relay, channel, cancellationToken);
    }

    /// <summary>Everything the worker's phone should hold, as a full snapshot (their own records only).</summary>
    private async Task<Channel> SendSnapshotAsync(IRelay relay, Channel channel, CancellationToken cancellationToken)
    {
        var id = channel.EmployeeId;
        var factory = await data.GetFactoryAsync(cancellationToken);
        var worker = (await data.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == id);
        if (factory is null || worker is null)
        {
            return channel;
        }

        var today = new FactoryTime(clock, FactoryTime.FindZone(factory.TimeZoneId)).Today;
        var since = today.AddDays(-SnapshotDays);
        var records = new List<SyncRecord>
        {
            Record(RecordTypes.Factory, factory),
            Record(RecordTypes.Employee, worker),
            Record(RecordTypes.Settings, new SharedSettings(await store.GetSettingAsync(SyncSettings.NotifyTopic, cancellationToken))),
        };
        records.AddRange((await data.GetRateChangesAsync(cancellationToken)).Where(r => r.EmployeeId == id).Select(r => Record(RecordTypes.Rate, r)));
        records.AddRange((await data.GetEntriesAsync(new DateRange(since, DateOnly.MaxValue), id, cancellationToken)).Select(e => Record(RecordTypes.Entry, e)));
        records.AddRange((await data.GetPaymentsAsync(id, cancellationToken)).Where(p => p.WeekEnd >= since).Select(p => Record(RecordTypes.Payment, p)));
        (channel, _) = await SendRecordsAsync(relay, channel, PhoneRole.Owner, records, full: true, since, cancellationToken);
        return channel;
    }

    /// <summary>
    /// Entries from a worker's phone. Accepted only if (D9, D10): it is the worker's own entry, entered by the worker,
    /// for the day it was typed on (factory time zone; a late upload is fine), within the daily limit, and the owner
    /// hasn't set that day already. Anything else is ignored.
    /// </summary>
    private async Task ApplyFromWorkerAsync(string workerId, List<SyncRecord> records, CancellationToken cancellationToken)
    {
        var factory = await data.GetFactoryAsync(cancellationToken);
        if (factory is null)
        {
            return;
        }

        var zone = FactoryTime.FindZone(factory.TimeZoneId);
        var worker = (await data.GetEmployeesAsync(cancellationToken)).FirstOrDefault(e => e.Id == workerId);
        var changes = new ChangeSet();
        foreach (var record in records.Where(r => r.Type == RecordTypes.Entry))
        {
            if (Read<ProductionEntry>(record) is not { } entry
                || entry.EmployeeId != workerId || entry.EnteredBy != EnteredBy.Employee
                || entry.SareeCount < 0 || entry.SareeCount > factory.MaxSareesPerDay
                || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.UpdatedAt, zone).DateTime) != entry.Date)
            {
                continue;
            }

            var existing = (await data.GetEntriesAsync(new DateRange(entry.Date, entry.Date), workerId, cancellationToken)).FirstOrDefault();
            if (existing is not null && (existing.EnteredBy == EnteredBy.Admin || existing.UpdatedAt >= entry.UpdatedAt))
            {
                continue;
            }

            var accepted = entry with { Id = ProductionEntry.IdFor(workerId, entry.Date) };
            changes.Entries.Add(accepted);
            changes.Audit.Add(new AuditEntry(Ids.New(), clock.UtcNow, workerId, AuditActions.EntrySaved, "Entry", accepted.Id,
                existing is null ? null : $"{worker?.FullName} {existing.Date:yyyy-MM-dd}: {existing.SareeCount}",
                $"{worker?.FullName} {entry.Date:yyyy-MM-dd}: {entry.SareeCount} (from worker's phone)"));
        }

        if (changes.Entries.Count > 0)
        {
            await data.CommitAsync(changes, cancellationToken);
        }
    }

    private static SyncRecord Record<T>(string type, T value) => new(type, System.Text.Json.JsonSerializer.Serialize(value, FactoryJson.Compact));
}
