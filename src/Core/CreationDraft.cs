namespace Pome.Core;

// Discovery supplies defaults only while the user has not edited that field.
public sealed class CreationDraft
{
    private bool cpuEdited, memoryEdited, networkEdited, nameEdited;
    private double cpu = ResourcePolicy.CompatibilityCpuCount, memory = 8;
    private string network = "Disconnected", name = MacOsCatalog.Default.Name;
    private MacOsRelease release = MacOsCatalog.Default;
    private IReadOnlyCollection<string> knownNames = [];
    /// <summary>Changing the version renames an unedited suggestion (for example "Sequoia" to "Tahoe").</summary>
    public MacOsRelease Release { get => release; set { if (release == value) return; release = value; SuggestName(); } }
    public double CpuCount { get => cpu; set { if (cpu == value) return; cpu = value; cpuEdited = true; } }
    public double MemoryGiB { get => memory; set { if (memory == value) return; memory = value; memoryEdited = true; } }
    public string Network { get => network; set { if (network == value) return; network = value; networkEdited = true; } }
    public string Name { get => name; set { if (name == value) return; name = value; nameEdited = true; } }

    public void ApplyHost(HostInfo host, bool useRecommendations = false)
    {
        var advice = ResourcePolicy.Recommend(host);
        if (!cpuEdited || useRecommendations) cpu = Math.Max(2, Math.Min(64, advice.CpuCount));
        if (!memoryEdited || useRecommendations) memory = Math.Max(4, advice.MemoryGiB);
        if (!networkEdited) network = host.Switches.FirstOrDefault(x => x == "Default Switch") ?? host.Switches.FirstOrDefault() ?? "Disconnected";
    }

    public void ApplyMachines(IEnumerable<VmInfo> machines)
    {
        knownNames = machines.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SuggestName();
    }

    private void SuggestName()
    {
        if (nameEdited) return;
        var names = knownNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stem = release.Name;
        name = stem;
        for (var suffix = 2; names.Contains(name); suffix++) name = $"{stem} {suffix}";
    }

    public static int WholeNumber(double value, string label)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > int.MaxValue)
            throw new InvalidDataException($"Enter a whole number for {label}.");
        return (int)value;
    }
}
