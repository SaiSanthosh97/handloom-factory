using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HandloomFactory.Core.Security;
using HandloomFactory.Core.Workflow;

namespace HandloomFactory.Core.Sync;

/// <summary>Message kinds on a channel.</summary>
public static class Kinds
{
    /// <summary>Worker → owner: "I scanned invite X; my signing key; proof of the invite secret".</summary>
    public const string Link = "link";

    /// <summary>Owner → worker: "linked; here is my half of the new channel key". Sent with the invite key; everything after uses the new key.</summary>
    public const string Linked = "linked";

    /// <summary>Records (both ways). A full snapshot from the owner sets <see cref="ChangesBody.Full"/>.</summary>
    public const string Changes = "changes";

    /// <summary>"I was away longer than the relay keeps messages: please send everything again."</summary>
    public const string StateRequest = "state-request";

    /// <summary>Owner → worker: this phone is no longer linked (worker disabled or re-linked).</summary>
    public const string Revoked = "revoked";

    /// <summary>Worker → owner: "last seen" (every 6 h).</summary>
    public const string CheckIn = "checkin";
}

public static class Senders
{
    public const string Owner = "owner";
    public const string Worker = "worker";
}

/// <summary>
/// One message on a channel, before encryption. <see cref="Seq"/> increases per sender, so a replayed old message is
/// recognised and dropped. <see cref="Sent"/> is the sender's clock (Unix ms), for information only.
/// </summary>
public sealed record Envelope(int V, string Ch, string From, long Seq, long Sent, string Kind, JsonElement? Body = null)
{
    public const int CurrentVersion = 1;

    public T? BodyAs<T>() => Body is { } b ? b.Deserialize<T>(FactoryJson.Compact) : default;

    public static Envelope Create<T>(string channel, string from, long seq, DateTimeOffset sent, string kind, T body) =>
        new(CurrentVersion, channel, from, seq, sent.ToUnixTimeMilliseconds(), kind, JsonSerializer.SerializeToElement(body, FactoryJson.Compact));
}

/// <summary>One record travelling between phones: its type and its JSON (same JSON as export files).</summary>
public sealed record SyncRecord(string Type, string Data);

public static class RecordTypes
{
    public const string Factory = "factory";
    public const string Employee = "employee";
    public const string Rate = "rate";
    public const string Entry = "entry";
    public const string EntryDeleted = "entry-deleted";
    public const string Payment = "payment";
    public const string PaymentDeleted = "payment-deleted";
    public const string Settings = "settings";

    /// <summary>Outbox only: send a full snapshot (after an import).</summary>
    public const string Snapshot = "snapshot";

    /// <summary>Outbox only: tells the flush to send <see cref="Kinds.Revoked"/> and close the channel.</summary>
    public const string Revoke = "revoke";
}

/// <param name="Full">A complete snapshot from the owner: anything of this worker dated on/after <paramref name="Since"/> that is not in it was deleted.</param>
/// <param name="Part">1-based part number of a snapshot split over several messages; <paramref name="Parts"/> in total.</param>
public sealed record ChangesBody(List<SyncRecord> Records, bool Full = false, DateOnly? Since = null, string? SnapshotId = null, int Part = 1, int Parts = 1);

public sealed record LinkBody(string InviteId, string WorkerKey, string Proof, string AppVersion, string WorkerAgreement);

/// <param name="WorkerKey">The signing key of the phone the owner accepted: any other phone that scanned the same QR ignores this.</param>
public sealed record LinkedBody(string WorkerKey, string OwnerAgreement);

public sealed record StateRequestBody(string RequestId);

public sealed record CheckInBody(string AppVersion);

/// <summary>Settings the owner shares with worker phones (0.5.0: where to send "submitted" notifications).</summary>
public sealed record SharedSettings(string? NotifyTopic);

public sealed class MessageTooLargeException() : Exception("The message doesn't fit in one relay message.");

/// <summary>
/// Turns envelopes into relay text and back (adapted from Travel Vault's WireCodec):
/// JSON → gzip → pad to a size bucket → AES-256-GCM with the channel's message key (label binds the channel) →
/// ECDSA signature over the ciphertext by the sending phone.
/// Text form: <c>hf1.&lt;ciphertext&gt;.&lt;signature&gt;</c> (base64url), always under ntfy's ~4 KB limit.
/// </summary>
public static class WireCodec
{
    private const string Prefix = "hf1.";

    /// <summary>Padding buckets (bytes): the relay can't tell a check-in from a day's entry by size.</summary>
    private static readonly int[] Buckets = [384, 768, 1536, 2600];

