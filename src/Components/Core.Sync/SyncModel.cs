using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>What this phone is in the factory (D5): the owner's hub phone, or one worker's own phone.</summary>
public enum PhoneRole
{
    None = 0,
    Owner = 1,
    Worker = 2,
}

public enum ChannelState
{
    /// <summary>The owner showed an invite; waiting for the worker's phone (owner side).</summary>
    Inviting = 0,

    /// <summary>The worker scanned the invite and asked to link; waiting for the owner's phone (worker side).</summary>
    Joining = 1,

    Linked = 2,
}

/// <summary>
/// The private channel between the owner's phone and ONE worker's phone (D5, TD-03): its own unguessable relay topic and
/// its own 256-bit key, so other workers' data never reaches this phone, not even encrypted. Each side pins the other
/// phone's signing key. The channel id is the worker's employee id.
/// </summary>
public sealed record Channel(
    string EmployeeId,
    string RelayUrl,
    string Topic,
    byte[] Key,
    byte[] OwnerKey,
    byte[]? WorkerKey,
    ChannelState State,
    string? InviteId,
    byte[]? InviteSecret,
    DateTimeOffset? InviteExpires,
    string? LastMessageId,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastStateRequestAt,
    long NextSeq,
    long PeerSeq,
    DateTimeOffset CreatedAt)
{
    public bool IsLinked => State == ChannelState.Linked;
}

/// <summary>Sync bookkeeping kept on the phone (implemented by Storage.Sqlite).</summary>
public interface ISyncStore
{
    Task<PhoneRole> GetRoleAsync(CancellationToken cancellationToken = default);

    Task SetRoleAsync(PhoneRole role, CancellationToken cancellationToken = default);

    /// <summary>This phone's signing key, created the first time it is needed.</summary>
    Task<SigningKey> GetSigningKeyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken cancellationToken = default);

    Task SaveChannelAsync(Channel channel, CancellationToken cancellationToken = default);

    Task DeleteChannelAsync(string employeeId, CancellationToken cancellationToken = default);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<(long Id, OutboxItem Item)>> GetOutboxAsync(string channel, CancellationToken cancellationToken = default);

    Task DeleteOutboxAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default);

    /// <summary>Adds outbox items outside a data change (e.g. "send everything again").</summary>
    Task AddOutboxAsync(IEnumerable<OutboxItem> items, CancellationToken cancellationToken = default);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>A worker phone that was unlinked: removes all factory data, channels, outbox, PINs and the role.</summary>
    Task WipeAsync(CancellationToken cancellationToken = default);
}

public static class SyncSettings
{
    public const string Role = "sync.role";
    public const string SigningKey = "sync.signingKey";
    public const string RelayUrl = "sync.relayUrl";

    /// <summary>Owner's ntfy topic for push notifications (0.5.0); empty = off.</summary>
    public const string NotifyTopic = "notify.topic";

    /// <summary>Worker phone, while linking: its private key-agreement half (deleted once linked).</summary>
    public const string LinkAgreement = "sync.linkAgreement";
}
