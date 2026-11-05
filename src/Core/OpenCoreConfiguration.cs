using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace Pome.Core;

public sealed record MachineIdentity(Guid SystemUuid, string Serial, string BoardSerial, byte[] Rom)
{
    public static MachineIdentity Create()
    {
        const string alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        string RandomText(int length) => string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
        var rom = RandomNumberGenerator.GetBytes(6); rom[0] = (byte)((rom[0] | 2) & 0xFE);
        return new(Guid.NewGuid(), RandomText(12), RandomText(17), rom);
    }
}
public enum CpuProfile { NativeIntel, CometLakeEmulation, HaswellEmulation, IvyBridgeEmulation, AmdVanilla }

/// <summary>Edits a complete pinned Hyper-V template, never a bare-metal template or an incomplete plist.</summary>
public static class OpenCoreConfiguration
{
    public static XDocument Configure(Stream template, MachineIdentity identity, CpuProfile cpu, MacOsRelease? release = null,
        IReadOnlyList<XElement>? amdPatches = null, int cores = 0, bool limitToOneCpu = true)
    {
        release ??= MacOsCatalog.Default;
        if (cpu == CpuProfile.AmdVanilla && (amdPatches is null || cores < 1)) throw new InvalidDataException("AMD media requires AMD_Vanilla patches and the VM's processor count.");
        using var reader = XmlReader.Create(template, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, MaxCharactersInDocument = 2_000_000 });
        var document = XDocument.Load(reader);
        var root = document.Root?.Element("dict") ?? throw new InvalidDataException("Expected an XML property list dictionary.");
        XElement Dict(XElement parent, string key) => Value(parent, key, "dict");
        var kernel = Dict(root, "Kernel");
        var add = Value(kernel, "Add", "array");
        if (!add.Elements("dict").Any(x => Value(x, "BundlePath", "string").Value.StartsWith("MacHyperVSupport", StringComparison.Ordinal))) throw new InvalidDataException("This is not a MacHyperVSupport template.");
        var generic = Dict(Dict(root, "PlatformInfo"), "Generic");
        Set(generic, "SystemProductName", new XElement("string", release.SmbiosModel));
        Set(generic, "SystemUUID", new XElement("string", identity.SystemUuid.ToString().ToUpperInvariant()));
        Set(generic, "SystemSerialNumber", new XElement("string", identity.Serial));
        Set(generic, "MLB", new XElement("string", identity.BoardSerial));
        Set(generic, "ROM", new XElement("data", Convert.ToBase64String(identity.Rom)));
        Set(Dict(kernel, "Quirks"), "ProvideCurrentCpuInfo", new XElement("true"));
        var emulate = Dict(kernel, "Emulate");
        var data = new byte[16]; var mask = new byte[16];
        // CPUID leaf 1 EAX signatures of CPUs each release recognizes natively.
        byte[]? signature = cpu switch
        {
            CpuProfile.CometLakeEmulation => [0x55, 0x06, 0x0A, 0x00],
            CpuProfile.HaswellEmulation => [0xC3, 0x06, 0x03, 0x00],
            CpuProfile.IvyBridgeEmulation => [0xA9, 0x06, 0x03, 0x00],
            _ => null
        };
        if (signature is not null) { signature.CopyTo(data, 0); Array.Fill(mask, (byte)0xFF, 0, 4); }
        Set(emulate, "Cpuid1Data", new XElement("data", Convert.ToBase64String(data)));
        Set(emulate, "Cpuid1Mask", new XElement("data", Convert.ToBase64String(mask)));
        // The pinned guest driver can acknowledge SynIC on the wrong CPU. Until
        // a rebuilt fix is boot-validated, keep XNU on one active processor.
        // Hyper-V's configured processor allocation is retained for later use.
        var nvram = Dict(Dict(root, "NVRAM"), "Add");
        var appleBoot = Dict(nvram, "7C436110-AB2A-4BBB-A880-FE41995C9F82");
        var arguments = Value(appleBoot, "boot-args", "string").Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("cpus=", StringComparison.Ordinal)).Concat(limitToOneCpu ? ["cpus=1"] : []);
        Set(appleBoot, "boot-args", new XElement("string", string.Join(" ", arguments)));
        // Recovery is restored to its own HFS+ disk. Loading the DMG into RAM
        // collides with kernel allocations on a 4 GiB Hyper-V guest.
        var misc = Dict(root, "Misc");
        Set(Dict(misc, "Security"), "DmgLoading", new XElement("string", "Disabled"));
        var debug = Dict(misc, "Debug");
        Set(debug, "AppleDebug", new XElement("true"));
        Set(debug, "ApplePanic", new XElement("true"));
        Set(debug, "Target", new XElement("integer", 67));
        if (cpu == CpuProfile.AmdVanilla) AmdKernelPatches.Apply(root, amdPatches!, cores);
        if (release.LegacyBooter)
        {
            // Required by OS X 10.7 and older (OSX-Hyper-V patch.legacy.yml).
            var booterQuirks = Dict(Dict(root, "Booter"), "Quirks");
            Set(booterQuirks, "AllowRelocationBlock", new XElement("true"));
            Set(booterQuirks, "ForceExitBootServices", new XElement("true"));
        }
        // Preserve ACPI, kext ordering, Secure Boot, SIP and version-specific quirks.
        return document;
    }
    public static XElement Value(XElement dict, string key, string? expectedType = null)
    {
        var matches = dict.Elements("key").Where(x => x.Value == key).ToArray();
        if (matches.Length != 1 || matches[0].ElementsAfterSelf().FirstOrDefault() is not XElement value || value.Name == "key" || (expectedType is not null && value.Name != expectedType)) throw new InvalidDataException($"Missing, duplicate or invalid plist key: {key}");
        return value;
    }
    private static void Set(XElement dict, string key, XElement value) => Value(dict, key).ReplaceWith(value);
}
