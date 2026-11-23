using System.Globalization;
using HandloomFactory.App.Resources.Strings;

namespace HandloomFactory.App.Services;

/// <summary>
/// Export files on the phone (D18): share an export (WhatsApp, Drive, email, Files…), pick a file to import, and keep
/// automatic safety copies taken just before an import replaces everything.
/// </summary>
public static class DataFiles
{
    private const int SafetyCopiesKept = 5;

    private static string SafetyFolder => Path.Combine(FileSystem.AppDataDirectory, "safety-copies");

    /// <summary>Writes the export to a file and opens Android's share sheet.</summary>
    public static async Task ShareAsync(string fileText, DateOnly today)
    {
        var folder = Path.Combine(FileSystem.CacheDirectory, "exports");
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true); // only the latest export stays in the cache
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"handloom-data-{today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json");
        await File.WriteAllTextAsync(path, fileText);
        await Share.Default.RequestAsync(new ShareFileRequest(AppResources.Settings_ExportTitle, new ShareFile(path, "application/json")));
    }

    /// <returns>The chosen file's text, or null when cancelled.</returns>
    public static async Task<string?> PickAsync()
    {
        var types = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            // WhatsApp and Drive sometimes label .json as plain text or "unknown": accept those too; the content is checked.
            [DevicePlatform.Android] = ["application/json", "text/plain", "application/octet-stream"],
        });
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = AppResources.Settings_Import, FileTypes = types });
        if (file is null)
        {
            return null;
        }

        await using var stream = await file.OpenReadAsync();
        if (stream.CanSeek && stream.Length > 50 * 1024 * 1024)
        {
            return ""; // far bigger than any real factory export: refused as invalid
        }

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Keeps a copy of the current data before an import (the newest few are kept).</summary>
    public static async Task SaveSafetyCopyAsync(string fileText)
    {
        Directory.CreateDirectory(SafetyFolder);
        var name = $"before-import-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
        await File.WriteAllTextAsync(Path.Combine(SafetyFolder, name), fileText);
        foreach (var old in Directory.GetFiles(SafetyFolder, "before-import-*.json").OrderDescending().Skip(SafetyCopiesKept))
        {
            File.Delete(old);
        }
    }
}
