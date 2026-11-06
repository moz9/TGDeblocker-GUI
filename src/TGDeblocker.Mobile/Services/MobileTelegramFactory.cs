using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using TGDeblocker.Core;

namespace TGDeblocker.Mobile.Services;

public sealed record TelegramApiCredentials(int ApiId, string ApiHash)
{
    public override string ToString() => nameof(TelegramApiCredentials);
}

public static class MobileTelegramFactory
{
    private const string ApiCredentialsKey = "telegram-api-v1";
    private const string SessionKey = "tdlib-database-key-v1";
    private static readonly SemaphoreSlim SessionStorageGate = new(1);
    private static readonly Lazy<TelegramApiCredentials?> BundledApi = new(ReadBundledApi);

    public static bool HasBundledApiCredentials => BundledApi.Value is not null;

    public static async Task<bool> HasSavedApiCredentialsAsync()
    {
        try { return await TdDeviceStorage.GetAsync(ApiCredentialsKey) is not null; }
        catch (Exception) { return true; } // Keep manual recovery available if secure storage cannot be read.
    }

    private static TelegramApiCredentials? ReadBundledApi()
    {
        using var stream = typeof(MobileTelegramFactory).Assembly.GetManifestResourceStream("TGDeblocker.Mobile.telegram-api.json");
        if (stream is null) return null; // Development builds can use their own API credentials.
        using var json = JsonDocument.Parse(stream);
        var apiId = json.RootElement.GetProperty("ApiId").GetInt32();
        var apiHash = json.RootElement.GetProperty("ApiHash").GetString();
        if (!IsValidApiCredentials(apiId, apiHash.AsSpan()))
            throw new InvalidDataException("The application's Telegram configuration is invalid.");
        return new TelegramApiCredentials(apiId, apiHash!);
    }

    public static ITelegramService CreateService(string language)
    {
        var device = DeviceInfo.Current;
        var locale = string.IsNullOrWhiteSpace(language) ? CultureInfo.CurrentUICulture.Name : language;
        var model = string.IsNullOrWhiteSpace(device.Model) ? device.Platform.ToString() : device.Model;
        return new TelegramService(TdMobileJsonClient.Create, OpenSessionStorageAsync,
            new TelegramClientInfo(model, string.IsNullOrWhiteSpace(locale) ? "en" : locale,
                AppInfo.Current.VersionString, device.VersionString));
    }

    public static async Task<TelegramApiCredentials?> GetApiCredentialsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var value = await TdDeviceStorage.GetAsync(ApiCredentialsKey).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (value is null)
            return BundledApi.Value;

        var separator = value.IndexOf(':');
        if (separator <= 0 || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var apiId) ||
            !IsValidApiCredentials(apiId, value.AsSpan(separator + 1)))
        {
            throw new InvalidDataException("Saved Telegram API credentials could not be read.");
        }

        return new TelegramApiCredentials(apiId, value[(separator + 1)..]);
    }

    public static async Task SaveApiCredentialsAsync(TelegramApiCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!IsValidApiCredentials(credentials.ApiId, credentials.ApiHash.AsSpan()))
            throw new ArgumentException("A positive API ID and a 32-character hexadecimal API hash are required.", nameof(credentials));

        ct.ThrowIfCancellationRequested();
        var value = credentials.ApiId.ToString(CultureInfo.InvariantCulture) + ":" + credentials.ApiHash;
        await TdDeviceStorage.SetAsync(ApiCredentialsKey, value).ConfigureAwait(false);
    }

    /// <summary>Call only after a real service has acknowledged logout and reported closed.</summary>
    public static async Task ForgetLocalSessionAfterLogoutAsync(CancellationToken ct = default)
    {
        await SessionStorageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var database = GetDatabaseDirectory();
            if (ContainsSessionFiles(database))
                throw new InvalidOperationException("TDLib still has local session files. The key was retained.");

            TdDeviceStorage.Remove(SessionKey);
        }
        finally
        {
            SessionStorageGate.Release();
        }
    }

    private static async ValueTask<TelegramSessionStorage> OpenSessionStorageAsync(CancellationToken ct)
    {
        await SessionStorageGate.WaitAsync(ct).ConfigureAwait(false);
        byte[]? key = null;
        try
        {
            var database = GetDatabaseDirectory();
            var encoded = await TdDeviceStorage.GetAsync(SessionKey).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (encoded is null)
            {
                if (ContainsSessionFiles(database))
                    throw new InvalidDataException("The protected key for the existing TDLib database is unavailable.");

                key = RandomNumberGenerator.GetBytes(32);
                await TdDeviceStorage.SetAsync(SessionKey, Convert.ToBase64String(key)).ConfigureAwait(false);
            }
            else
            {
                key = Convert.FromBase64String(encoded);
            }

            ct.ThrowIfCancellationRequested();
            var storage = new TelegramSessionStorage(database, key);
            key = null; // TDLib initialization now owns and clears the key array.
            return storage;
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);

            SessionStorageGate.Release();
        }
    }

    private static bool IsValidApiCredentials(int apiId, ReadOnlySpan<char> hash)
    {
        if (apiId <= 0 || hash.Length != 32)
            return false;

        foreach (var character in hash)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    private static string GetDatabaseDirectory() => Path.Combine(TdDeviceStorage.GetNoBackupDirectory(), "tdlib");

    private static bool ContainsSessionFiles(string database)
    {
        try { return Directory.EnumerateFiles(database, "*", SearchOption.AllDirectories).Any(); }
        catch (DirectoryNotFoundException) { return false; }
    }
}
