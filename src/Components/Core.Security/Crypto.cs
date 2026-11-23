using System.Security.Cryptography;
using System.Text;

namespace HandloomFactory.Core.Security;

/// <summary>
/// Authenticated encryption with a 32-byte key (AES-256-GCM), adapted from Travel Vault's AeadBox.
/// Layout: version(1) | nonce(12) | ciphertext | tag(16). A <c>label</c> is bound to every box, so a box made for
/// one channel can't be passed off as belonging to another: opening it with a different label fails.
/// </summary>
public static class AeadBox
{
    public const int KeySize = 32;
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    public const int Overhead = 1 + NonceSize + TagSize;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string label)
    {
        CheckKey(key);
        var output = new byte[Overhead + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(1 + NonceSize, plaintext.Length), output.AsSpan(1 + NonceSize + plaintext.Length, TagSize), Encoding.UTF8.GetBytes(label));
        return output;
    }

    /// <returns>The plaintext, or null when the box wasn't made with this key and label, or was changed.</returns>
    public static byte[]? Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> box, string label)
    {
        CheckKey(key);
        if (box.Length < Overhead || box[0] != Version)
        {
            return null;
        }

        var plaintext = new byte[box.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(box.Slice(1, NonceSize), box.Slice(1 + NonceSize, plaintext.Length), box.Slice(1 + NonceSize + plaintext.Length, TagSize), plaintext, Encoding.UTF8.GetBytes(label));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"The key must be {KeySize} bytes.", nameof(key));
        }
    }
}

/// <summary>Purpose-specific keys from one key (HKDF-SHA256), so one secret never serves two purposes.</summary>
public static class KeyDerivation
{
    public static byte[] Derive(ReadOnlySpan<byte> key, string purpose, int length = AeadBox.KeySize) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), length, salt: [], info: Encoding.UTF8.GetBytes("handloomfactory/" + purpose));
}

/// <summary>
/// A phone's signing key (ECDSA P-256). Each phone creates one; the public half is pinned by the other side of a
/// channel, so every message proves which phone wrote it.
/// </summary>
public sealed class SigningKey : IDisposable
{
    private readonly ECDsa _key;

    private SigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKey { get; }

    public static SigningKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static SigningKey Import(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new SigningKey(key);
    }

    public byte[] ExportPrivate() => _key.ExportPkcs8PrivateKey();

    /// <summary>64-byte signature (IEEE P1363 r||s).</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

public static class Signatures
{
    public const int Size = 64;

    /// <returns>False for a wrong signature or a malformed key (never throws on input from another phone).</returns>
    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != Size)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Key agreement (ECDH P-256) used once, when a worker's phone links: both phones derive a NEW channel key that
/// someone who only photographed the invite QR can't compute (TD-03).
/// </summary>
public static class KeyAgreement
{
    /// <returns>A fresh key pair: private (PKCS#8, kept on the phone) and public (SubjectPublicKeyInfo, sent).</returns>
    public static (byte[] Private, byte[] Public) Create()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKey(), key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The shared 32-byte key, bound to both public keys and to <paramref name="salt"/> (the invite's channel key).</summary>
    /// <returns>Null if the other public key is malformed.</returns>
    public static byte[]? Derive(byte[] ownPrivate, byte[] otherPublic, byte[] salt)
    {
        try
        {
            using var own = ECDiffieHellman.Create();
            own.ImportPkcs8PrivateKey(ownPrivate, out _);
            using var other = ECDiffieHellman.Create();
            other.ImportSubjectPublicKeyInfo(otherPublic, out _);
            var secret = own.DeriveRawSecretAgreement(other.PublicKey);
            try
            {
                var ownPublic = own.ExportSubjectPublicKeyInfo();
                // Order the two public keys so both sides compute the same info.
                var (a, b) = string.CompareOrdinal(Convert.ToBase64String(ownPublic), Convert.ToBase64String(otherPublic)) < 0 ? (ownPublic, otherPublic) : (otherPublic, ownPublic);
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, AeadBox.KeySize, salt, [.. Encoding.UTF8.GetBytes("handloomfactory/channel-key/"), .. SHA256.HashData([.. a, .. b])]);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>"I know the invite secret" proofs: HMAC-SHA256(secret, data), compared in constant time.</summary>
public static class Proofs
{
    public static byte[] Make(byte[] secret, ReadOnlySpan<byte> data) => HMACSHA256.HashData(secret, data);

    public static bool Check(byte[] secret, ReadOnlySpan<byte> data, ReadOnlySpan<byte> proof) =>
        CryptographicOperations.FixedTimeEquals(Make(secret, data), proof);

    /// <summary>Stored instead of the secret itself.</summary>
    public static byte[] Hash(byte[] secret) => SHA256.HashData(secret);
}

/// <summary>Unguessable names (topics, ids): random base62 text.</summary>
public static class RandomText
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>32 characters ≈ 190 bits: a topic nobody can guess.</summary>
    public static string New(int length = 32) => RandomNumberGenerator.GetString(Alphabet, length);
}

/// <summary>URL-safe base64 without padding (fits in QR codes, links and relay text).</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? TryDecode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
