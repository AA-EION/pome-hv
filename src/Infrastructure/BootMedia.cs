using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Intrinsics.X86;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed record PreparedMedia(string Archive, string RecoveryDmg, string DmgHash, string Chunklist, string ChunklistHash, CpuProfile CpuProfile);

public static class BootMedia
{
    public static CpuProfile DetectIntelProfile(string vendor)
    {
        if (!X86Base.IsSupported || !vendor.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Automatic EFI preparation currently supports a limited Intel CPU matrix. Use reviewed custom EFI media for other processors.");
        var signature = X86Base.CpuId(1, 0).Eax;
        var family = (signature >> 8) & 15;
        var model = ((signature >> 4) & 15) | ((signature >> 12) & 0xF0);
        if (family != 6) throw new NotSupportedException("This Intel CPU family needs a reviewed custom EFI profile.");
        return model switch
        {
            0x8C or 0x8D or 0x97 or 0x9A or 0xB7 or 0xBA or 0xBF => CpuProfile.CometLakeEmulation,
            0x3C or 0x3F or 0x45 or 0x46 or 0x3D or 0x47 or 0x4F or 0x56 or 0x4E or 0x5E or 0x8E or 0x9E or 0xA5 or 0xA6 => CpuProfile.NativeIntel,
            _ => throw new NotSupportedException($"Intel model 0x{model:X2} is outside the automatic profile matrix. Use reviewed custom EFI media.")
        };
    }
    public static async Task<string> FetchEfiAsync(string cache, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache); var archive = Path.Combine(cache, "OSX-Hyper-V-0.4.0.zip");
        using var client = VerifiedDownloader.CreateClient();
        await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.HyperVEfi, archive, ct);
        return archive;
    }
    public static async Task<PreparedMedia> PrepareAsync(string cache, string cpuVendor, string pythonPath, CancellationToken ct = default)
    {
        var profile = DetectIntelProfile(cpuVendor);
        pythonPath = SafePaths.RequireLocal(pythonPath);
        if (!pythonPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose an installed Python 3 executable.");
        var archive = await FetchEfiAsync(cache, ct);
        var work = Path.Combine(cache, "prepare-" + Guid.NewGuid().ToString("N"));
        SafeArchive.Extract(archive, work);
        var recovery = Path.Combine(work, "com.apple.recovery.boot");
        // Only the authenticated upstream utility is executed, unelevated. No remote bootstrap scripts.
        await RunUtilityAsync(pythonPath, ["-I", Path.Combine(work, "Utilities", "macrecovery", "macrecovery.py"), "-b", "Mac-7BA5B2D9E42DDD94", "-m", "00000000000000000", "-o", recovery, "-n", "BaseSystem", "download"], work, ct);
        var dmg = Path.Combine(recovery, "BaseSystem.dmg"); var chunks = Path.Combine(recovery, "BaseSystem.chunklist");
        if (!File.Exists(dmg) || !File.Exists(chunks)) throw new InvalidDataException("The Apple recovery utility did not produce the expected verified files.");
        return new(archive, dmg, await HashAsync(dmg, ct), chunks, await HashAsync(chunks, ct), profile);
    }
    public static async Task<string> ConfigureAsync(string archive, string output, CpuProfile profile, MachineIdentity identity, CancellationToken ct = default)
    {
        await SafePaths.VerifyHashAsync(archive, UpstreamAssets.HyperVEfi.Sha256, ct);
        SafeArchive.Extract(archive, output);
        var config = Path.Combine(output, "EFI", "OC", "config.plist");
        System.Xml.Linq.XDocument document;
        using (var input = File.OpenRead(config)) document = OpenCoreConfiguration.Configure(input, identity, profile);
        using (var writer = System.Xml.XmlWriter.Create(config, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, NewLineChars = "\n" })) document.Save(writer);
        return config;
    }
    public static async Task ValidateAsync(string directory, CancellationToken ct = default)
    {
        await RunUtilityAsync(Path.Combine(directory, "Utilities", "ocvalidate", "ocvalidate.exe"), [Path.Combine(directory, "EFI", "OC", "config.plist")], directory, ct);
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }
    private static async Task RunUtilityAsync(string executable, IEnumerable<string> arguments, string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The media utility did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException($"Media utility failed ({process.ExitCode}): {Redaction.Clean(error + output)}");
    }
}
