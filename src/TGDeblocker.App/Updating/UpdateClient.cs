using System.Net;
using System.Security.Cryptography;

namespace TGDeblocker.App.Updating;

internal sealed class UpdateClient(HttpClient client, UpdatePolicy policy)
{
    internal async Task<UpdateManifest?> CheckAsync(string current, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(policy.FeedUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > UpdatePolicy.MaximumEnvelopeBytes) throw new UpdateValidationException("Update manifest too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > UpdatePolicy.MaximumEnvelopeBytes) throw new UpdateValidationException("Update manifest too large.");
            output.Write(buffer, 0, read);
        }
        var manifest = policy.Verify(output.ToArray());
        return UpdatePolicy.IsNewer(manifest, current) ? manifest : null;
    }

    internal async Task DownloadAsync(UpdateManifest manifest, string current, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!UpdatePolicy.IsNewer(manifest, current)) throw new UpdateValidationException("Downgrade refused.");
        using var response = await GetAsync(new Uri(manifest.Url), cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != manifest.Size)
            throw new UpdateValidationException("Unexpected installer size.");
        bool created = false;
        try
        {
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            created = true;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
            {
                written += read;
                if (written > manifest.Size) throw new UpdateValidationException("Installer exceeds its signed size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report((double)written / manifest.Size);
            }
            if (written != manifest.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(manifest.Sha256)))
                throw new UpdateValidationException("Installer integrity check failed.");
            await output.FlushAsync(cancellationToken);
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!policy.IsAllowedDownloadUri(uri)) throw new UpdateValidationException("Untrusted update redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("TGDeblocker-Updater/1.0");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 5) throw new UpdateValidationException("Invalid update redirect.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new UpdateValidationException("Too many redirects.");
    }
}
