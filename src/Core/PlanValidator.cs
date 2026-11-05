using System.Text.RegularExpressions;

namespace Pome.Core;

public static partial class PlanValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    public static bool IsValidName(string? name) => NamePattern().IsMatch(name ?? "");
    public static IReadOnlyList<string> Validate(CreatePlan plan, HostInfo? host = null, bool preparing = false)
    {
        var errors = new List<string>();
        if (plan.SchemaVersion != 1 || plan.OperationId == Guid.Empty) errors.Add("Unsupported plan version or operation identifier.");
        if (!IsValidName(plan.Name)) errors.Add("Use 1–64 letters, digits, spaces, dots, underscores or hyphens for the VM name.");
        if (plan.CpuCount is < 2 or > 64) errors.Add("Choose 2–64 virtual processors.");
        if (plan.MemoryGiB is < 4 or > 128) errors.Add("Choose 4–128 GB of static memory.");
        if (plan.DiskGiB is < 64 or > 2048) errors.Add("Choose 64–2048 GB of disk capacity.");
        var release = MacOsCatalog.Find(plan.MacOsVersion);
        if (release is null) errors.Add("Choose a macOS version from pome's catalog.");
        if (!plan.AcceptLicense) errors.Add("Review the applicable Apple software license before creating a guest.");
        if (!plan.AcceptGuestFirmwareChange) errors.Add("Consent is required to disable Secure Boot on the new guest.");
        if (string.IsNullOrWhiteSpace(plan.StorageRoot)) errors.Add("Choose a storage folder.");
        if (string.IsNullOrWhiteSpace(plan.EfiSource) || !HashPattern().IsMatch(plan.EfiSha256 ?? "")) errors.Add("Select an EFI VHDX and its trusted SHA-256 digest.");
        if (!string.IsNullOrWhiteSpace(plan.RecoverySource) && !HashPattern().IsMatch(plan.RecoverySha256 ?? "")) errors.Add("The recovery VHDX requires a trusted SHA-256 digest.");
        if (!Enum.IsDefined(plan.CpuProfile)) errors.Add("Unknown CPU profile.");
        if (plan.BuildEfiFromArchive && !string.IsNullOrWhiteSpace(plan.RecoverySource)) errors.Add("Automatic media builds its own recovery disk. Remove the custom recovery VHDX or choose manual media.");
        if (plan.BuildEfiFromArchive && (string.IsNullOrWhiteSpace(plan.RecoveryDmg) || string.IsNullOrWhiteSpace(plan.RecoveryChunklist)
            || !HashPattern().IsMatch(plan.RecoveryDmgSha256 ?? "") || !HashPattern().IsMatch(plan.RecoveryChunklistSha256 ?? "")))
            errors.Add("Automatic EFI creation requires verified recovery DMG and chunklist files.");
        if (plan.BuildEfiFromArchive && release is not null && GuestDriverPolicy.UsesUpdatedDriver(release)
            && (string.IsNullOrWhiteSpace(plan.HyperVDriverArchive) || !HashPattern().IsMatch(plan.HyperVDriverSha256 ?? "")))
            errors.Add($"{release.DisplayName} requires the verified MacHyperVSupport update archive.");
        if (plan.BuildEfiFromArchive && plan.CpuProfile == CpuProfile.AmdVanilla)
        {
            if (string.IsNullOrWhiteSpace(plan.AmdPatches) || !HashPattern().IsMatch(plan.AmdPatchesSha256 ?? ""))
                errors.Add("AMD media requires the verified AMD_Vanilla kernel patches.");
            if (release is not null && release.Darwin < AmdKernelPatches.MinimumDarwin) errors.Add("AMD processors need macOS 10.13 High Sierra or newer.");
            if (!ResourcePolicy.IsPowerOfTwo(plan.CpuCount)) errors.Add("AMD kernel patches need 2, 4, 8 or 16 virtual processors.");
        }
        if (host is not null)
        {
            // Downloading media consumes no guest RAM. Recheck live memory in the
            // privileged create/start path instead of blocking reusable preparation.
            var advice = ResourcePolicy.Recommend(preparing ? host with { AvailableMemoryBytes = host.TotalMemoryBytes } : host);
            if (!advice.CanCreate || plan.MemoryGiB > advice.MaximumMemoryGiB || plan.CpuCount > advice.MaximumCpuCount)
                errors.Add("The allocation exceeds the current safe host resource budget. Refresh host discovery.");
            if (host.HyperVAvailable != true) errors.Add(HostCompatibility.HyperVEnabled(host)
                ? "Hyper-V is enabled. Inspect with administrator access to load its networks before preparing the VM."
                : "Hyper-V management availability has not been confirmed. Inspect with administrator access.");
            if (plan.BuildEfiFromArchive && string.IsNullOrWhiteSpace(plan.SwitchName)) errors.Add("Apple recovery needs a connected virtual switch with internet access.");
            if (!string.IsNullOrWhiteSpace(plan.SwitchName) && !host.Switches.Contains(plan.SwitchName, StringComparer.Ordinal)) errors.Add("The selected virtual switch no longer exists.");
        }
        return errors;
    }
    public static void RequireValid(CreatePlan plan, HostInfo? host = null, bool preparing = false)
    {
        var errors = Validate(plan, host, preparing);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }
}
