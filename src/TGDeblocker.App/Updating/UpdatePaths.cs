using System.Text.Json;

namespace TGDeblocker.App.Updating;

internal static class UpdatePaths
{
    internal static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new UpdateValidationException("An absolute local path is required.");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static void RejectReparsePoints(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UpdateValidationException("Reparse-point update path refused.");
            current = current.Parent;
        }
    }

    internal static string CreateStagingDirectory(string dataDirectory)
    {
        RejectReparsePoints(dataDirectory);
        var parent = Path.Combine(dataDirectory, "updates");
        RejectReparsePoints(parent);
        Directory.CreateDirectory(parent);
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        return directory;
    }

    internal static void CleanOldStaging(string dataDirectory)
    {
        var parent = Path.Combine(dataDirectory, "updates");
        if (!Directory.Exists(parent)) return;
        RejectReparsePoints(parent);
        foreach (var folder in Directory.EnumerateDirectories(parent))
        {
            var name = Path.GetFileName(folder);
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{32}$")) continue;
            RejectReparsePoints(folder);
            var info = new DirectoryInfo(folder);
            if (info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
            // Remove only our direct installer files, never arbitrary subdirectories or links.
            foreach (var file in info.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^TGDeblocker-Setup-\d+\.\d+\.\d+-x64\.exe$")) continue;
                try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(folder, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static string ReadInstallation(string baseDirectory, string product, string currentVersion)
    {
        var root = Normalize(baseDirectory);
        RejectReparsePoints(root);
        var marker = Path.Combine(root, "installed.json");
        var info = new FileInfo(marker);
        if (!info.Exists || info.Length > 16384 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UpdateValidationException("No valid installed application marker.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(marker));
        var value = json.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("appId").GetString() != product ||
            value.GetProperty("version").GetString() != currentVersion ||
            !string.Equals(Normalize(value.GetProperty("installDirectory").GetString()!), root, StringComparison.OrdinalIgnoreCase))
            throw new UpdateValidationException("Installation identity mismatch.");
        var uninstall = Normalize(value.GetProperty("uninstallExecutable").GetString()!);
        if (!string.Equals(Path.GetDirectoryName(uninstall), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(uninstall).StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            !uninstall.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(uninstall))
            throw new UpdateValidationException("Invalid uninstaller location.");
        return root;
    }
}
