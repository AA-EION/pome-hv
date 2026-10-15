using System.Net;
using System.Net.Http.Headers;

namespace Pome.Infrastructure;

public sealed record DownloadAsset(Uri Url, string Sha256, long Size, string License);
public static class UpstreamAssets
{
    public static readonly DownloadAsset NullMoth = new(new("https://github.com/nullmoth/nvidia-macos-driver/releases/download/v2.0.2/nullmoth-nvidia-2.0.2.tar.gz"), "acf939c2829ae31a1840866cefc814c4b819168da1eb5f1ea257ea3e63e8753d", 378125394, "PolyForm-Noncommercial-1.0.0");
    public static readonly DownloadAsset HyperVEfi = new(new("https://github.com/Qonfused/OSX-Hyper-V/releases/download/0.4.0/EFI-0.4.0-64-bit-RELEASE.zip"), "43b68f135f908f046058c7cb4a6968a9caec57ac22bbaadebcf891289375a428", 14408392, "See upstream component licenses");
}

public sealed class VerifiedDownloader(HttpClient client)
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    private static void ValidateUrl(Uri url)
    {
        if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length > 0 ||
            url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException("Download host is not allowlisted.");
    }
    public async Task DownloadAsync(DownloadAsset asset, string destination, CancellationToken ct = default)
    {
        ValidateUrl(asset.Url);
        if (asset.Size <= 0 || asset.Size > 4L * 1024 * 1024 * 1024 || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid pinned asset metadata.");
        if (File.Exists(destination)) { await SafePaths.VerifyHashAsync(destination, asset.Sha256, ct); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var partial = destination + ".part";
        // FileShare.None prevents two downloaders from appending to the same partial file.
        await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true))
        {
            if (file.Length > asset.Size) file.SetLength(0);
            if (file.Length < asset.Size)
            {
                var offset = file.Length;
                var uri = asset.Url;
                HttpResponseMessage? response = null;
                try
                {
                    for (var redirects = 0; redirects <= 5; redirects++)
                    {
                        ValidateUrl(uri);
                        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        request.Headers.UserAgent.ParseAdd("pome/0.1");
                        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                        {
                            var location = response.Headers.Location ?? throw new InvalidDataException("Missing redirect location.");
                            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                            response.Dispose(); response = null; continue;
                        }
                        break;
                    }
                    if (response is null) throw new InvalidDataException("Too many download redirects.");
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range?.From != offset || range.Length != asset.Size || range.To != asset.Size - 1) throw new InvalidDataException("Unexpected download range.");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK) { offset = 0; file.SetLength(0); }
                    else throw new InvalidDataException("Unexpected download response.");
                    file.Position = offset;
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[1024 * 1024]; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        if (file.Position + count > asset.Size) throw new InvalidDataException("Download exceeds the pinned asset size.");
                        await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    }
                    if (file.Length != asset.Size) throw new IOException("Download interrupted. The partial file is available for resume.");
                    await file.FlushAsync(ct); file.Flush(true);
                }
                finally { response?.Dispose(); }
            }
        }
        try { await SafePaths.VerifyHashAsync(partial, asset.Sha256, ct); }
        catch (InvalidDataException) { File.Move(partial, partial + ".rejected-" + Guid.NewGuid().ToString("N")); throw; }
        File.Move(partial, destination, false);
    }
}
