using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TGDeblocker.App.Updating;

internal sealed record UpdateEnvelope(string Payload, string Signature);
internal sealed record UpdateManifest(int Schema, string Product, string Architecture, string Channel,
    string Version, string FileName, string Url, long Size, string Sha256);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(UpdateEnvelope))]
[JsonSerializable(typeof(UpdateManifest))]
internal partial class UpdateJsonContext : JsonSerializerContext { }

internal sealed class UpdateValidationException(string message) : Exception(message);

internal sealed class UpdatePolicy(string publicKey, string repository, string product)
{
    internal const int MaximumEnvelopeBytes = 65536;
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal string Repository { get; } = repository;
    internal string Product { get; } = product;
    internal Uri FeedUri => new($"https://github.com/{Repository}/releases/latest/download/windows-update.json");

    internal static Version ParseVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})$"))
            throw new UpdateValidationException("Invalid release version.");
        var parts = value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return new Version(parts[0], parts[1], parts[2]);
    }

    internal UpdateManifest Verify(ReadOnlySpan<byte> envelopeBytes)
    {
        try
        {
            if (envelopeBytes.Length is 0 or > MaximumEnvelopeBytes) throw new UpdateValidationException("Invalid manifest size.");
            var envelope = JsonSerializer.Deserialize(envelopeBytes, UpdateJsonContext.Default.UpdateEnvelope)
                ?? throw new UpdateValidationException("Missing signed manifest.");
            var payload = Convert.FromBase64String(envelope.Payload);
            var signature = Convert.FromBase64String(envelope.Signature);
            if (payload.Length is 0 or > 32768 || signature.Length != 64) throw new UpdateValidationException("Invalid signature envelope.");
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out var consumed);
            if (consumed != Convert.FromBase64String(publicKey).Length || key.KeySize != 256 ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new UpdateValidationException("Update signature verification failed.");
            var manifest = JsonSerializer.Deserialize(payload, UpdateJsonContext.Default.UpdateManifest)
                ?? throw new UpdateValidationException("Missing update fields.");
            _ = ParseVersion(manifest.Version);
            var expectedName = $"TGDeblocker-Setup-{manifest.Version}-x64.exe";
            var expectedUrl = $"https://github.com/{Repository}/releases/download/v{manifest.Version}/{expectedName}";
            if (manifest.Schema != 1 || manifest.Product != Product || manifest.Architecture != "x64" || manifest.Channel != "stable" ||
                manifest.FileName != expectedName || manifest.Url != expectedUrl || manifest.Size is <= 0 or > MaximumInstallerBytes ||
                manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "^[a-fA-F0-9]{64}$"))
                throw new UpdateValidationException("Update does not match this application.");
            return manifest;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or NullReferenceException)
        {
            throw new UpdateValidationException("Invalid signed update manifest.");
        }
    }

    internal static bool IsNewer(UpdateManifest manifest, string current) => ParseVersion(manifest.Version) > ParseVersion(current);

    internal bool IsAllowedDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        return uri.Host == "github.com" ? uri.AbsolutePath.StartsWith('/' + Repository + "/releases/", StringComparison.Ordinal)
            : uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    }
}
