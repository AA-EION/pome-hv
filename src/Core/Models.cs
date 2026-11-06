using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pome.Core;

public enum CapabilityStatus { Supported, Experimental, DetectedUntested, Unsupported, HardwareIneligible, HostOsRestricted, Unknown }
public sealed record Capability(string Name, CapabilityStatus Status, string Detail);
public sealed record GpuInfo(string Name, string PnpId, string DriverVersion, string? LocationPath, bool? DrivesDisplay = null, ulong? BarBytes = null);
public sealed record HyperVEvidence(bool? FeatureEnabled, bool ModuleAvailable, string? ServiceState, string? ManagementError)
{
    public bool ServiceRunning => string.Equals(ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
}
public sealed record HostInfo(string Manufacturer, string Model, string Bios, string Os, string Edition,
    int Build, bool IsServer, string Cpu, string CpuVendor, int PhysicalCores, int LogicalProcessors,
    ulong TotalMemoryBytes, ulong AvailableMemoryBytes, bool? VirtualizationFirmware, bool? Slat,
    bool? HypervisorPresent, bool? HyperVAvailable, string[] Switches, GpuInfo[] Gpus, string[] Warnings, HyperVEvidence? HyperV = null);
public sealed record ResourceAdvice(int CpuCount, int MaximumCpuCount, int MemoryGiB, int MaximumMemoryGiB, int DiskGiB, bool CanCreate, string Explanation);
public sealed record VmInfo(Guid Id, string Name, string State, int ProcessorCount, ulong MemoryAssigned, int CpuUsage, string Notes)
{
    public string Summary => $"{State} · {ProcessorCount} processors · {MemoryAssigned / 1073741824.0:0.#} GB assigned";
    public bool Managed => Notes.StartsWith("pome:v1:", StringComparison.Ordinal);
    public bool CanStart => Managed && State is "Off" or "Saved";
    public bool CanShutdown => Managed && State == "Running";
    public bool CanPause => Managed && State == "Running";
    public bool CanResume => Managed && State == "Paused";
    public bool CanSave => Managed && State is "Running" or "Paused";
    public bool CanConfigure => Managed && State == "Off";
    public bool CanTurnOff => Managed && State is not ("Off" or "Saved");
    public bool CanReset => Managed && State == "Running";
    public bool CanDelete => Managed && State == "Off";
    public bool IsTransitioning => State is not ("Off" or "Saved" or "Running" or "Paused");
}
public sealed record VmConfiguration(string Name, int CpuCount, int MemoryGiB, string? SwitchName);
public sealed record VmDisk(string Path, ulong CapacityBytes, string Controller)
{
    public string Summary => $"{(CapacityBytes == 0 ? "Capacity unavailable" : $"{CapacityBytes / ResourcePolicy.GiB} GB")} · {Controller}\n{Path}";
}
public sealed record VmCheckpoint(Guid Id, string Name, DateTime CreationTime)
{
    public string Summary => $"{Name} · {CreationTime:g}";
}
public sealed record VmDetails(Guid VmId, string Name, int CpuCount, ulong StartupMemoryBytes, string? SwitchName,
    string ConfigurationLocation, string SecureBoot, VmDisk[] Disks, VmCheckpoint[] Checkpoints, string[] BootOrder,
    string[]? GpuPartitions = null, PartitionableGpu[]? PartitionableGpus = null, string? GuestMacOsVersion = null, bool VirtualGpu = false);
public sealed record CreatePlan(int SchemaVersion, Guid OperationId, string Name, string StorageRoot, int CpuCount, int MemoryGiB,
    int DiskGiB, string? SwitchName, string EfiSource, string EfiSha256, string? RecoverySource, string? RecoverySha256,
    string MacOsVersion, bool AcceptLicense, bool AcceptGuestFirmwareChange,
    bool BuildEfiFromArchive = false, CpuProfile CpuProfile = CpuProfile.NativeIntel,
    string? RecoveryDmg = null, string? RecoveryDmgSha256 = null, string? RecoveryChunklist = null, string? RecoveryChunklistSha256 = null,
    string? HyperVDriverArchive = null, string? HyperVDriverSha256 = null,
    string? AmdPatches = null, string? AmdPatchesSha256 = null);
public enum OperationKind { Create, Start, Shutdown, EnableHyperV, Inspect, InspectVm, Configure, Pause, Resume, Save, Checkpoint, RestoreCheckpoint, DeleteCheckpoint, RepairBoot, Export, ResizeDisk, ExportBootLogs, TurnOff, Reset, DeleteVm, GrantHyperVAccess, AddGpuPartition, RemoveGpuPartition, EnableVirtualGpu, DisableVirtualGpu }
public sealed record OperationRequest(OperationKind Kind, CreatePlan? Plan = null, Guid? VmId = null,
    VmConfiguration? Configuration = null, Guid? CheckpointId = null, string? Destination = null, Guid? OperationId = null, int? DiskGiB = null, bool DeleteFiles = false, string? GpuInstancePath = null, bool SkipMemoryReserve = false);
public sealed record OperationResult(bool Success, string Message, Guid? VmId = null, string? ResourceDirectory = null,
    HostInfo? Host = null, VmInfo[]? VirtualMachines = null, string? Stage = null, VmDetails? Details = null);
public sealed record OperationMarker(Guid OperationId, string Stage, string Detail, bool Terminal, Guid? VmId, DateTimeOffset UpdatedAt);
public sealed record JournalEntry(Guid Id, string Kind, string Stage, string Detail, DateTimeOffset UpdatedAt, bool Terminal, string? ResourceDirectory = null);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Empty response.");
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public interface IHostDiscovery { Task<HostInfo> DiscoverAsync(CancellationToken cancellationToken = default); }
public interface IVirtualMachineManager { Task<IReadOnlyList<VmInfo>> ListAsync(CancellationToken cancellationToken = default); }
public interface IPrivilegeBroker { Task<OperationResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default); }
public interface IOperationJournal
{
    void Write(JournalEntry entry);
    IReadOnlyList<JournalEntry> ReadAll();
}
