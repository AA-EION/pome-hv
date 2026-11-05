using System.Net;
using System.Net.Http.Headers;

namespace Pome.Infrastructure;

public sealed record DownloadAsset(Uri Url, string Sha256, long Size, string License);
public static class UpstreamAssets
{
    public static readonly DownloadAsset NullMoth = new(new("https://github.com/nullmoth/nvidia-macos-driver/releases/download/v2.0.2/nullmoth-nvidia-2.0.2.tar.gz"), "acf939c2829ae31a1840866cefc814c4b819168da1eb5f1ea257ea3e63e8753d", 378125394, "PolyForm-Noncommercial-1.0.0");
    public static readonly DownloadAsset HyperVEfi = new(new("https://github.com/Qonfused/OSX-Hyper-V/releases/download/0.4.0/EFI-0.4.0-64-bit-RELEASE.zip"), "43b68f135f908f046058c7cb4a6968a9caec57ac22bbaadebcf891289375a428", 14408392, "See upstream component licenses");
    // MacHyperVSupport 1.0.0 is built with the MacKernelSDK IOPCIHostBridge layout fix
    // (acidanthera/MacKernelSDK#25) that macOS 26 needs. Digest from GitHub release metadata.
    public static readonly DownloadAsset MacHyperVSupport = new(new("https://github.com/acidanthera/MacHyperVSupport/releases/download/1.0.0/MacHyperVSupport-1.0.0-RELEASE.zip"), "490ebb1f8822b99d7efc175c0ce0e7a035a844093319927216570106f45bc0cf", 4852457, "BSD-3-Clause");
    public const string MacHyperVSupportVersion = "1.0.0";
    // AMD_Vanilla publishes no license file, so pome downloads this commit-pinned file at runtime instead of bundling it.
    public static readonly DownloadAsset AmdVanillaPatches = new(new("https://raw.githubusercontent.com/AMD-OSX/AMD_Vanilla/eaf52ef292abf4ebec899df6d48626569ba50cc6/patches.plist"), "4bc820109b3d020c3c547fa23c49e0098e4f4a2c625ed6184dd54e390e84e1ab", 19661, "No license file published upstream; downloaded, not redistributed");
    public static readonly DownloadAsset PythonEmbed = new(new("https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"), "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3", 11133606, "PSF-2.0");
}

public sealed class VerifiedDownloader(HttpClient client)
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    private static void ValidateUrl(Uri url)
    {
        if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length > 0 ||
            url.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "raw.githubusercontent.com" or "www.python.org"))
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
