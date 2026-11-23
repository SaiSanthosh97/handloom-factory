using HandloomFactory.App.Resources.Strings;
using HandloomFactory.Core.AppUpdater;

namespace HandloomFactory.App.Services;

/// <summary>
/// App updates (D16). Each release is published on GitHub; the app checks every time it is opened or brought back
/// (at most once an hour, on any connection). A new version is announced with a pop-up (once per version) and a
/// banner on the dashboard until it is installed. The download is verified against GitHub's SHA-256 before Android's
/// installer opens. Adapted from Travel Vault's UpdateService.
/// </summary>
public sealed class UpdateService(HttpClient http, Dialogs dialogs)
{
    public const string Owner = "SaiSanthosh97";
    public const string Repository = "handloom-factory";
    public static readonly Uri RepositoryUrl = new($"https://github.com/{Owner}/{Repository}");

    private const string ApkMimeType = "application/vnd.android.package-archive";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private readonly GitHubReleaseChecker _checker = new(http, Owner, Repository, ".apk");
    private bool _checking;

    /// <summary>Raised when <see cref="Available"/> changes (the dashboard shows or hides its banner).</summary>
    public event Action? Changed;

    /// <summary>A newer version found by the last check, or null.</summary>
    public AvailableUpdate? Available { get; private set; }

    public static Version CurrentVersion => AppInfo.Current.Version;

    private static string DownloadFolder => Path.Combine(FileSystem.CacheDirectory, "updates");

    private static DateTimeOffset? LastCheck
    {
        get => Preferences.Default.Get("update.lastCheck", 0L) is var ms and > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        set => Preferences.Default.Set("update.lastCheck", value?.ToUnixTimeMilliseconds() ?? 0L);
    }

    /// <summary>The newest version already announced with a pop-up (so the pop-up appears once per version).</summary>
    private static string AnnouncedVersion
    {
        get => Preferences.Default.Get("update.announced", "");
        set => Preferences.Default.Set("update.announced", value);
    }

    /// <exception cref="HttpRequestException">No connection.</exception>
    public async Task<AvailableUpdate?> CheckNowAsync()
    {
        var update = await _checker.CheckAsync(CurrentVersion);
        LastCheck = DateTimeOffset.UtcNow;
        Available = update;
        Changed?.Invoke();
        return update;
    }

    /// <summary>On open/resume: check (at most hourly) and announce a new version once with a pop-up.</summary>
    public async Task CheckAndAnnounceAsync()
    {
        if (_checking || Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return;
        }

        _checking = true;
        try
        {
            var update = LastCheck is { } last && DateTimeOffset.UtcNow - last < CheckEvery ? Available : await CheckNowAsync();
            if (update is not null && AnnouncedVersion != update.TagName)
            {
                AnnouncedVersion = update.TagName;
                await OfferAsync(update);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline or GitHub unavailable: try again next time.
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Asks, then downloads and opens Android's installer.</summary>
    public async Task OfferAsync(AvailableUpdate update)
    {
        if (!await dialogs.ConfirmAsync(AppResources.Update_Title, Show.Format(AppResources.Update_Available, update.Version.ToString(3)), AppResources.Update_Install, AppResources.Update_Later))
        {
            return;
        }

        await InstallAsync(update);
    }

    public async Task InstallAsync(AvailableUpdate update)
    {
        // Android 8+: "Install unknown apps" is a per-app permission. (On Android 6–7 it is one phone-wide setting
        // and Android's installer asks for it itself.)
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && Platform.AppContext.PackageManager?.CanRequestPackageInstalls() != true)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_AllowInstall);
            var intent = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse($"package:{AppInfo.Current.PackageName}"));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(intent);
            return;
        }

        try
        {
            if (Directory.Exists(DownloadFolder))
            {
                Directory.Delete(DownloadFolder, recursive: true);
            }

            Directory.CreateDirectory(DownloadFolder);
            var path = Path.Combine(DownloadFolder, $"handloom-factory-{update.Version.ToString(3)}.apk");
            Dialogs.Toast(AppResources.Update_Title + "…");
            await new UpdateDownloader(http).DownloadAsync(update, path);
            await Launcher.Default.OpenAsync(new OpenFileRequest(AppResources.Update_Title, new ReadOnlyFile(path, ApkMimeType)));
        }
        catch (DamagedDownloadException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_Damaged);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await dialogs.AlertAsync(AppResources.Update_Title, AppResources.Update_Failed);
        }
    }
}

/// <summary>
/// The owner's phone asks for the PIN again after 5 minutes in the background (D3), because it holds everyone's wages.
/// </summary>
public sealed class SessionLock(AppHost host)
{
    public static readonly TimeSpan OwnerTimeout = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _leftAt;

    public void Left() => _leftAt = DateTimeOffset.UtcNow;

    /// <returns>True when the user was logged out and must enter the PIN again.</returns>
    public bool Returned()
    {
        var expired = _leftAt is { } left && host.Session.IsSignedIn && DateTimeOffset.UtcNow - left >= OwnerTimeout;
        _leftAt = null;
        if (expired)
        {
            host.Session.SignOut();
        }

        return expired;
    }
}
