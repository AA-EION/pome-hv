namespace Pome.Core;

public static class HostCompatibility
{
    public static bool HyperVEnabled(HostInfo host) => host.HyperVAvailable == true || host.HyperV?.FeatureEnabled == true || host.HyperV?.ServiceRunning == true;
    public static Capability HyperVCapability(HostInfo host)
    {
        if (host.HyperVAvailable == true) return new("Hyper-V", CapabilityStatus.Supported, "Enabled. Virtual machine management is available. Guest boot still needs validation.");
        if (HyperVEnabled(host)) return new("Hyper-V", CapabilityStatus.Supported,
            "Enabled. Windows restricted the management query. Choose Inspect with administrator access to load virtual machines and networks; the feature does not need to be enabled again.");
        if (host.HyperV?.FeatureEnabled == false) return new("Hyper-V", CapabilityStatus.Unsupported, "The Hyper-V feature is disabled. Enable it, then restart if Windows requests it.");
        return new("Hyper-V", CapabilityStatus.Unknown,
            $"Feature state could not be confirmed. Management service: {host.HyperV?.ServiceState ?? "unavailable"}. Inspect with administrator access to resolve this; a running hypervisor alone can also be used by Windows security.");
    }
    public static IReadOnlyList<Capability> Assess(HostInfo host)
    {
        var edition = host.IsServer || host.Edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)
            || host.Edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
            || host.Edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase);
        return
        [
            new("Windows edition", edition ? CapabilityStatus.Supported : CapabilityStatus.HostOsRestricted,
                edition ? host.Os : "Hyper-V requires Windows Pro, Enterprise, Education or a supported Server edition."),
            HyperVCapability(host),
            new("Processor virtualization", host.HypervisorPresent == true || (host.VirtualizationFirmware == true && host.Slat == true) ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
                host.HypervisorPresent == true ? "A hypervisor is running." : "Firmware virtualization and SLAT must be available; an unknown result is not evidence of failure."),
            new("macOS guest", CapabilityStatus.DetectedUntested, "OS X 10.7 through macOS 26 can be selected. Sequoia reached Recovery and the installer on the reference host; other versions follow upstream support and remain untested by pome. Tahoe uses the updated MacHyperVSupport 1.0.0 driver."),
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
    public const int CompatibilityCpuCount = 2;
    /// <summary>macOS expects logical CPUs to divide evenly into packages; powers of two are safest.</summary>
    public static bool IsPowerOfTwo(int count) => count > 0 && (count & (count - 1)) == 0;
    public static ResourceAdvice Recommend(HostInfo host)
    {
        // Reserve at least 6 GiB installed and 2 GiB currently available for Windows.
        var totalGiB = (host.TotalMemoryBytes + GiB / 2) / GiB;
        var installedBudget = totalGiB > 6 ? totalGiB - 6 : 0;
        var availableBudget = host.AvailableMemoryBytes > 2 * GiB ? (host.AvailableMemoryBytes - 2 * GiB) / GiB : 0;
        var maximum = (int)Math.Min(128UL, Math.Min(installedBudget, availableBudget));
        var cpuMax = Math.Max(1, host.LogicalProcessors - 2);
        // Compatibility boot keeps macOS on one active processor (cpus=1) until a
        // corrected multi-CPU guest driver ships, so extra vCPUs are idle in the guest.
        var cpu = Math.Min(cpuMax, CompatibilityCpuCount);
        var ready = maximum >= 4 && cpuMax >= 2;
        return new(cpu, cpuMax, Math.Min(8, maximum), maximum, 100, ready,
            ready ? "Static memory, 6 GB reserved from installed RAM and 2 GB of currently free RAM kept for Windows. Two logical processors remain for the host. macOS currently runs on one active processor in compatibility mode, so 2 virtual processors are recommended; more stay idle until the multi-CPU driver fix ships."
            : "Close memory-intensive apps or add RAM. macOS guests require at least 4 GB available while preserving the Windows reserve.");
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
