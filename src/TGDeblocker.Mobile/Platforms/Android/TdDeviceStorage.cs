using Microsoft.Maui.Storage;

namespace TGDeblocker.Mobile.Services;

internal static class TdDeviceStorage
{
    public static string GetNoBackupDirectory()
    {
        var directory = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new IOException("Android app-private no-backup storage is unavailable.");
        return Path.Combine(directory, "tgdeblocker-v1");
    }

    public static Task<string?> GetAsync(string name) => SecureStorage.Default.GetAsync(name);
    public static Task SetAsync(string name, string value) => SecureStorage.Default.SetAsync(name, value);
    public static void Remove(string name) => SecureStorage.Default.Remove(name);
}
