using Android.App;
using Android.Content;
using Android.Content.PM;

namespace HandloomFactory.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTask, Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>The phone's screen-lock check for "Forgot PIN?" comes back here.</summary>
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        Services.DeviceLock.OnActivityResult(requestCode, resultCode);
        base.OnActivityResult(requestCode, resultCode, data);
    }
}
