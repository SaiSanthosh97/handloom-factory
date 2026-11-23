using HandloomFactory.Core.Common;
using HandloomFactory.Core.Factory;
using HandloomFactory.Core.Relay;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>The worker's phone: joining with the owner's invite, receiving its own records, being unlinked.</summary>
public sealed partial class SyncEngine
{
    /// <summary>
    /// A new phone scans (or pastes) the owner's invite: it becomes that worker's phone, sends its signing key with a
    /// proof of the invite secret, and waits for the owner's phone to confirm with a snapshot.
    /// </summary>
    public async Task<Result> JoinAsync(string inviteText, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Invite.TryParse(inviteText) is not { } invite)
            {
                return Result.Fail(ErrorCode.InviteInvalid);
            }

            if (clock.UtcNow > invite.ExpiresAt)
            {
                return Result.Fail(ErrorCode.InviteExpired);
            }

            if (await store.GetRoleAsync(cancellationToken) == PhoneRole.Owner || await data.GetFactoryAsync(cancellationToken) is not null)
            {
                return Result.Fail(ErrorCode.AlreadySetUp);
            }

            var key = Base64Url.TryDecode(invite.Key)!;
            var ownerKey = Base64Url.TryDecode(invite.OwnerKey);
            if (ownerKey is null)
            {
                return Result.Fail(ErrorCode.InviteInvalid);
            }

            // A phone that scanned before but never got confirmed starts over with this invite.
            foreach (var old in await store.GetChannelsAsync(cancellationToken))
            {
                await store.DeleteChannelAsync(old.EmployeeId, cancellationToken);
            }

            await store.SetRoleAsync(PhoneRole.Worker, cancellationToken);
            using var signing = await store.GetSigningKeyAsync(cancellationToken);
            var workerKey = Base64Url.Encode(signing.PublicKey);
            var secret = Base64Url.TryDecode(invite.Secret)!;
            var channel = new Channel(invite.EmployeeId, invite.Relay, invite.Topic, key, ownerKey, signing.PublicKey, ChannelState.Joining,
                invite.InviteId, null, invite.ExpiresAt, null, null, null, null, 1, 0, clock.UtcNow);
            await store.SaveChannelAsync(channel, cancellationToken);

