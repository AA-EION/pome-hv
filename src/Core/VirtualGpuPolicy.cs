using System.Xml.Linq;

namespace Pome.Core;

/// <summary>What pome changed to turn on the virtual GPU, so turning it off restores exactly that.</summary>
public sealed record VirtualGpuRecord(int SchemaVersion, string OriginalBootArgs, string? OriginalCsrActiveConfig, bool AddedBootArgsDelete,
    bool AddedCsrDelete, DateTimeOffset EnabledAt);

/// <summary>
/// Experimental Pome Virtual GPU: Vulkan forwarding from a macOS 26 Tahoe guest to the host GPU
/// (any vendor) over a Hyper-V socket. The guest runs PVGTransport.kext, Mesa's Venus driver and
/// the PVGMTL Metal layer; the host runs pvg-host (gpu/host). See gpu/README.md.
///
/// The Metal layer is an unsigned GPU bundle that WindowServer and apps load, so the guest needs the
/// same relaxed AMFI and SIP settings as NullMoth's driver. Pome changes them only on request and
/// records the originals.
/// </summary>
public static class VirtualGpuPolicy
{
    public const int MinimumDarwin = 25;   // macOS 26 Tahoe
    /// <summary>The Hyper-V socket service: the vsock template GUID for port "PVG1" (gpu/protocol/pvg_wire.h).</summary>
    public static readonly Guid ServiceId = new("50564731-facb-11e6-bd58-64006a7986d3");
    public const string ServiceName = "Pome Virtual GPU";
    public const string ServicesKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices";
    public const string RecordFile = "virtual-gpu.json";
    private const string AppleBootGuid = "7C436110-AB2A-4BBB-A880-FE41995C9F82";
    /// <summary>The guest settings NullMoth's Metal driver was validated with (its README, csr-active-config 0x0A43).</summary>
    public static readonly string[] BootArgs = ["amfi_get_out_of_my_way=0x1", "amfi=0x80", "ipc_control_port_options=0"];
    public static readonly byte[] CsrActiveConfig = [0x43, 0x0A, 0x00, 0x00];

    public static void RequireCanEnable(VmInfo vm, VmDetails details)
    {
        RequireTahoe(vm, details);
        if (details.VirtualGpu) throw new InvalidDataException("The virtual GPU is already on for this VM.");
    }

