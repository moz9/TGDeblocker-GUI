using Foundation;
using Security;

namespace TGDeblocker.Mobile.Services;

internal static class TdDeviceStorage
{
    private static string ServiceName => NSBundle.MainBundle.BundleIdentifier + ".tgdeblocker.device-only";

    public static string GetNoBackupDirectory()
    {
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory,
            NSSearchPathDomain.User).FirstOrDefault()?.Path ?? throw new IOException("iOS app-private storage is unavailable.");
        var directory = Path.Combine(support, "tgdeblocker-v1");
        Directory.CreateDirectory(directory);
        using var url = NSUrl.FromFilename(directory);
        using var excluded = new NSNumber(true);
        if (!url.SetResource(NSUrl.IsExcludedFromBackupKey, excluded))
            throw new IOException("iOS could not exclude the TDLib database from backups.");

        return directory;
    }

    public static Task<string?> GetAsync(string name)
    {
        using var query = CreateQuery(name);
        using var result = SecKeyChain.QueryAsRecord(query, out var status);
        if (status == SecStatusCode.ItemNotFound)
            return Task.FromResult<string?>(null);

        EnsureSuccess(status);
        using var value = result?.ValueData ?? throw new IOException("The iOS Keychain item has no data.");
        using var text = NSString.FromData(value, NSStringEncoding.UTF8);
        return Task.FromResult<string?>(text?.ToString() ?? throw new IOException("The iOS Keychain data is invalid."));
    }

    public static Task SetAsync(string name, string value)
    {
        using var query = CreateQuery(name);
        using var data = NSData.FromString(value, NSStringEncoding.UTF8);
        using var update = new SecRecord
        {
            ValueData = data,
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
        };
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            query.ValueData = data;
            query.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
            status = SecKeyChain.Add(query);
        }

        EnsureSuccess(status);
        return Task.CompletedTask;
    }

    public static void Remove(string name)
    {
        using var query = CreateQuery(name);
        var status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.ItemNotFound)
            EnsureSuccess(status);
    }

    private static SecRecord CreateQuery(string name) => new(SecKind.GenericPassword)
    {
        Account = name,
        Service = ServiceName,
        Synchronizable = false,
    };

    private static void EnsureSuccess(SecStatusCode status)
    {
        if (status != SecStatusCode.Success)
            throw new IOException("The iOS Keychain could not complete the operation.");
    }
}
