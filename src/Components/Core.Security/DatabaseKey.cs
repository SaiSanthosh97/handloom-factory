using System.Security.Cryptography;

namespace HandloomFactory.Core.Security;

/// <summary>
/// A platform key store that can wrap (encrypt) and unwrap a small secret with a key that never leaves it.
/// On Android: the Keystore (Device.Security). In tests: <see cref="InMemoryKeyVault"/>.
/// </summary>
public interface IKeyVault
{
    byte[] Wrap(byte[] secret);

    /// <returns>The secret, or null when it can't be unwrapped (key lost or data damaged).</returns>
    byte[]? Unwrap(byte[] wrapped);

    /// <summary>Deletes the wrapping key: everything it protected becomes unreadable.</summary>
    void Destroy();
}

/// <summary>
/// The random 256-bit key that encrypts the database. It is kept on disk only wrapped by the <see cref="IKeyVault"/>,
/// and is deliberately <b>not</b> derived from the PIN, so background sync can open the database while the app is
/// locked (TD-00 §2.3).
/// </summary>
public static class DatabaseKey
{
    /// <returns>The key, creating and storing a new one the first time.</returns>
    /// <exception cref="InvalidDataException">A key file exists but can't be unwrapped.</exception>
    public static byte[] LoadOrCreate(string path, IKeyVault vault)
    {
        if (File.Exists(path))
        {
            return vault.Unwrap(File.ReadAllBytes(path)) is { Length: 32 } key
                ? key
                : throw new InvalidDataException("The database key could not be unwrapped.");
        }

        var created = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, vault.Wrap(created));
        return created;
    }
}

/// <summary>An <see cref="IKeyVault"/> for tests and PC tools: AES-GCM with a key held in memory.</summary>
public sealed class InMemoryKeyVault : IKeyVault
{
    private byte[]? _key = RandomNumberGenerator.GetBytes(32);

    public byte[] Wrap(byte[] secret)
    {
        var key = _key ?? throw new InvalidOperationException("The key was destroyed.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[secret.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[]? Unwrap(byte[] wrapped)
    {
        if (_key is null || wrapped.Length < 28)
        {
            return null;
        }

        var plain = new byte[wrapped.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(wrapped[..12], wrapped[28..], wrapped[12..28], plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    public void Destroy() => _key = null;
}
