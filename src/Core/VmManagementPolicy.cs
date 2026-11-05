namespace Pome.Core;

public static class VmManagementPolicy
{
    public static void RequireAllowed(OperationRequest request, VmInfo vm, HostInfo? host = null)
    {
        if (request.VmId != vm.Id || vm.Id == Guid.Empty) throw new InvalidDataException("The selected VM could not be identified.");
        if (request.Kind == OperationKind.InspectVm) return;
        if (!vm.Managed) throw new InvalidDataException("VMs created outside pome are read-only.");
        var allowed = request.Kind switch
        {
            OperationKind.Start => vm.CanStart,
            OperationKind.Shutdown => vm.CanShutdown,
            OperationKind.Pause => vm.CanPause,
            OperationKind.Resume => vm.CanResume,
            OperationKind.Save => vm.CanSave,
            OperationKind.TurnOff => vm.CanTurnOff,
            OperationKind.Reset => vm.CanReset,
            OperationKind.DeleteVm => vm.CanDelete,
            OperationKind.AddGpuPartition or OperationKind.RemoveGpuPartition => vm.CanConfigure,
            OperationKind.Configure or OperationKind.Checkpoint or OperationKind.RestoreCheckpoint or
                OperationKind.DeleteCheckpoint or OperationKind.RepairBoot or OperationKind.Export or OperationKind.ResizeDisk or OperationKind.ExportBootLogs => vm.CanConfigure,
            _ => false
        };
        if (!allowed) throw new InvalidDataException("This action is unavailable in the VM's current state. Refresh; shut down the guest before changing settings, checkpoints or backups.");
        if (request.Kind is OperationKind.RestoreCheckpoint or OperationKind.DeleteCheckpoint && (request.CheckpointId is null || request.CheckpointId == Guid.Empty))
            throw new InvalidDataException("Select a checkpoint belonging to this VM.");
        if (request.Kind == OperationKind.ResizeDisk && (request.DiskGiB is null or < 64 or > 2048))
            throw new InvalidDataException("Choose a new disk capacity between 64 and 2048 GB. Shrinking is unavailable.");
        if (request.Kind == OperationKind.Configure)
        {
            var settings = request.Configuration ?? throw new InvalidDataException("Missing VM settings.");
            if (!PlanValidator.IsValidName(settings.Name)) throw new InvalidDataException("Use 1–64 letters, digits, spaces, dots, underscores or hyphens for the VM name.");
            if (settings.CpuCount is < 2 or > 64 || settings.MemoryGiB is < 4 or > 128) throw new InvalidDataException("Choose 2–64 processors and 4–128 GB of static memory.");
            if (host is null) throw new InvalidDataException("Refresh host discovery before applying settings.");
            var budget = ResourcePolicy.Recommend(host with { AvailableMemoryBytes = host.TotalMemoryBytes });
            if (settings.CpuCount > budget.MaximumCpuCount || settings.MemoryGiB > budget.MaximumMemoryGiB)
                throw new InvalidDataException("The settings exceed this computer's resource budget.");
            // AMD_Vanilla's core-count patch is written into the EFI at creation and must match the VM.
            if (host.CpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) && settings.CpuCount != vm.ProcessorCount)
                throw new InvalidDataException("On AMD hosts the boot media encodes the processor count. Keep it unchanged, or create a new VM with the count you need.");
            if (settings.SwitchName is not null && !host.Switches.Contains(settings.SwitchName, StringComparer.Ordinal))
                throw new InvalidDataException("The selected virtual switch no longer exists.");
        }
    }
}