    public static string Encode(Envelope envelope, byte[] channelKey, SigningKey signer)
    {
        var compressed = Gzip(JsonSerializer.SerializeToUtf8Bytes(envelope, FactoryJson.Compact));
        var bucket = Buckets.FirstOrDefault(b => b >= compressed.Length + 4);
        if (bucket == 0)
        {
            throw new MessageTooLargeException();
        }

        var padded = new byte[bucket];
        BinaryPrimitives.WriteInt32BigEndian(padded, compressed.Length);
        compressed.CopyTo(padded, 4);
        var box = AeadBox.Seal(MessageKey(channelKey), padded, Label(envelope.Ch));
        return $"{Prefix}{Base64Url.Encode(box)}.{Base64Url.Encode(signer.Sign(box))}";
    }

    /// <summary>Decrypts and checks the signature against <paramref name="senderKey"/> (null: skip the check, used only for link messages whose key is inside).</summary>
    /// <returns>The envelope, or null if it isn't for this channel, was changed, or wasn't signed by that phone.</returns>
    public static (Envelope Envelope, byte[] Box, byte[] Signature)? Decode(string text, string channel, byte[] channelKey, byte[]? senderKey)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = text[Prefix.Length..].Split('.');
        if (parts.Length != 2 || Base64Url.TryDecode(parts[0]) is not { } box || Base64Url.TryDecode(parts[1]) is not { } signature)
        {
            return null;
        }

        if (senderKey is not null && !Signatures.Verify(senderKey, box, signature))
        {
            return null;
        }

        if (AeadBox.Open(MessageKey(channelKey), box, Label(channel)) is not { Length: >= 4 } padded)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(padded);
        if (length <= 0 || length > padded.Length - 4)
        {
            return null;
        }

        try
        {
            var json = Gunzip(padded.AsSpan(4, length).ToArray());
            var envelope = JsonSerializer.Deserialize<Envelope>(json, FactoryJson.Compact);
            return envelope is not null && envelope.Ch == channel ? (envelope, box, signature) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Packs records into as few messages as fit, splitting the list in halves when one message would be too large.
    /// </summary>
    public static List<List<SyncRecord>> Pack(List<SyncRecord> records, Func<List<SyncRecord>, bool> fits)
    {
        if (records.Count == 0 || fits(records))
        {
            return [records];
        }

        if (records.Count == 1)
        {
            throw new MessageTooLargeException();
        }

        var half = records.Count / 2;
        return [.. Pack(records[..half], fits), .. Pack(records[half..], fits)];
    }

    private static byte[] MessageKey(byte[] channelKey) => KeyDerivation.Derive(channelKey, "msg");

    private static string Label(string channel) => "hf/channel/" + channel;

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Capped at 1 MB against "zip bombs".</summary>
    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > 1024 * 1024)
            {
                throw new InvalidDataException("Too large.");
            }
        }

        return output.ToArray();
    }
}

/// <summary>
/// The link invite shown as a QR code (or sent as text) by the owner (TD-03 §Linking). It carries the channel key, so
/// it is one-time, expires after 15 minutes, and the owner's phone confirms the link.
/// Text form: <c>HF1:&lt;base64url JSON&gt;</c>.
/// </summary>
public sealed record Invite(
    int V,
    string Factory,
    string EmployeeId,
    string WorkerName,
    string Relay,
    string Topic,
    string Key,
    string OwnerKey,
    string InviteId,
    string Secret,
    long Expires)
{
    private const string Prefix = "HF1:";

    public string ToText() => Prefix + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(this, FactoryJson.Compact));

    /// <returns>The invite, or null if the text isn't one (it may contain other text around it, e.g. a WhatsApp message).</returns>
    public static Invite? TryParse(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var start = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var token = new string(text[(start + Prefix.Length)..].TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (Base64Url.TryDecode(token) is not { } json)
        {
            return null;
        }

        try
        {
            var invite = JsonSerializer.Deserialize<Invite>(json, FactoryJson.Compact);
            return invite is { V: 1 } && Base64Url.TryDecode(invite.Key) is { Length: 32 } && Base64Url.TryDecode(invite.Secret) is { Length: 32 } ? invite : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeMilliseconds(Expires);

    public static string ProofData(string inviteId, string workerKey) => $"{inviteId}|{workerKey}";

    public static byte[] ProofBytes(string inviteId, string workerKey) => Encoding.UTF8.GetBytes(ProofData(inviteId, workerKey));
}
