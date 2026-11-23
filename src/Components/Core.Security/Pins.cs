using System.Security.Cryptography;
using System.Text;

namespace HandloomFactory.Core.Security;

/// <summary>A PIN stored as a salted PBKDF2-SHA256 hash. The iteration count is stored so it can be raised later.</summary>
public sealed record PinHash(byte[] Hash, byte[] Salt, int Iterations);

/// <summary>
/// 4-digit PINs (D2). Hashed with PBKDF2-HMAC-SHA256 and a random salt, compared in constant time.
/// Honest limit: 10,000 possible PINs. The hash only matters if someone gets the database, which is itself encrypted
/// with a key held in the Android Keystore; the PIN guards the screens against casual use of the phone.
/// </summary>
public static class Pins
{
    /// <summary>Chosen so a check takes well under a second on a 2016-era phone.</summary>
    public const int DefaultIterations = 100_000;

    public const int Length = 4;

    public static bool IsWellFormed(string? pin) => pin is { Length: Length } && pin.All(char.IsAsciiDigit);

    public static PinHash Hash(string pin, int iterations = DefaultIterations)
    {
        if (!IsWellFormed(pin))
        {
            throw new ArgumentException("A PIN is exactly four digits.", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        return new PinHash(Derive(pin, salt, iterations), salt, iterations);
    }

    public static bool Verify(string pin, PinHash stored) =>
        IsWellFormed(pin) && CryptographicOperations.FixedTimeEquals(Derive(pin, stored.Salt, stored.Iterations), stored.Hash);

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// Wrong-PIN lockout: after 5 wrong PINs in a row, wait 30 seconds; each further wrong PIN doubles the wait,
/// up to 15 minutes. Stored in the database so restarting the app doesn't reset it.
/// </summary>
public static class PinLockout
{
    public const int FreeAttempts = 5;

    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    /// <returns>How long to wait after this many consecutive failures (zero while attempts remain).</returns>
    public static TimeSpan WaitAfter(int failures)
    {
        if (failures < FreeAttempts)
        {
            return TimeSpan.Zero;
        }

        var doublings = Math.Min(failures - FreeAttempts, 10);
        var wait = TimeSpan.FromTicks(FirstWait.Ticks << doublings);
        return wait > MaxWait ? MaxWait : wait;
    }
}