    public static void RequireCanDisable(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id) throw new InvalidDataException("VMs created outside pome are read-only.");
        if (!details.VirtualGpu) throw new InvalidDataException("The virtual GPU is not on for this VM.");
    }

    private static void RequireTahoe(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id) throw new InvalidDataException("VMs created outside pome are read-only.");
        var release = MacOsCatalog.Find(details.GuestMacOsVersion)
            ?? throw new InvalidDataException("Pome does not know which macOS this VM runs. The virtual GPU is offered for VMs created as macOS 26 Tahoe with this version of pome.");
        if (release.Darwin < MinimumDarwin) throw new InvalidDataException($"The virtual GPU is experimental and limited to macOS 26 Tahoe VMs; this VM runs {release.DisplayName}.");
    }

    /// <summary>Adds the AMFI boot arguments and the SIP value to an OpenCore config and returns what to restore.</summary>
    public static VirtualGpuRecord Enable(XDocument config)
    {
        var (nvramAdd, nvramDelete) = Nvram(config);
        var appleBoot = Dict(nvramAdd, AppleBootGuid);
        var bootArgs = OpenCoreConfiguration.Value(appleBoot, "boot-args", "string");
        var original = bootArgs.Value;
        var keys = BootArgs.Select(Key).ToHashSet(StringComparer.Ordinal);
        var kept = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(x => !keys.Contains(Key(x)));
        bootArgs.Value = string.Join(" ", kept.Concat(BootArgs));
        var csr = TryValue(appleBoot, "csr-active-config");
        var originalCsr = csr?.Value;
        if (csr is null) Append(appleBoot, "csr-active-config", new XElement("data", Convert.ToBase64String(CsrActiveConfig)));
        else csr.ReplaceWith(new XElement("data", Convert.ToBase64String(CsrActiveConfig)));
        // OpenCore writes NVRAM>Add only for variables it may overwrite (listed in NVRAM>Delete).
        var deleteList = DeleteList(nvramDelete);
        var addedBootArgs = AddDelete(deleteList, "boot-args");
        var addedCsr = AddDelete(deleteList, "csr-active-config");
        return new(1, original, originalCsr, addedBootArgs, addedCsr, DateTimeOffset.UtcNow);
    }

    /// <summary>Restores the boot arguments and SIP value recorded when the virtual GPU was turned on.</summary>
    public static void Disable(XDocument config, VirtualGpuRecord record)
    {
        var (nvramAdd, nvramDelete) = Nvram(config);
        var appleBoot = Dict(nvramAdd, AppleBootGuid);
        // Keep later edits (cpus=, other flags) and drop only what Enable added.
        var keys = BootArgs.Select(Key).ToHashSet(StringComparer.Ordinal);
        var originalTokens = record.OriginalBootArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var restoredFromOriginal = originalTokens.Where(x => keys.Contains(Key(x)));
        var current = OpenCoreConfiguration.Value(appleBoot, "boot-args", "string");
        current.Value = string.Join(" ", current.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !keys.Contains(Key(x))).Concat(restoredFromOriginal));
        var csr = TryValue(appleBoot, "csr-active-config");
        if (record.OriginalCsrActiveConfig is null) { if (csr is not null) Remove(appleBoot, "csr-active-config"); }
        else if (csr is not null) csr.ReplaceWith(new XElement("data", record.OriginalCsrActiveConfig));
        else Append(appleBoot, "csr-active-config", new XElement("data", record.OriginalCsrActiveConfig));
        var deleteList = DeleteList(nvramDelete);
        if (record.AddedBootArgsDelete) deleteList.Elements("string").Where(x => x.Value == "boot-args").Remove();
        if (record.AddedCsrDelete) deleteList.Elements("string").Where(x => x.Value == "csr-active-config").Remove();
    }

    private static string Key(string arg) { var i = arg.IndexOf('='); return i < 0 ? arg : arg[..i]; }

    private static (XElement Add, XElement Delete) Nvram(XDocument config)
    {
        var root = config.Root?.Element("dict") ?? throw new InvalidDataException("Expected an XML property list dictionary.");
        var nvram = Dict(root, "NVRAM");
        if (TryValue(nvram, "Delete") is null) Append(nvram, "Delete", new XElement("dict"));
        return (Dict(nvram, "Add"), Dict(nvram, "Delete"));
    }

    private static XElement DeleteList(XElement nvramDelete)
    {
        if (TryValue(nvramDelete, AppleBootGuid) is { } list)
        {
            if (list.Name != "array") throw new InvalidDataException($"NVRAM Delete {AppleBootGuid} is not an array.");
            return list;
        }
        var created = new XElement("array");
        Append(nvramDelete, AppleBootGuid, created);
        return created;
    }

    private static bool AddDelete(XElement list, string name)
    {
        if (list.Elements("string").Any(x => x.Value == name)) return false;
        list.Add(new XElement("string", name));
        return true;
    }

    private static XElement Dict(XElement parent, string key) => OpenCoreConfiguration.Value(parent, key, "dict");

    private static XElement? TryValue(XElement dict, string key)
    {
        var matches = dict.Elements("key").Where(x => x.Value == key).ToArray();
        if (matches.Length == 0) return null;
        return OpenCoreConfiguration.Value(dict, key);
    }

    private static void Append(XElement dict, string key, XElement value) { dict.Add(new XElement("key", key)); dict.Add(value); }

    private static void Remove(XElement dict, string key)
    {
        var k = dict.Elements("key").Single(x => x.Value == key);
        k.ElementsAfterSelf().First().Remove();
        k.Remove();
    }
}
