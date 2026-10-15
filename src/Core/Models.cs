using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pome.Core;

public enum CapabilityStatus { Supported, Experimental, DetectedUntested, Unsupported, HardwareIneligible, HostOsRestricted, Unknown }
public sealed record Capability(string Name, CapabilityStatus Status, string Detail);
public sealed record GpuInfo(string Name, string PnpId, string DriverVersion, string? LocationPath, bool? DrivesDisplay = null, ulong? BarBytes = null);
public sealed record HostInfo(string Manufacturer, string Model, string Bios, string Os, string Edition,
    int Build, bool IsServer, string Cpu, string CpuVendor, int PhysicalCores, int LogicalProcessors,
    ulong TotalMemoryBytes, ulong AvailableMemoryBytes, bool? VirtualizationFirmware, bool? Slat,
    bool? HypervisorPresent, bool? HyperVAvailable, string[] Switches, GpuInfo[] Gpus, string[] Warnings);
public sealed record ResourceAdvice(int CpuCount, int MaximumCpuCount, int MemoryGiB, int MaximumMemoryGiB, int DiskGiB, bool CanCreate, string Explanation);
public sealed record VmInfo(Guid Id, string Name, string State, int ProcessorCount, ulong MemoryAssigned, int CpuUsage, string Notes)
{
    public string Summary => $"{State} · {ProcessorCount} processors · {MemoryAssigned / 1073741824.0:0.#} GB assigned";
    public bool Managed => Notes.StartsWith("pome:v1:", StringComparison.Ordinal);
}
public sealed record CreatePlan(int SchemaVersion, Guid OperationId, string Name, string StorageRoot, int CpuCount, int MemoryGiB,
    int DiskGiB, string? SwitchName, string EfiSource, string EfiSha256, string? RecoverySource, string? RecoverySha256,
    string MacOsVersion, bool AcceptLicense, bool AcceptGuestFirmwareChange,
    bool BuildEfiFromArchive = false, CpuProfile CpuProfile = CpuProfile.NativeIntel,
    string? RecoveryDmg = null, string? RecoveryDmgSha256 = null, string? RecoveryChunklist = null, string? RecoveryChunklistSha256 = null);
public enum OperationKind { Create, Start, Shutdown, EnableHyperV }
public sealed record OperationRequest(OperationKind Kind, CreatePlan? Plan = null, Guid? VmId = null);
public sealed record OperationResult(bool Success, string Message, Guid? VmId = null, string? ResourceDirectory = null);
public sealed record JournalEntry(Guid Id, string Kind, string Stage, string Detail, DateTimeOffset UpdatedAt, bool Terminal);

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
