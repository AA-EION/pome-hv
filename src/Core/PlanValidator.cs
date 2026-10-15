using System.Text.RegularExpressions;

namespace Pome.Core;

public static partial class PlanValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    public static IReadOnlyList<string> Validate(CreatePlan plan, HostInfo? host = null)
    {
        var errors = new List<string>();
        if (plan.SchemaVersion != 1 || plan.OperationId == Guid.Empty) errors.Add("Unsupported plan version or operation identifier.");
        if (!NamePattern().IsMatch(plan.Name ?? "")) errors.Add("Use 1–64 letters, digits, spaces, dots, underscores or hyphens for the VM name.");
        if (plan.CpuCount is < 2 or > 64) errors.Add("Choose 2–64 virtual processors.");
        if (plan.MemoryGiB is < 8 or > 128) errors.Add("Choose 8–128 GB of static memory.");
        if (plan.DiskGiB is < 64 or > 2048) errors.Add("Choose 64–2048 GB of disk capacity.");
        if (plan.MacOsVersion != "15") errors.Add("Only the experimental Sequoia profile is enabled.");
        if (!plan.AcceptLicense) errors.Add("Review the applicable Apple software license before creating a guest.");
        if (!plan.AcceptGuestFirmwareChange) errors.Add("Consent is required to disable Secure Boot on the new guest.");
        if (string.IsNullOrWhiteSpace(plan.StorageRoot)) errors.Add("Choose a storage folder.");
        if (string.IsNullOrWhiteSpace(plan.EfiSource) || !HashPattern().IsMatch(plan.EfiSha256 ?? "")) errors.Add("Select an EFI VHDX and its trusted SHA-256 digest.");
        if (!string.IsNullOrWhiteSpace(plan.RecoverySource) && !HashPattern().IsMatch(plan.RecoverySha256 ?? "")) errors.Add("The recovery VHDX requires a trusted SHA-256 digest.");
        if (!Enum.IsDefined(plan.CpuProfile)) errors.Add("Unknown CPU profile.");
        if (plan.BuildEfiFromArchive && (string.IsNullOrWhiteSpace(plan.RecoveryDmg) || string.IsNullOrWhiteSpace(plan.RecoveryChunklist)
            || !HashPattern().IsMatch(plan.RecoveryDmgSha256 ?? "") || !HashPattern().IsMatch(plan.RecoveryChunklistSha256 ?? "")))
            errors.Add("Automatic EFI creation requires verified recovery DMG and chunklist files.");
        if (host is not null)
        {
            var advice = ResourcePolicy.Recommend(host);
            if (!advice.CanCreate || plan.MemoryGiB > advice.MaximumMemoryGiB || plan.CpuCount > advice.MaximumCpuCount)
                errors.Add("The allocation exceeds the current safe host resource budget. Refresh host discovery.");
            if (host.HyperVAvailable != true) errors.Add("Hyper-V availability has not been confirmed.");
            if (!string.IsNullOrWhiteSpace(plan.SwitchName) && !host.Switches.Contains(plan.SwitchName, StringComparer.Ordinal)) errors.Add("The selected virtual switch no longer exists.");
        }
        return errors;
    }
    public static void RequireValid(CreatePlan plan, HostInfo? host = null)
    {
        var errors = Validate(plan, host);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
