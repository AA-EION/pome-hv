namespace Pome.Core;

public enum GuestEvidence { InstallerReached, UpstreamSupported, Experimental }

/// <summary>
/// One installable macOS release. Recovery parameters follow OpenCorePkg's
/// Utilities/macrecovery/recovery_urls.txt; SMBIOS models are chosen so the
/// recovery's own supported-model list includes them.
/// </summary>
public sealed record MacOsRelease(
    string Version, string Name, int Darwin, string BoardId, string Mlb, bool LatestRecovery,
    string SmbiosModel, GuestEvidence Evidence, bool RequiresAvx2 = false,
    bool RequiresUpdatedHyperVDriver = false, bool LegacyBooter = false)
{
    // Apple renamed OS X to macOS with 10.12 Sierra (Darwin 16).
    public string DisplayName => Darwin >= 16 ? $"macOS {Version} {Name}" : $"OS X {Version} {Name}";
    public string EvidenceSummary => Evidence switch
    {
        GuestEvidence.InstallerReached => "Recovery and installer reached on the pome reference host",
        GuestEvidence.UpstreamSupported => "Supported by the upstream Hyper-V EFI; not yet tested by pome",
        _ => "Experimental; not yet tested by pome"
    };
    public string PickerLabel => $"{DisplayName} — {EvidenceSummary}";
    /// <summary>Cache folder for this release's Apple recovery files.</summary>
    public string RecoveryCacheName => $"recovery-{Version}-{BoardId}";
    public IReadOnlyList<string> RecoveryArguments =>
        LatestRecovery ? ["-b", BoardId, "-m", Mlb, "-os", "latest"] : ["-b", BoardId, "-m", Mlb];
}

public static class MacOsCatalog
{
    public const string DefaultVersion = "15";
    private const string ZeroMlb = "00000000000000000";

    public static IReadOnlyList<MacOsRelease> Releases { get; } =
    [
        // Tahoe needs MacHyperVSupport 1.0.0, built with the MacKernelSDK
        // IOPCIHostBridge layout fix. Pome replaces the pinned 0.9.x driver.
        new("26", "Tahoe", 25, "Mac-CFF7D910A743CAAF", ZeroMlb, true, "iMac20,1", GuestEvidence.Experimental, RequiresAvx2: true, RequiresUpdatedHyperVDriver: true),
        new("15", "Sequoia", 24, "Mac-7BA5B2D9E42DDD94", ZeroMlb, true, "iMac20,1", GuestEvidence.InstallerReached, RequiresAvx2: true),
        new("14", "Sonoma", 23, "Mac-827FAC58A8FDFA22", ZeroMlb, false, "iMac20,1", GuestEvidence.UpstreamSupported, RequiresAvx2: true),
        new("13", "Ventura", 22, "Mac-B4831CEBD52A0C4C", ZeroMlb, false, "iMac20,1", GuestEvidence.UpstreamSupported, RequiresAvx2: true),
        new("12", "Monterey", 21, "Mac-E43C1C25D4880AD6", ZeroMlb, false, "iMac20,1", GuestEvidence.UpstreamSupported),
        new("11", "Big Sur", 20, "Mac-2BD1B31983FE1663", ZeroMlb, false, "iMac20,1", GuestEvidence.UpstreamSupported),
        // iMac20,1 first shipped with 10.15.6. Older recoveries need older models.
        new("10.15", "Catalina", 19, "Mac-00BE6ED71E35EB86", ZeroMlb, false, "iMac19,1", GuestEvidence.UpstreamSupported),
        new("10.14", "Mojave", 18, "Mac-7BA5B2DFE22DDD8C", "00000000000KXPG00", false, "iMac19,1", GuestEvidence.UpstreamSupported),
        new("10.13", "High Sierra", 17, "Mac-7BA5B2D9E42DDD94", "00000000000J80300", false, "iMac18,3", GuestEvidence.UpstreamSupported),
        new("10.12", "Sierra", 16, "Mac-77F17D7DA9285301", "00000000000J0DX00", false, "iMac17,1", GuestEvidence.UpstreamSupported),
        new("10.11", "El Capitan", 15, "Mac-FFE5EF870D7BA81A", "00000000000GQRX00", false, "iMac17,1", GuestEvidence.UpstreamSupported),
        new("10.10", "Yosemite", 14, "Mac-E43C1C25D4880AD6", "00000000000GDVW00", false, "iMac14,2", GuestEvidence.UpstreamSupported),
        new("10.9", "Mavericks", 13, "Mac-F60DEB81FF30ACF6", "00000000000FNN100", false, "iMac14,2", GuestEvidence.UpstreamSupported),
        new("10.8", "Mountain Lion", 12, "Mac-7DF2A3B5E5D671ED", "00000000000F65100", false, "iMac14,2", GuestEvidence.UpstreamSupported),
        new("10.7", "Lion", 11, "Mac-2E6FAB96566FE58C", "00000000000F25Y00", false, "iMac12,2", GuestEvidence.Experimental, LegacyBooter: true),
    ];

    public static MacOsRelease Default => Find(DefaultVersion)!;
    public static MacOsRelease? Find(string? version) => Releases.FirstOrDefault(x => x.Version == version);
    public static MacOsRelease Require(string? version) => Find(version) ?? throw new InvalidDataException("Choose a macOS version from pome's catalog.");
}

