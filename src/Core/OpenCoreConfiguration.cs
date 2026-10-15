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
public enum CpuProfile { NativeIntel, CometLakeEmulation }

/// <summary>Edits a complete pinned Hyper-V template, never a bare-metal template or an incomplete plist.</summary>
public static class OpenCoreConfiguration
{
    public static XDocument Configure(Stream template, MachineIdentity identity, CpuProfile cpu)
    {
        using var reader = XmlReader.Create(template, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, MaxCharactersInDocument = 2_000_000 });
        var document = XDocument.Load(reader);
        var root = document.Root?.Element("dict") ?? throw new InvalidDataException("Expected an XML property list dictionary.");
        XElement Dict(XElement parent, string key) => Value(parent, key, "dict");
        var kernel = Dict(root, "Kernel");
        var add = Value(kernel, "Add", "array");
        if (!add.Elements("dict").Any(x => Value(x, "BundlePath", "string").Value.StartsWith("MacHyperVSupport", StringComparison.Ordinal))) throw new InvalidDataException("This is not a MacHyperVSupport template.");
        var generic = Dict(Dict(root, "PlatformInfo"), "Generic");
        Set(generic, "SystemProductName", new XElement("string", "iMac20,1"));
        Set(generic, "SystemUUID", new XElement("string", identity.SystemUuid.ToString().ToUpperInvariant()));
        Set(generic, "SystemSerialNumber", new XElement("string", identity.Serial));
        Set(generic, "MLB", new XElement("string", identity.BoardSerial));
        Set(generic, "ROM", new XElement("data", Convert.ToBase64String(identity.Rom)));
        Set(Dict(kernel, "Quirks"), "ProvideCurrentCpuInfo", new XElement("true"));
        var emulate = Dict(kernel, "Emulate");
        var data = new byte[16]; var mask = new byte[16];
        if (cpu == CpuProfile.CometLakeEmulation) { new byte[] { 0x55, 0x06, 0x0A, 0 }.CopyTo(data, 0); Array.Fill(mask, (byte)0xFF, 0, 4); }
        Set(emulate, "Cpuid1Data", new XElement("data", Convert.ToBase64String(data)));
        Set(emulate, "Cpuid1Mask", new XElement("data", Convert.ToBase64String(mask)));
        // Preserve all ACPI, kext ordering, security settings and version-specific quirks from the pinned template.
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
