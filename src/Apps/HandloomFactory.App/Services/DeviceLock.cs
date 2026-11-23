using Android.App;
using Android.Content;
using AndroidApp = Android.App.Application;

namespace HandloomFactory.App.Services;

/// <summary>
/// The phone's own screen lock (fingerprint, pattern or phone PIN). "Forgot PIN?" on the owner's phone asks for it
/// before a new app PIN can be chosen (requirements: Owner forgot PIN, 0.6.0).
/// </summary>
public static class DeviceLock
{
    private const int RequestCode = 7201;
    private static TaskCompletionSource<bool>? _pending;

    private static KeyguardManager Keyguard => (KeyguardManager)AndroidApp.Context.GetSystemService(Context.KeyguardService)!;

    /// <summary>False when the phone has no screen lock at all.</summary>
    public static bool IsSet => Keyguard.IsDeviceSecure;

    /// <returns>True when the person unlocked with the phone's screen lock.</returns>
    public static Task<bool> ConfirmAsync(string title, string description)
    {
#pragma warning disable CA1422, CS0618 // still the only way that works on Android 6 to 15 without a biometric library
        var intent = Keyguard.CreateConfirmDeviceCredentialIntent(title, description);
#pragma warning restore CA1422, CS0618
        if (intent is null || Platform.CurrentActivity is not { } activity)
        {
            return Task.FromResult(false);
        }

        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();
        activity.StartActivityForResult(intent, RequestCode);
        return _pending.Task;
    }

    /// <summary>Called from <c>MainActivity.OnActivityResult</c>.</summary>
    public static void OnActivityResult(int requestCode, Result resultCode)
    {
        if (requestCode == RequestCode)
        {
            _pending?.TrySetResult(resultCode == Result.Ok);
            _pending = null;
        }
    }
}
