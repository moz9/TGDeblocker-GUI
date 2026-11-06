using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace TGDeblocker.App.Updating;

internal static class WindowsUpdate
{
    internal static UpdatePolicy CreatePolicy()
    {
        using var stream = typeof(WindowsUpdate).Assembly.GetManifestResourceStream("WindowsUpdatePublicKey")
            ?? throw new UpdateValidationException("Update trust key is missing.");
        using var reader = new StreamReader(stream);
        return new UpdatePolicy(reader.ReadToEnd().Trim(), AppIdentity.Repository, AppIdentity.ProductId);
    }

    internal static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static string RequireInstalledApplication()
    {
        var root = UpdatePaths.ReadInstallation(AppContext.BaseDirectory, AppIdentity.ProductId, AppIdentity.Version);
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppIdentity.ProductId + "_is1");
        var registered = key?.GetValue("InstallLocation") as string;
        if (registered is null || !string.Equals(UpdatePaths.Normalize(registered), root, StringComparison.OrdinalIgnoreCase))
            throw new UpdateValidationException("Application is not registered for this Windows user.");
        return root;
    }

    internal static async Task StartInstallerAsync(string path, UpdateManifest manifest, string installDirectory, string restartMode, CancellationToken cancellationToken)
    {
        if (restartMode is not ("normal" or "session" or "demo" or "visual")) throw new ArgumentException("Invalid restart mode.");
        if (!UpdatePolicy.IsNewer(manifest, AppIdentity.Version)) throw new UpdateValidationException("Downgrade refused.");
        UpdatePaths.RejectReparsePoints(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UpdateValidationException("Installer must be a regular file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != manifest.Size || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancellationToken), Convert.FromHexString(manifest.Sha256)))
            throw new UpdateValidationException("Installer changed after download.");
        var eventName = @"Local\TGDeblocker.UpdateReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        foreach (var argument in new[] { "/UPDATE", "/SILENT", "/NORESTART", "/DIR=" + installDirectory, "/PARENTPID=" + Environment.ProcessId, "/READYEVENT=" + eventName, "/RESTARTMODE=" + restartMode })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Installer could not start.");
        var signaled = await Task.Run(() => WaitHandle.WaitAny([ready, cancellationToken.WaitHandle], TimeSpan.FromSeconds(15)), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        if (signaled != 0) throw new InvalidOperationException("Installer did not acknowledge the update. The application was left open.");
        // Setup has opened and verified this process handle. Its file replacement waits for our exit.
    }
}
