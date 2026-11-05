using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Intrinsics.X86;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed record PreparedMedia(string Archive, string RecoveryDmg, string DmgHash, string Chunklist, string ChunklistHash, CpuProfile CpuProfile, string? DriverArchive = null, string? DriverHash = null,
    string? AmdPatches = null, string? AmdPatchesHash = null);

public static class BootMedia
{
    // The pinned macrecovery release calls os.get_terminal_size even when stdout
    // is redirected. Supply geometry without editing its authenticated source.
    private const string RecoveryConsoleAdapter = "import os,runpy,sys; os.get_terminal_size=lambda fd=1: os.terminal_size((80,24)); ";
    /// <summary>
    /// Reads the host's CPUID directly. The reported vendor (from WMI) must agree with it, so a
    /// stale or spoofed host record cannot select the wrong kernel patches.
    /// </summary>
    public static CpuProfile DetectIntelProfile(string vendor, MacOsRelease? release = null)
    {
        release ??= MacOsCatalog.Default;
        if (!X86Base.IsSupported) throw new NotSupportedException("Automatic media requires an x64 processor.");
        var leaf0 = X86Base.CpuId(0, 0);
        Span<byte> id = stackalloc byte[12];
        BitConverter.TryWriteBytes(id[0..4], leaf0.Ebx); BitConverter.TryWriteBytes(id[4..8], leaf0.Edx); BitConverter.TryWriteBytes(id[8..12], leaf0.Ecx);
        var cpuVendor = System.Text.Encoding.ASCII.GetString(id);
        if (!vendor.Contains(cpuVendor, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"The recorded processor vendor ({vendor}) does not match this computer ({cpuVendor}). Refresh host discovery.");
        var signature = X86Base.CpuId(1, 0).Eax;
        var baseFamily = (signature >> 8) & 15;
        var family = baseFamily == 15 ? baseFamily + ((signature >> 20) & 0xFF) : baseFamily;
        var model = ((signature >> 4) & 15) | (baseFamily is 6 or 15 ? (signature >> 12) & 0xF0 : 0);
        return CpuCompatibility.Choose(cpuVendor, family, model, Avx2.IsSupported, release);
    }
    public static async Task<string> FetchAmdPatchesAsync(string cache, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache); var file = Path.Combine(cache, "AMD_Vanilla-eaf52ef-patches.plist");
        using var client = VerifiedDownloader.CreateClient();
        await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.AmdVanillaPatches, file, ct);
        return file;
    }
    public static async Task<string> FetchDriverAsync(string cache, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache); var archive = Path.Combine(cache, $"MacHyperVSupport-{UpstreamAssets.MacHyperVSupportVersion}-RELEASE.zip");
        using var client = VerifiedDownloader.CreateClient();
        await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.MacHyperVSupport, archive, ct);
        return archive;
    }
    public static async Task<string> FetchEfiAsync(string cache, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache); var archive = Path.Combine(cache, "OSX-Hyper-V-0.4.0.zip");
        using var client = VerifiedDownloader.CreateClient();
        await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.HyperVEfi, archive, ct);
        return archive;
    }
    public static async Task<PreparedMedia> PrepareAsync(string cache, string cpuVendor, string? pythonPath = null, CancellationToken ct = default, IProgress<string>? progress = null, MacOsRelease? release = null)
    {
        release ??= MacOsCatalog.Default;
        var profile = DetectIntelProfile(cpuVendor, release);
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
        string? driver = null;
        if (GuestDriverPolicy.UsesUpdatedDriver(release))
        {
            progress?.Report($"Downloading and checking MacHyperVSupport {UpstreamAssets.MacHyperVSupportVersion} for {release.DisplayName}…");
            driver = await FetchDriverAsync(cache, ct);
        }
        string? amd = null;
        if (profile == CpuProfile.AmdVanilla)
        {
            progress?.Report("Downloading and checking the pinned AMD_Vanilla kernel patches…");
            amd = await FetchAmdPatchesAsync(cache, ct);
        }
        var recovery = Path.Combine(cache, release.RecoveryCacheName);
        Directory.CreateDirectory(recovery);
        var dmg = Path.Combine(recovery, "BaseSystem.dmg"); var chunks = Path.Combine(recovery, "BaseSystem.chunklist");
        var utility = Path.Combine(work, "Utilities", "macrecovery", "macrecovery.py");
        // Only the authenticated upstream utility is executed, unelevated. No remote bootstrap scripts.
        // Rejected files are renamed, never reused or deleted, so evidence of a bad download remains.
        void SetAside()
        {
            var suffix = ".rejected-" + Guid.NewGuid().ToString("N");
            if (File.Exists(dmg)) File.Move(dmg, dmg + suffix);
            if (File.Exists(chunks)) File.Move(chunks, chunks + suffix);
        }
        var cached = File.Exists(dmg) && File.Exists(chunks);
        if (cached)
        {
            progress?.Report("Checking cached recovery against Apple's signed chunklist…");
            try { await VerifyRecoveryAsync(pythonPath, utility, dmg, chunks, ct, progress); }
            catch (InvalidDataException)
            {
                // An interrupted earlier download leaves a partial image; replace it instead of asking for two retries.
                SetAside(); cached = false;
                progress?.Report("Cached recovery failed Apple's checks and was set aside. Downloading a fresh copy…");
            }
        }
        if (!cached)
        {
            const int attempts = 3;
            for (var attempt = 1; ; attempt++)
            {
                progress?.Report(attempt == 1 ? $"Downloading Apple recovery for {release.DisplayName}…" : $"Apple's download was interrupted or failed verification. Retrying ({attempt}/{attempts})…");
                try
                {
                    // macrecovery verifies every chunk against Apple's signed chunklist while downloading.
                    await RunUtilityAsync(pythonPath, ["-I", "-u", "-c", RecoveryConsoleAdapter + "sys.argv=sys.argv[1:]; runpy.run_path(sys.argv[0],run_name='__main__')", utility,
                        .. release.RecoveryArguments, "-o", recovery, "-n", "BaseSystem", "download"], work, ct, progress);
                    break;
                }
                catch (InvalidDataException) when (!ct.IsCancellationRequested)
                {
                    SetAside();
                    if (attempt >= attempts)
                        throw new InvalidDataException($"Apple's recovery for {release.DisplayName} could not be downloaded intact after {attempts} attempts. Check the network connection and retry later; the rejected files were set aside.");
                }
            }
        }
        if (!File.Exists(dmg) || !File.Exists(chunks)) throw new InvalidDataException("The Apple recovery utility did not produce the expected verified files.");
        return new(archive, dmg, await HashAsync(dmg, ct), chunks, await HashAsync(chunks, ct), profile, driver, driver is null ? null : UpstreamAssets.MacHyperVSupport.Sha256,
            amd, amd is null ? null : UpstreamAssets.AmdVanillaPatches.Sha256);
    }
    public static Task VerifyRecoveryAsync(string python, string utility, string dmg, string chunks, CancellationToken ct = default, IProgress<string>? progress = null)
        => RunUtilityAsync(python, ["-I", "-u", "-c", RecoveryConsoleAdapter + "m=runpy.run_path(sys.argv[1]); m['verify_image'](sys.argv[2],sys.argv[3])", utility, dmg, chunks], Path.GetDirectoryName(utility)!, ct, progress);
    public static async Task<string> ConfigureAsync(string archive, string output, CpuProfile profile, MachineIdentity identity, CancellationToken ct = default, MacOsRelease? release = null, string? driverArchive = null,
        string? amdPatches = null, int cores = 0)
    {
        release ??= MacOsCatalog.Default;
        IReadOnlyList<System.Xml.Linq.XElement>? amd = null;
        if (profile == CpuProfile.AmdVanilla)
        {
            if (amdPatches is null) throw new InvalidDataException("AMD media requires the AMD_Vanilla kernel patches.");
            await SafePaths.VerifyHashAsync(amdPatches, UpstreamAssets.AmdVanillaPatches.Sha256, ct);
            await using var patches = File.OpenRead(amdPatches);
            amd = AmdKernelPatches.Read(patches);
        }
        await SafePaths.VerifyHashAsync(archive, UpstreamAssets.HyperVEfi.Sha256, ct);
        SafeArchive.Extract(archive, output);
        var updatedDriver = GuestDriverPolicy.UsesUpdatedDriver(release);
        if (updatedDriver)
        {
            if (driverArchive is null) throw new InvalidDataException($"{release.DisplayName} requires MacHyperVSupport {UpstreamAssets.MacHyperVSupportVersion}.");
            await ReplaceHyperVDriverAsync(driverArchive, output, ct);
        }
        var config = Path.Combine(output, "EFI", "OC", "config.plist");
        System.Xml.Linq.XDocument document;
        using (var input = File.OpenRead(config)) document = OpenCoreConfiguration.Configure(input, identity, profile, release, amd, cores, GuestDriverPolicy.LimitToOneCpu(updatedDriver));
        using (var writer = System.Xml.XmlWriter.Create(config, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, NewLineChars = "\n" })) document.Save(writer);
        return config;
    }
    /// <summary>Replaces the pinned EFI's MacHyperVSupport kexts with the verified newer release.</summary>
    public static async Task ReplaceHyperVDriverAsync(string driverArchive, string efiRoot, CancellationToken ct = default)
    {
        await SafePaths.VerifyHashAsync(driverArchive, UpstreamAssets.MacHyperVSupport.Sha256, ct);
        var kexts = Path.Combine(efiRoot, "EFI", "OC", "Kexts");
        var staging = Path.Combine(efiRoot, "driver-" + Guid.NewGuid().ToString("N"));
        SafeArchive.Extract(driverArchive, staging);
        try
        {
            var source = Path.Combine(staging, "Kexts");
            foreach (var name in new[] { "MacHyperVSupport.kext", "MacHyperVSupportMonterey.kext", "MacHyperVFramebuffer.kext" })
            {
                var replacement = Path.Combine(source, name);
                var info = Path.Combine(replacement, "Contents", "Info.plist");
                // The framebuffer is disabled in the pinned configuration; keep the old copy if the release omits it.
                if (name == "MacHyperVFramebuffer.kext" && !File.Exists(info)) continue;
                if (!File.Exists(info) ||!File.ReadAllText(info).Contains($"<string>{UpstreamAssets.MacHyperVSupportVersion}</string>", StringComparison.Ordinal))
                    throw new InvalidDataException($"The MacHyperVSupport archive does not contain the expected {name} {UpstreamAssets.MacHyperVSupportVersion}.");
                var target = Path.Combine(kexts, name);
                if (!Directory.Exists(target)) throw new InvalidDataException($"The pinned EFI is missing {name}; refusing to add an unconfigured kext.");
                Directory.Delete(target, true);
                Directory.Move(replacement, target);
            }
            // Keep the guest daemons installed by Scripts/post-install.sh in step with the kexts.
            var tools = Path.Combine(staging, "Tools");
            if (Directory.Exists(tools) && Directory.EnumerateFiles(tools, "fish.goldfish64.*.plist").Any())
            {
                var targetTools = Path.Combine(efiRoot, "Tools");
                if (Directory.Exists(targetTools)) Directory.Delete(targetTools, true);
                Directory.Move(tools, targetTools);
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
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
