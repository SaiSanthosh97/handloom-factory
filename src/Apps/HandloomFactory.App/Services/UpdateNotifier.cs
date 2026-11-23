using Android.App;
using Android.Content;
using AndroidX.Core.App;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.Core.AppUpdater;
using AndroidApp = Android.App.Application;

namespace HandloomFactory.App.Services;

/// <summary>
/// D16 in the background (0.5.0): about once a day the background job checks GitHub, and a new version is announced
/// with an Android notification ("Handloom Factory update available"), once per version, even when the app is closed.
/// Tapping it opens the app, which shows the install pop-up and banner.
/// </summary>
public sealed class UpdateNotifier(HttpClient http)
{
    private const string ChannelId = "updates";
    private const int NotificationId = 7101;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    public async Task CheckInBackgroundAsync(CancellationToken cancellationToken = default)
    {
        var last = Preferences.Default.Get("update.bgCheck", 0L);
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last < CheckEvery.TotalMilliseconds)
        {
            return;
        }

        try
        {
            var checker = new GitHubReleaseChecker(http, UpdateService.Owner, UpdateService.Repository, ".apk");
            var update = await checker.CheckAsync(UpdateService.CurrentVersion, cancellationToken);
            Preferences.Default.Set("update.bgCheck", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (update is not null && Preferences.Default.Get("update.notified", "") != update.TagName)
            {
                Preferences.Default.Set("update.notified", update.TagName);
                Notify(update.Version.ToString(3));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline: tried again on a later run.
        }
    }

    /// <summary>Android 13+ asks the user once whether the app may show notifications.</summary>
    public static async Task AskPermissionAsync()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() == PermissionStatus.Unknown)
        {
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
    }

    private static void Notify(string version)
    {
        var context = AndroidApp.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, AppResources.Update_Title, NotificationImportance.Default));
        }

        var open = context.PackageManager!.GetLaunchIntentForPackage(context.PackageName!)!;
        open.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var notification = new NotificationCompat.Builder(context, ChannelId)
            .SetSmallIcon(Resource.Drawable.notification_icon)!
            .SetContentTitle(AppResources.Update_NotificationTitle)!
            .SetContentText(Show.Format(AppResources.Update_Available, version))!
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(Show.Format(AppResources.Update_Available, version)))!
            .SetContentIntent(pending)!
            .SetAutoCancel(true)!
            .Build()!;
        try
        {
            NotificationManagerCompat.From(context)!.Notify(NotificationId, notification);
        }
        catch (Java.Lang.SecurityException)
        {
            // Notifications not allowed: the app still shows the pop-up and banner when opened.
        }
    }
}
