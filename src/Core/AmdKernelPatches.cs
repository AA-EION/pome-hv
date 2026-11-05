using System.Xml;
using System.Xml.Linq;

namespace Pome.Core;

/// <summary>
/// Merges AMD_Vanilla kernel patches (https://github.com/AMD-OSX/AMD_Vanilla) into a Hyper-V OpenCore
/// configuration, following OSX-Hyper-V/OCE-Build's amd.py with --hyperv: the four core-count patches
/// receive the VM's processor count, DummyPowerManagement is enabled and Hyper-V's invalid wrmsr in
/// _cpu_syscall_init is removed for 10.13+.
/// </summary>
public static class AmdKernelPatches
{
    public const int MinimumDarwin = 17;
    public const string CoreCountComment = "Force cpuid_cores_per_package";
    public const string HyperVWrmsrComment = "pome | Hyper-V | _cpu_syscall_init | Remove invalid wrmsr | 10.13+";

    /// <summary>Reads the Kernel → Patch dictionaries of an AMD_Vanilla patches.plist.</summary>
    public static IReadOnlyList<XElement> Read(Stream patches)
    {
        using var reader = XmlReader.Create(patches, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true, MaxCharactersInDocument = 1_000_000 });
        var root = XDocument.Load(reader).Root?.Element("dict") ?? throw new InvalidDataException("Expected an AMD_Vanilla property list.");
        var list = OpenCoreConfiguration.Value(OpenCoreConfiguration.Value(root, "Kernel", "dict"), "Patch", "array").Elements("dict").ToArray();
        if (list.Length is < 10 or > 200) throw new InvalidDataException("The AMD patch list has an unexpected size.");
        if (list.Count(x => Comment(x).Contains(CoreCountComment, StringComparison.Ordinal)) != 4)
            throw new InvalidDataException("The AMD patch list does not contain the four expected core-count patches.");
        return list;
    }

    /// <summary>Adds the patches to Kernel → Patch, replacing any earlier pome-applied AMD entries.</summary>
    public static void Apply(XElement configRoot, IReadOnlyList<XElement> patches, int cores)
    {
        if (cores is < 1 or > 255) throw new InvalidDataException("The AMD core-count patch needs 1–255 processors.");
        var kernel = OpenCoreConfiguration.Value(configRoot, "Kernel", "dict");
        var target = OpenCoreConfiguration.Value(kernel, "Patch", "array");
        var comments = patches.Select(Comment).Append(HyperVWrmsrComment).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in target.Elements("dict").Where(x => comments.Contains(Comment(x))).ToArray()) existing.Remove();
        foreach (var source in patches)
        {
            var patch = new XElement(source);
            if (Comment(patch).Contains(CoreCountComment, StringComparison.Ordinal))
            {
                var replace = OpenCoreConfiguration.Value(patch, "Replace", "data");
                var bytes = Convert.FromBase64String(replace.Value.Trim());
                if (bytes.Length < 2 || bytes[0] is not (0xB8 or 0xBA) || bytes[1] != 0)
                    throw new InvalidDataException("An AMD core-count patch has an unexpected placeholder.");
                bytes[1] = (byte)cores;
                replace.Value = Convert.ToBase64String(bytes);
            }
            target.Add(patch);
        }
        target.Add(HyperVWrmsr());
        var emulate = OpenCoreConfiguration.Value(kernel, "Emulate", "dict");
        SetOrAdd(emulate, "DummyPowerManagement", new XElement("true"));
    }

    private static XElement HyperVWrmsr() => new("dict",
        Entry("Arch", new XElement("string", "x86_64")), Entry("Base", new XElement("string", "_cpu_syscall_init")),
        Entry("Comment", new XElement("string", HyperVWrmsrComment)), Entry("Count", new XElement("integer", 3)),
        Entry("Enabled", new XElement("true")), Entry("Find", new XElement("data", Convert.ToBase64String([0x0F, 0x30]))),
        Entry("Identifier", new XElement("string", "kernel")), Entry("Limit", new XElement("integer", 0)),
        Entry("Mask", new XElement("data")), Entry("MaxKernel", new XElement("string", "")),
        Entry("MinKernel", new XElement("string", "17.0.0")), Entry("Replace", new XElement("data", Convert.ToBase64String([0x90, 0x90]))),
        Entry("ReplaceMask", new XElement("data")), Entry("Skip", new XElement("integer", 0)));

    private static IEnumerable<XElement> Entry(string key, XElement value) => [new XElement("key", key), value];
    private static string Comment(XElement patch) =>
        patch.Elements("key").FirstOrDefault(x => x.Value == "Comment")?.ElementsAfterSelf().FirstOrDefault()?.Value ?? "";
    private static void SetOrAdd(XElement dict, string key, XElement value)
    {
        var existing = dict.Elements("key").Where(x => x.Value == key).ToArray();
        if (existing.Length > 1) throw new InvalidDataException($"Duplicate plist key: {key}");
        if (existing.Length == 1) existing[0].ElementsAfterSelf().First().ReplaceWith(value);
        else dict.Add(new XElement("key", key), value);
    }
}
