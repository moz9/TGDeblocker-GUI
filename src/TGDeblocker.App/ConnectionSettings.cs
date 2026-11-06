using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TGDeblocker.App;

internal sealed record ConnectionSettings(int ApiId, string ApiHash)
{
    private static string FileName => Path.Combine(AppIdentity.DataDirectory, "connection.dpapi");
    public static ConnectionSettings? Load()
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FileName), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<ConnectionSettings>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException) { return null; }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        var temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            File.Move(temporary, FileName, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public override string ToString() => nameof(ConnectionSettings);
}