            var proof = Base64Url.Encode(Proofs.Make(secret, Invite.ProofBytes(invite.InviteId, workerKey)));
            var (agreementPrivate, agreementPublic) = KeyAgreement.Create();
            await store.SetSettingAsync(SyncSettings.LinkAgreement, Convert.ToBase64String(agreementPrivate), cancellationToken);
            try
            {
                await SendAsync(relays.For(channel.RelayUrl), channel, PhoneRole.Worker, Kinds.Link,
                    new LinkBody(invite.InviteId, workerKey, proof, AppVersion, Base64Url.Encode(agreementPublic)), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await store.DeleteChannelAsync(channel.EmployeeId, cancellationToken);
                await store.SetRoleAsync(PhoneRole.None, cancellationToken);
                return Result.Fail(ErrorCode.NoInternet);
            }

            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The worker's phone: linked (snapshot received), still waiting, or not linked at all.</summary>
    public async Task<ChannelState?> WorkerStateAsync(CancellationToken cancellationToken = default) =>
        (await store.GetChannelsAsync(cancellationToken)).FirstOrDefault()?.State;

    /// <summary>Gives up waiting for the owner (wrong QR, owner offline too long): back to the first screen.</summary>
    public async Task CancelJoinAsync(CancellationToken cancellationToken = default)
    {
        if ((await store.GetChannelsAsync(cancellationToken)).FirstOrDefault() is { State: ChannelState.Joining } channel)
        {
            await store.DeleteChannelAsync(channel.EmployeeId, cancellationToken);
            await store.SetRoleAsync(PhoneRole.None, cancellationToken);
        }
    }

    private async Task<(Channel? Channel, int Received, bool Unlinked)> WorkerReceiveAsync(IRelay relay, Channel channel, IReadOnlyList<RelayMessage> messages, CancellationToken cancellationToken)
    {
        var received = 0;
        var snapshots = new Dictionary<string, (ChangesBody First, HashSet<string> Entries, HashSet<string> Payments, int Parts)>();
        foreach (var message in messages)
        {
            if (WireCodec.Decode(message.Text, channel.EmployeeId, channel.Key, channel.OwnerKey) is not { } decoded
                || decoded.Envelope.From != Senders.Owner || decoded.Envelope.Seq <= channel.PeerSeq)
            {
                continue; // our own messages, replays, or anything not signed by the owner's phone
            }

            var envelope = decoded.Envelope;
            channel = channel with { PeerSeq = envelope.Seq };
            received++;
            switch (envelope.Kind)
            {
                case Kinds.Revoked:
                    await store.WipeAsync(cancellationToken);
                    return (null, received, true);

                // The owner accepted A phone: if it is this one, switch to the new key agreed with the owner. Another
                // phone that scanned the same QR can't derive that key and stays unlinked.
                case Kinds.Linked when channel.State == ChannelState.Joining && envelope.BodyAs<LinkedBody>() is { } linked:
                    if (linked.WorkerKey == Base64Url.Encode(channel.WorkerKey ?? [])
                        && await store.GetSettingAsync(SyncSettings.LinkAgreement, cancellationToken) is { } agreementPrivate
                        && Base64Url.TryDecode(linked.OwnerAgreement) is { } ownerHalf
                        && KeyAgreement.Derive(Convert.FromBase64String(agreementPrivate), ownerHalf, channel.Key) is { } newKey)
                    {
                        channel = channel with { Key = newKey };
                        await store.SetSettingAsync(SyncSettings.LinkAgreement, null, cancellationToken);
                        await store.SaveChannelAsync(channel, cancellationToken);
                    }

                    break;

                case Kinds.Changes when envelope.BodyAs<ChangesBody>() is { } body:
                    await ApplyFromOwnerAsync(channel.EmployeeId, body.Records, cancellationToken);
                    if (body is { Full: true, SnapshotId: { } id })
                    {
                        var seen = snapshots.TryGetValue(id, out var s) ? s : (body, new HashSet<string>(), new HashSet<string>(), 0);
                        foreach (var r in body.Records)
                        {
                            if (r.Type == RecordTypes.Entry && Read<ProductionEntry>(r) is { } e)
                            {
                                seen.Item2.Add(e.Id);
                            }
                            else if (r.Type == RecordTypes.Payment && Read<Payment>(r) is { } p)
                            {
                                seen.Item3.Add(p.Id);
                            }
                        }

                        snapshots[id] = (seen.Item1, seen.Item2, seen.Item3, seen.Item4 + 1);
                        if (snapshots[id].Parts == body.Parts)
                        {
                            await ReconcileAsync(channel.EmployeeId, body.Since, snapshots[id].Entries, snapshots[id].Payments, cancellationToken);
                            if (channel.State == ChannelState.Joining)
                            {
                                var now = clock.UtcNow;
                                channel = channel with { State = ChannelState.Linked, InviteId = null, InviteExpires = null, LastSyncAt = now };
                            }
                        }
                    }

                    break;

                case Kinds.StateRequest when channel.IsLinked:
                    // The owner's phone missed messages: send this worker's own recent entries again.
                    var since = (await TodayAsync(cancellationToken)).AddDays(-60);
                    var own = (await data.GetEntriesAsync(new DateRange(since, DateOnly.MaxValue), channel.EmployeeId, cancellationToken))
                        .Where(e => e.EnteredBy == EnteredBy.Employee).Select(e => Record(RecordTypes.Entry, e)).ToList();
                    (channel, _) = await SendRecordsAsync(relay, channel, PhoneRole.Worker, own, full: false, since: null, cancellationToken);
                    break;
            }
        }

        return (channel, received, false);
    }

    /// <summary>
    /// Records from the owner's phone (signed by it, so trusted), limited to this worker's own data. The owner's value
    /// for a day always wins (D10); the worker's own newer edit wins over an older copy from the owner.
    /// </summary>
    private async Task ApplyFromOwnerAsync(string self, List<SyncRecord> records, CancellationToken cancellationToken)
    {
        var changes = new ChangeSet();
        foreach (var record in records)
        {
            switch (record.Type)
            {
                case RecordTypes.Factory when Read<FactoryInfo>(record) is { } f:
                    changes.Factory = f;
                    break;
                case RecordTypes.Employee when Read<Employee>(record) is { } e && e.Id == self:
                    changes.Employees.Add(e);
                    break;
                case RecordTypes.Rate when Read<RateChange>(record) is { } r && r.EmployeeId == self:
                    changes.Rates.Add(r);
                    break;
                case RecordTypes.Entry when Read<ProductionEntry>(record) is { } e && e.EmployeeId == self:
                    var local = (await data.GetEntriesAsync(new DateRange(e.Date, e.Date), self, cancellationToken)).FirstOrDefault();
                    if (e.EnteredBy == EnteredBy.Admin || local is null || e.UpdatedAt >= local.UpdatedAt)
                    {
                        changes.Entries.Add(e);
                    }

                    break;
                case RecordTypes.EntryDeleted when Read<ProductionEntry>(record) is { } e && e.EmployeeId == self:
                    changes.DeletedEntries.Add(e);
                    break;
                case RecordTypes.Payment when Read<Payment>(record) is { } p && p.EmployeeId == self:
                    changes.Payments.Add(p);
                    break;
                case RecordTypes.PaymentDeleted when Read<Payment>(record) is { } p && p.EmployeeId == self:
                    changes.DeletedPayments.Add(p);
                    break;
                case RecordTypes.Settings when Read<SharedSettings>(record) is { } s:
                    await store.SetSettingAsync(SyncSettings.NotifyTopic, s.NotifyTopic, cancellationToken);
                    break;
            }
        }

        await data.CommitAsync(changes, cancellationToken);
    }

    /// <summary>
    /// After a complete snapshot: anything of this worker from <paramref name="since"/> on that the owner no longer has
    /// was deleted there (deletes it here), except the worker's own entries, which are sent again (not lost).
    /// </summary>
    private async Task ReconcileAsync(string self, DateOnly? since, HashSet<string> entries, HashSet<string> payments, CancellationToken cancellationToken)
    {
        if (since is not { } from)
        {
            return;
        }

        var changes = new ChangeSet();
        var resend = new List<OutboxItem>();
        foreach (var local in await data.GetEntriesAsync(new DateRange(from, DateOnly.MaxValue), self, cancellationToken))
        {
            if (!entries.Contains(local.Id))
            {
                if (local.EnteredBy == EnteredBy.Admin)
                {
                    changes.DeletedEntries.Add(local);
                }
                else
                {
                    resend.Add(OutboxStore.Item(self, RecordTypes.Entry, local));
                }
            }
        }

        changes.DeletedPayments.AddRange((await data.GetPaymentsAsync(self, cancellationToken)).Where(p => p.WeekEnd >= from && !payments.Contains(p.Id)));
        if (changes.DeletedEntries.Count + changes.DeletedPayments.Count > 0)
        {
            await data.CommitAsync(changes, cancellationToken);
        }

        if (resend.Count > 0)
        {
            await store.AddOutboxAsync(resend, cancellationToken);
        }
    }

    private async Task<DateOnly> TodayAsync(CancellationToken cancellationToken) =>
        new FactoryTime(clock, FactoryTime.FindZone((await data.GetFactoryAsync(cancellationToken))?.TimeZoneId)).Today;
}
