using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Intrinsics.X86;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed record PreparedMedia(string Archive, string RecoveryDmg, string DmgHash, string Chunklist, string ChunklistHash, CpuProfile CpuProfile);

public static class BootMedia
{
    // The pinned macrecovery release calls os.get_terminal_size even when stdout
    // is redirected. Supply geometry without editing its authenticated source.
    private const string RecoveryConsoleAdapter = "import os,runpy,sys; os.get_terminal_size=lambda fd=1: os.terminal_size((80,24)); ";
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
    public static async Task<PreparedMedia> PrepareAsync(string cache, string cpuVendor, string? pythonPath = null, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var profile = DetectIntelProfile(cpuVendor);
        Directory.CreateDirectory(cache);
        SafePaths.RequireLocal(cache);
        // Separate processes cannot replace the recovery pair while it is being verified.
        await using var lease = new FileStream(Path.Combine(cache, "preparation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
        {
            pythonPath = await PythonLocator.EnsureManagedAsync(Path.GetDirectoryName(Path.GetFullPath(cache)) ?? cache, ct, progress);
        }
        pythonPath = SafePaths.RequireLocal(pythonPath);
        if (!pythonPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose an installed Python 3 executable.");
        await RunUtilityAsync(pythonPath, ["-I", "-c", "import sys; assert sys.version_info >= (3, 9), 'Python 3.9 or newer is required'"], cache, ct);
        progress?.Report("Downloading and checking the pinned EFI tools…");
        var archive = await FetchEfiAsync(cache, ct);
        var work = Path.Combine(cache, "prepare-" + Guid.NewGuid().ToString("N"));
        SafeArchive.Extract(archive, work);
        var recovery = Path.Combine(cache, "recovery-Mac-7BA5B2D9E42DDD94");
        Directory.CreateDirectory(recovery);
        var dmg = Path.Combine(recovery, "BaseSystem.dmg"); var chunks = Path.Combine(recovery, "BaseSystem.chunklist");
        var utility = Path.Combine(work, "Utilities", "macrecovery", "macrecovery.py");
        // Only the authenticated upstream utility is executed, unelevated. No remote bootstrap scripts.
        if (File.Exists(dmg) && File.Exists(chunks))
        {
            progress?.Report("Checking cached recovery against Apple's signed chunklist…");
            try { await VerifyRecoveryAsync(pythonPath, utility, dmg, chunks, ct, progress); }
            catch (InvalidDataException)
            {
                var suffix = ".rejected-" + Guid.NewGuid().ToString("N");
                File.Move(dmg, dmg + suffix); File.Move(chunks, chunks + suffix);
                throw new InvalidDataException("Cached recovery failed Apple's signature or image checks and was set aside. Retry preparation to download a fresh copy.");
            }
        }
        else
        {
            progress?.Report("Downloading Apple recovery…");
            await RunUtilityAsync(pythonPath, ["-I", "-u", "-c", RecoveryConsoleAdapter + "sys.argv=sys.argv[1:]; runpy.run_path(sys.argv[0],run_name='__main__')", utility, "-b", "Mac-7BA5B2D9E42DDD94", "-m", "00000000000000000", "-o", recovery, "-n", "BaseSystem", "download"], work, ct, progress);
        }
        if (!File.Exists(dmg) || !File.Exists(chunks)) throw new InvalidDataException("The Apple recovery utility did not produce the expected verified files.");
        return new(archive, dmg, await HashAsync(dmg, ct), chunks, await HashAsync(chunks, ct), profile);
    }
    public static Task VerifyRecoveryAsync(string python, string utility, string dmg, string chunks, CancellationToken ct = default, IProgress<string>? progress = null)
        => RunUtilityAsync(python, ["-I", "-u", "-c", RecoveryConsoleAdapter + "m=runpy.run_path(sys.argv[1]); m['verify_image'](sys.argv[2],sys.argv[3])", utility, dmg, chunks], Path.GetDirectoryName(utility)!, ct, progress);
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
    private static async Task RunUtilityAsync(string executable, IEnumerable<string> arguments, string directory, CancellationToken ct, IProgress<string>? progress = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The media utility did not start.");
        var stdout = ReadOutputAsync(process.StandardOutput, progress, ct); var stderr = ReadOutputAsync(process.StandardError, null, ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException($"Media utility failed ({process.ExitCode}): {Redaction.Clean(error + output)}");
    }
    private static async Task<string> ReadOutputAsync(StreamReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        var tail = new System.Text.StringBuilder(); var line = new System.Text.StringBuilder();
        var buffer = new char[1024]; var lastReport = Stopwatch.StartNew(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            tail.Append(buffer, 0, count); if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
            foreach (var character in buffer.AsSpan(0, count))
            {
                if (character is '\r' or '\n')
                {
                    if (line.Length > 0 && lastReport.ElapsedMilliseconds >= 500)
                    { progress?.Report(Redaction.Clean(line.ToString())); lastReport.Restart(); }
                    line.Clear();
                }
                else if (line.Length < 512) line.Append(character);
            }
        }
        return tail.ToString();
    }
}
