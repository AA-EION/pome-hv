namespace Pome.Core;

public static class HostCompatibility
{
    public static IReadOnlyList<Capability> Assess(HostInfo host)
    {
        var edition = host.IsServer || host.Edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)
            || host.Edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
            || host.Edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase);
        return
        [
            new("Windows edition", edition ? CapabilityStatus.Supported : CapabilityStatus.HostOsRestricted,
                edition ? host.Os : "Hyper-V requires Windows Pro, Enterprise, Education or a supported Server edition."),
            new("Hyper-V", host.HyperVAvailable switch { true => CapabilityStatus.Supported, false => CapabilityStatus.Unsupported, _ => CapabilityStatus.Unknown },
                host.HyperVAvailable == true ? "Hyper-V management is available. Guest boot still needs validation." : "Enable Hyper-V and restart if Windows requests it. Access restrictions can also prevent discovery."),
            new("Processor virtualization", host.HypervisorPresent == true || (host.VirtualizationFirmware == true && host.Slat == true) ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
                host.HypervisorPresent == true ? "A hypervisor is running." : "Firmware virtualization and SLAT must be available; an unknown result is not evidence of failure."),
            new("macOS guest", CapabilityStatus.DetectedUntested, "Upstream Sequoia support exists. Pome has not validated a guest boot on this computer. Tahoe is not enabled."),
            new("Standard graphics", CapabilityStatus.Experimental, "OpenCore display and MacHyperVSupport framebuffer path. No Metal acceleration claim."),
            new("GPU partitioning", CapabilityStatus.Unsupported, "No validated macOS GPU-P guest driver is integrated."),
            new("GPU passthrough", host.IsServer ? CapabilityStatus.Unknown : CapabilityStatus.HostOsRestricted,
                host.IsServer ? "DDA hardware eligibility, host display continuity and macOS driver operation remain unverified. Assignment is disabled." : "Microsoft documents DDA for Windows Server hosts. Client Windows GPU assignment is disabled.")
        ];
    }
}

public static class ResourcePolicy
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    public static ResourceAdvice Recommend(HostInfo host)
    {
        // Reserve at least 6 GiB installed and 2 GiB currently available for Windows.
        var installedBudget = host.TotalMemoryBytes > 6 * GiB ? (host.TotalMemoryBytes - 6 * GiB) / GiB : 0;
        var availableBudget = host.AvailableMemoryBytes > 2 * GiB ? (host.AvailableMemoryBytes - 2 * GiB) / GiB : 0;
        var maximum = (int)Math.Min(128UL, Math.Min(installedBudget, availableBudget));
        var cpuMax = Math.Max(1, host.LogicalProcessors - 2);
        var cpu = Math.Min(cpuMax, Math.Max(2, Math.Min(8, host.PhysicalCores - 2)));
        var ready = maximum >= 8 && cpuMax >= 2;
        return new(cpu, cpuMax, Math.Min(8, maximum), maximum, 100, ready,
            ready ? "Static memory, 6 GB reserved from installed RAM and 2 GB of currently free RAM kept for Windows. Two logical processors remain for the host."
            : "Close memory-intensive apps or add RAM. This Sequoia profile requires 8 GB available for the guest while preserving the Windows reserve.");
    }
}

public static class GpuSafety
{
    public static Capability Evaluate(HostInfo host, GpuInfo gpu) => !host.IsServer
        ? new(gpu.Name, CapabilityStatus.HostOsRestricted, "DDA is not enabled on Windows client.")
        : gpu.DrivesDisplay != false
            ? new(gpu.Name, CapabilityStatus.Unknown, "Display ownership is active or unknown; detachment is prohibited.")
            : new(gpu.Name, CapabilityStatus.DetectedUntested, "Display check alone does not establish IOMMU isolation, reset, power or guest compatibility.");
}
