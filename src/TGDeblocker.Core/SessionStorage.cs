using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace TGDeblocker.Core;

public sealed class TelegramSessionStorage
{
    public string DatabaseDirectory { get; }
    internal byte[] Key { get; }

    /// <summary>
    /// Use an absolute app-private database path and a fresh copy of the key from protected platform storage.
    /// The service takes ownership of the array and clears it after TDLib initialization.
    /// </summary>
    public TelegramSessionStorage(string databaseDirectory, byte[] encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        if (!Path.IsPathFullyQualified(databaseDirectory))
            throw new ArgumentException("An absolute database path is required.", nameof(databaseDirectory));

        ArgumentNullException.ThrowIfNull(encryptionKey);
        if (encryptionKey.Length != 32)
            throw new ArgumentException("A 32-byte encryption key is required.", nameof(encryptionKey));

        DatabaseDirectory = databaseDirectory;
        Key = encryptionKey;
    }
}

internal static class SessionStorage
{
    public static TelegramSessionStorage OpenDefault()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("TDLib в этом приложении поддерживает Windows x64.");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TGDeblocker", "session-v1");
        return Open(root);
    }

    [SupportedOSPlatform("windows")]
    internal static TelegramSessionStorage Open(string root)
    {
        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "database-key.dpapi");
        var databaseDirectory = Path.Combine(root, "database");

        if (!File.Exists(keyPath))
            CreateProtectedKey(keyPath);

        var key = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Неверный размер ключа базы TDLib.");
        }

        return new TelegramSessionStorage(databaseDirectory, key);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateProtectedKey(string path)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(protectedKey);
                file.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(staging, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent launch already created the key. All instances use that key.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(staging);
        }
    }
}
