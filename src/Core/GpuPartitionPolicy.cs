namespace Pome.Core;

/// <summary>A GPU that Hyper-V can partition (Get-VMHostPartitionableGpu), matched to its display adapter name.</summary>
public sealed record PartitionableGpu(string InstancePath, string Name)
{
    public string Summary => $"{Name}\n{InstancePath}";
}

/// <summary>Written to the VM's pome folder at creation, so later features know which guest it holds.</summary>
public sealed record GuestRecord(int SchemaVersion, string MacOsVersion, CpuProfile CpuProfile, DateTimeOffset CreatedAt);

/// <summary>
/// Experimental Hyper-V GPU partitioning (GPU-P) for macOS guests. A partition gives the guest a
/// paravirtual WDDM device (virtual PCI 1414:008E plus dxgkrnl VMBus channels), not an NVIDIA PCI
/// function, so no macOS driver uses it yet. Pome attaches it only to Tahoe VMs, for guest-driver
/// development, and keeps every other card path (NullMoth on real PCI, DDA) untouched.
/// See docs/GPU_P_MACOS.md.
/// </summary>
public static class GpuPartitionPolicy
{
    public const int MinimumDarwin = 25;                     // macOS 26 Tahoe
    public const ulong LowMmioBytes = 1UL << 30;             // 1 GB, as ExHyperV and Microsoft's GPU-P guidance use
    public const ulong HighMmioBytes = 32UL << 30;           // 32 GB high MMIO gap
    public const string VirtualRenderDevice = "PCI 1414:008E";

    public static void RequireCanAdd(VmInfo vm, VmDetails details, string? instancePath)
    {
        RequireTahoe(vm, details);
        if (details.Checkpoints.Length != 0) throw new InvalidDataException("Delete this VM's checkpoints first. Hyper-V cannot checkpoint or save a VM with a GPU partition.");
        if ((details.GpuPartitions ?? []).Length != 0) throw new InvalidDataException("This VM already has a GPU partition.");
        if (string.IsNullOrWhiteSpace(instancePath) || !(details.PartitionableGpus ?? []).Any(x => string.Equals(x.InstancePath, instancePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a GPU that this computer reports as partitionable. Refresh the VM's settings.");
    }

    public static void RequireCanRemove(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id) throw new InvalidDataException("VMs created outside pome are read-only.");
        if ((details.GpuPartitions ?? []).Length == 0) throw new InvalidDataException("This VM has no GPU partition.");
    }

    private static void RequireTahoe(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id) throw new InvalidDataException("VMs created outside pome are read-only.");
        var release = MacOsCatalog.Find(details.GuestMacOsVersion);
        if (release is null) throw new InvalidDataException("Pome does not know which macOS this VM runs. GPU partitions are offered for VMs created as macOS 26 Tahoe with this version of pome.");
        if (release.Darwin < MinimumDarwin) throw new InvalidDataException($"GPU partitions are experimental and limited to macOS 26 Tahoe VMs; this VM runs {release.DisplayName}.");
    }

    /// <summary>Converts a GPU-P instance path (\\?\PCI#VEN_..#..#{guid}) to its PnP device ID for name matching.</summary>
    public static string? PnpIdFromInstancePath(string instancePath)
    {
        var path = instancePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? instancePath[4..] : instancePath;
        var parts = path.Split('#');
        return parts.Length >= 3 ? string.Join('\\', parts[0], parts[1], parts[2]) : null;
    }
}