/// <summary>
/// Which guest driver automatic media uses, and whether macOS must stay on one active CPU.
/// The pinned OSX-Hyper-V EFI carries MacHyperVSupport 0.9.9, which can acknowledge SynIC messages
/// on the wrong vCPU. When <see cref="PinnedDriverFixesSmp"/> is true, the pinned replacement driver
/// (built by .github/workflows/build-machypervsupport.yml) is used for every release and the
/// cpus=1 limit is dropped. Flip it only together with a new pin that passed the multi-CPU boot gate
/// in docs/MULTI_CPU_DRIVER.md.
/// </summary>
public static class GuestDriverPolicy
{
    public static bool PinnedDriverFixesSmp { get; } = false;
    public static bool UsesUpdatedDriver(MacOsRelease release) => release.RequiresUpdatedHyperVDriver || PinnedDriverFixesSmp;
    public static bool LimitToOneCpu(bool updatedDriver) => !(updatedDriver && PinnedDriverFixesSmp);
}

/// <summary>Intel host classification for automatic OpenCore CPU profiles.</summary>
public static class CpuCompatibility
{
    private sealed record Generation(string Name, int NativeMinimumDarwin, int? NativeMaximumDarwin, bool NeedsEmulation);

    private static Generation? Classify(int model) => model switch
    {
        0x2A or 0x2D => new("Sandy Bridge", 11, 21, false),
        0x3A or 0x3E => new("Ivy Bridge", 11, 21, false),
        0x3C or 0x3F or 0x45 or 0x46 => new("Haswell", 12, null, false),
        0x3D or 0x47 or 0x4F or 0x56 => new("Broadwell", 14, null, false),
        0x4E or 0x5E => new("Skylake", 15, null, false),
        0x55 => new("Skylake-SP / Cascade Lake", 17, null, false),
        0x8E or 0x9E => new("Kaby Lake / Coffee Lake", 17, null, false),
        0xA5 or 0xA6 => new("Comet Lake", 19, null, false),
        0x7D or 0x7E => new("Ice Lake", 19, null, false),
        0xA7 => new("Rocket Lake", 0, null, true),
        0x8C or 0x8D => new("Tiger Lake", 0, null, true),
        0x97 or 0x9A or 0xBE => new("Alder Lake", 0, null, true),
        0xB7 or 0xBA or 0xBF => new("Raptor Lake", 0, null, true),
        0xAA or 0xAC => new("Meteor Lake", 0, null, true),
        0xC5 or 0xC6 => new("Arrow Lake", 0, null, true),
        0xBD => new("Lunar Lake", 0, null, true),
        0x8F or 0xCF => new("Sapphire / Emerald Rapids", 0, null, true),
        _ => null
    };

    /// <summary>Chooses the profile from the CPUID vendor string ("GenuineIntel" or "AuthenticAMD").</summary>
    public static CpuProfile Choose(string vendor, int family, int model, bool avx2, MacOsRelease release) => vendor switch
    {
        "GenuineIntel" => Choose(true, family, model, avx2, release),
        "AuthenticAMD" => ChooseAmd(family, avx2, release),
        _ => throw new NotSupportedException($"Processor vendor '{vendor}' is not supported by automatic media. Use reviewed custom EFI media.")
    };

    /// <summary>
    /// AMD hosts use AMD_Vanilla kernel patches (families 15h, 16h, 17h, 19h and 1Ah), which start at
    /// macOS 10.13. Bulldozer and Jaguar (15h/16h) stop at Monterey upstream.
    /// </summary>
    public static CpuProfile ChooseAmd(int family, bool avx2, MacOsRelease release)
    {
        if (family is not (0x15 or 0x16 or 0x17 or 0x19 or 0x1A))
            throw new NotSupportedException($"AMD family {family:X2}h is outside the AMD_Vanilla patch set. Use reviewed custom EFI media.");
        if (release.Darwin < AmdKernelPatches.MinimumDarwin)
            throw new NotSupportedException("AMD processors need macOS 10.13 High Sierra or newer.");
        if (family is 0x15 or 0x16 && release.Darwin > 21)
            throw new NotSupportedException("AMD Bulldozer and Jaguar processors are supported up to macOS 12 Monterey.");
        if (release.RequiresAvx2 && !avx2)
            throw new NotSupportedException($"{release.DisplayName} requires AVX2. Choose macOS 12 Monterey or older on this processor.");
        return CpuProfile.AmdVanilla;
    }

    /// <summary>Chooses the CPUID profile for this host and guest, or explains why automatic media cannot be built.</summary>
    public static CpuProfile Choose(bool genuineIntel, int family, int model, bool avx2, MacOsRelease release)
    {
        if (!genuineIntel)
            throw new NotSupportedException("This processor is not an Intel CPU. AMD hosts use the AMD_Vanilla profile; other vendors need reviewed custom EFI media.");
        if (family != 6) throw new NotSupportedException("This Intel CPU family needs a reviewed custom EFI profile.");
        var generation = Classify(model)
            ?? throw new NotSupportedException($"Intel model 0x{model:X2} is outside the automatic profile matrix. Use reviewed custom EFI media.");
        if (release.RequiresAvx2 && !avx2)
            throw new NotSupportedException($"{release.DisplayName} requires AVX2 (Haswell or newer). Choose macOS 12 Monterey or older on this {generation.Name} host.");
        if (generation.NativeMaximumDarwin is int maximum && release.Darwin > maximum)
            throw new NotSupportedException($"{generation.Name} processors are supported up to macOS 12 Monterey. Choose an older macOS version.");
        if (!generation.NeedsEmulation && release.Darwin >= generation.NativeMinimumDarwin) return CpuProfile.NativeIntel;
        // Newer or unrecognized-by-macOS CPUs present the newest CPUID the release knows.
        return release.Darwin switch
        {
            >= 19 => CpuProfile.CometLakeEmulation,
            >= 12 => CpuProfile.HaswellEmulation,
            _ => CpuProfile.IvyBridgeEmulation
        };
    }
}
