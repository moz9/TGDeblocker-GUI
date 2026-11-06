using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TGDeblocker.App.Updating;

namespace TGDeblocker.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string directory = Directory.CreateTempSubdirectory("TGDeblockerUpdateTests-").FullName;
    private readonly byte[] installer = Encoding.UTF8.GetBytes("A deterministic test installer, never executable.");
    private UpdatePolicy Policy => new(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "moz9/TGDeblocker-GUI", "TGDeblocker.Windows");
    private UpdateManifest Manifest => new(1, "TGDeblocker.Windows", "x64", "stable", "1.1.0", "TGDeblocker-Setup-1.1.0-x64.exe",
        "https://github.com/moz9/TGDeblocker-GUI/releases/download/v1.1.0/TGDeblocker-Setup-1.1.0-x64.exe", installer.Length, Convert.ToHexString(SHA256.HashData(installer)));
    private byte[] Signed(UpdateManifest manifest)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateJsonContext.Default.UpdateManifest);
        return JsonSerializer.SerializeToUtf8Bytes(new UpdateEnvelope(Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))), UpdateJsonContext.Default.UpdateEnvelope);
    }

    [Fact]
    public void AcceptsAuthenticManifestAndComparesNumericVersions()
    {
        var result = Policy.Verify(Signed(Manifest));
        Assert.Equal(Manifest, result);
        Assert.True(UpdatePolicy.IsNewer(result, "1.0.9"));
        Assert.False(UpdatePolicy.IsNewer(result, "1.1.0"));
        Assert.False(UpdatePolicy.IsNewer(result, "1.2.0"));
        Assert.True(UpdatePolicy.ParseVersion("1.10.0") > UpdatePolicy.ParseVersion("1.9.0"));
    }

    [Fact]
    public void RejectsTamperingUnsignedAndDifferentSigningKey()
    {
        var envelope = JsonSerializer.Deserialize(Signed(Manifest), UpdateJsonContext.Default.UpdateEnvelope)!;
        var tampered = envelope with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")) };
        Assert.Throws<UpdateValidationException>(() => Policy.Verify(JsonSerializer.SerializeToUtf8Bytes(tampered, UpdateJsonContext.Default.UpdateEnvelope)));
        Assert.Throws<UpdateValidationException>(() => Policy.Verify(Encoding.UTF8.GetBytes("{\"version\":\"9.0.0\"}")));
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPolicy = new UpdatePolicy(Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo()), "moz9/TGDeblocker-GUI", "TGDeblocker.Windows");
        Assert.Throws<UpdateValidationException>(() => otherPolicy.Verify(Signed(Manifest)));
    }

    [Theory]
    [InlineData("product")]
    [InlineData("architecture")]
    [InlineData("channel")]
    [InlineData("schema")]
    [InlineData("size")]
    [InlineData("name")]
    [InlineData("url")]
    [InlineData("hash")]
    public void RejectsSignedButIncompatibleFields(string field)
    {
        var m = field switch
        {
            "product" => Manifest with { Product = "AnotherApp" },
            "architecture" => Manifest with { Architecture = "arm64" },
            "channel" => Manifest with { Channel = "beta" },
            "schema" => Manifest with { Schema = 2 },
            "size" => Manifest with { Size = UpdatePolicy.MaximumInstallerBytes + 1 },
            "name" => Manifest with { FileName = "../setup.exe" },
            "url" => Manifest with { Url = "https://github.com/other/repo/releases/download/v1.1.0/setup.exe" },
            _ => Manifest with { Sha256 = new string('z', 64) }
        };
        Assert.Throws<UpdateValidationException>(() => Policy.Verify(Signed(m)));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.-1")]
    [InlineData("10000.0.0")]
    public void RejectsNonReleaseVersions(string version) => Assert.Throws<UpdateValidationException>(() => UpdatePolicy.ParseVersion(version));

    [Theory]
    [InlineData("http://github.com/moz9/TGDeblocker-GUI/releases/a")]
    [InlineData("https://github.com.attacker.example/moz9/TGDeblocker-GUI/releases/a")]
    [InlineData("https://github.com/other/repo/releases/a")]
    [InlineData("https://github.com:8443/moz9/TGDeblocker-GUI/releases/a")]
    [InlineData("https://user@release-assets.githubusercontent.com/a")]
    [InlineData("https://release-assets.githubusercontent.com/a#fragment")]
    public void RejectsUnsafeDownloadLocations(string url) => Assert.False(Policy.IsAllowedDownloadUri(new Uri(url)));

    [Fact]
    public async Task FollowsBoundedTrustedRedirectButNeverContactsUntrustedDestination()
    {
        var handler = new Handler((request, count) => count == 1 ? Redirect("https://release-assets.githubusercontent.com/test") : Bytes(Signed(Manifest)));
        using var http = new HttpClient(handler);
        Assert.NotNull(await new UpdateClient(http, Policy).CheckAsync("1.0.0", CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
        var unsafeHandler = new Handler((_, _) => Redirect("https://attacker.example/setup"));
        using var unsafeHttp = new HttpClient(unsafeHandler);
        await Assert.ThrowsAsync<UpdateValidationException>(() => new UpdateClient(unsafeHttp, Policy).CheckAsync("1.0.0", CancellationToken.None));
        Assert.Equal(1, unsafeHandler.RequestCount);
    }

    [Fact]
    public async Task CurrentAndOlderManifestDoNotOfferAnUpdate()
    {
        using var http = new HttpClient(new Handler((_, _) => Bytes(Signed(Manifest))));
        var client = new UpdateClient(http, Policy);
        Assert.Null(await client.CheckAsync("1.1.0", CancellationToken.None));
        Assert.Null(await client.CheckAsync("1.2.0", CancellationToken.None));
        await Assert.ThrowsAsync<UpdateValidationException>(() => client.DownloadAsync(Manifest, "1.1.0", Path.Combine(directory, "blocked.exe"), null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task VerifiedDownloadMatchesSignedBytesAndNeverOverwritesAnExistingFile()
    {
        using var http = new HttpClient(new Handler((_, _) => Bytes(installer)));
        var client = new UpdateClient(http, Policy);
        var file = Path.Combine(directory, "setup.exe");
        await client.DownloadAsync(Manifest, "1.0.0", file, null, CancellationToken.None);
        Assert.Equal(installer, File.ReadAllBytes(file));
        await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(Manifest, "1.0.0", file, null, CancellationToken.None));
        Assert.Equal(installer, File.ReadAllBytes(file));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("corrupt")]
    public async Task RemovesUnverifiedPartialDownload(string failure)
    {
        var bytes = failure == "truncated" ? installer[..^3] : failure == "oversized" ? installer.Concat(new byte[] { 1, 2, 3 }).ToArray() : new byte[installer.Length];
        using var http = new HttpClient(new Handler((_, _) => { var response = Bytes(bytes); response.Content.Headers.ContentLength = installer.Length; return response; }));
        var file = Path.Combine(directory, "bad.exe");
        await Assert.ThrowsAsync<UpdateValidationException>(() => new UpdateClient(http, Policy).DownloadAsync(Manifest, "1.0.0", file, null, CancellationToken.None));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task CancellationAndUnavailableChannelCannotProduceInstaller()
    {
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateClient(http, Policy).CheckAsync("1.0.0", CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(http, Policy).DownloadAsync(Manifest, "1.0.0", Path.Combine(directory, "cancelled.exe"), null, cancelled.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void InstallationMarkerCannotRedirectUpdaterToAnotherDirectory()
    {
        File.WriteAllText(Path.Combine(directory, "unins000.exe"), "test only");
        var escaped = Quote(directory);
        var uninstaller = Quote(Path.Combine(directory, "unins000.exe"));
        var json = "{\"schema\":1,\"appId\":\"TGDeblocker.Windows\",\"version\":\"1.1.0\",\"installDirectory\":" + escaped + ",\"uninstallExecutable\":" + uninstaller + "}";
        File.WriteAllText(Path.Combine(directory, "installed.json"), json);
        Assert.Equal(UpdatePaths.Normalize(directory), UpdatePaths.ReadInstallation(directory, "TGDeblocker.Windows", "1.1.0"));
        Assert.Throws<UpdateValidationException>(() => UpdatePaths.ReadInstallation(directory, "AnotherApp", "1.1.0"));
        Assert.Throws<UpdateValidationException>(() => UpdatePaths.ReadInstallation(directory, "TGDeblocker.Windows", "1.0.0"));
        File.WriteAllText(Path.Combine(directory, "installed.json"), json.Replace(escaped, Quote(Path.GetTempPath())));
        Assert.Throws<UpdateValidationException>(() => UpdatePaths.ReadInstallation(directory, "TGDeblocker.Windows", "1.1.0"));
    }

    [Fact]
    public void CleansOnlyOldOwnedDirectInstallersAndKeepsOtherContents()
    {
        var updates = Directory.CreateDirectory(Path.Combine(directory, "updates")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(updates, Guid.NewGuid().ToString("N"))).FullName;
        var removable = Path.Combine(old, "TGDeblocker-Setup-1.2.3-x64.exe");
        var another = Path.Combine(old, "TGDeblocker-Setup-10.20.30-x64.exe");
        var foreign = Path.Combine(old, "notes.txt");
        var nearMatch = Path.Combine(old, "TGDeblocker-Setup-1.2.3-x64.exe.backup");
        var nested = Directory.CreateDirectory(Path.Combine(old, "nested")).FullName;
        var nestedInstaller = Path.Combine(nested, "TGDeblocker-Setup-1.2.3-x64.exe");
        foreach (var path in new[] { removable, another, foreign, nearMatch, nestedInstaller }) File.WriteAllText(path, "test");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));

        var fresh = Directory.CreateDirectory(Path.Combine(updates, Guid.NewGuid().ToString("N"))).FullName;
        var freshInstaller = Path.Combine(fresh, "TGDeblocker-Setup-1.2.3-x64.exe");
        File.WriteAllText(freshInstaller, "test");
        File.SetLastWriteTimeUtc(freshInstaller, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        var otherFolder = Directory.CreateDirectory(Path.Combine(updates, "third-party")).FullName;
        var otherInstaller = Path.Combine(otherFolder, "TGDeblocker-Setup-1.2.3-x64.exe");
        File.WriteAllText(otherInstaller, "test");
        Directory.SetLastWriteTimeUtc(otherFolder, DateTime.UtcNow.AddDays(-2));
        var oldEmpty = Directory.CreateDirectory(Path.Combine(updates, Guid.NewGuid().ToString("N"))).FullName;
        Directory.SetLastWriteTimeUtc(oldEmpty, DateTime.UtcNow.AddDays(-2));

        UpdatePaths.CleanOldStaging(directory);

        Assert.False(File.Exists(removable)); Assert.False(File.Exists(another));
        Assert.True(File.Exists(foreign)); Assert.True(File.Exists(nearMatch));
        Assert.True(File.Exists(nestedInstaller)); Assert.True(Directory.Exists(old));
        Assert.True(File.Exists(freshInstaller)); Assert.True(File.Exists(otherInstaller));
        Assert.False(Directory.Exists(oldEmpty));
    }

    [Fact]
    public void RejectsLinkedStagingDirectoryWithoutTouchingItsTarget()
    {
        var updates = Directory.CreateDirectory(Path.Combine(directory, "updates")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(directory, "outside")).FullName;
        var targetFile = Path.Combine(target, "TGDeblocker-Setup-1.2.3-x64.exe");
        File.WriteAllText(targetFile, "keep");
        var link = Path.Combine(updates, Guid.NewGuid().ToString("N"));
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { throw Xunit.Sdk.SkipException.ForSkip("Directory symbolic links unavailable: " + ex.GetType().Name); }
            Assert.Throws<UpdateValidationException>(() => UpdatePaths.CleanOldStaging(directory));
            Assert.Equal("keep", File.ReadAllText(targetFile));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link, false); }
    }

    [Fact]
    public void NeverDeletesInstallerNamedFileLinkOrItsTarget()
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "updates", Guid.NewGuid().ToString("N"))).FullName;
        var target = Path.Combine(directory, "outside.txt");
        File.WriteAllText(target, "keep");
        var link = Path.Combine(folder, "TGDeblocker-Setup-1.2.3-x64.exe");
        try
        {
            try { File.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { throw Xunit.Sdk.SkipException.ForSkip("File symbolic links unavailable: " + ex.GetType().Name); }
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-2));
            UpdatePaths.CleanOldStaging(directory);
            Assert.True(File.Exists(link));
            Assert.Equal("keep", File.ReadAllText(target));
        }
        finally { if (File.Exists(link)) File.Delete(link); }
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
    private static HttpResponseMessage Redirect(string url) { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(url); return response; }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int RequestCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request, ++RequestCount)); }
    }
    public void Dispose() { key.Dispose(); Directory.Delete(directory, true); }
}
