using System.Reflection;
using System.Text.RegularExpressions;

namespace TGDeblocker.App;

internal static class AppIdentity
{
    internal const string Repository = "moz9/TGDeblocker-GUI";
    internal static string Version => typeof(AppIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
    internal static string? QaId { get; } = ReadQaId();
    internal static string ProductId => "TGDeblocker.Windows" + (QaId is null ? "" : ".QA." + QaId);
    internal static string MutexName => @"Local\" + ProductId;
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        QaId is null ? "TGDeblocker" : "TGDeblocker-InstallerQA-" + QaId);

    private static string? ReadQaId()
    {
        var value = typeof(AppIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "InstallerQaId")?.Value;
        if (string.IsNullOrEmpty(value)) return null;
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9-]{0,23}$")) throw new InvalidOperationException("Invalid installer QA identity.");
        return value;
    }
}
