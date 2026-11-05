using Pome.Core;

namespace Pome.Infrastructure;

public static class CreationPreflight
{
    public static void RequireUniqueName(string name, IEnumerable<VmInfo> machines)
    {
        if (machines.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A VM with this name already exists. Choose another name before preparing media.");
    }

    public static long RequiredStorageBytes(int diskGiB, long mediaBytes) => checked((long)diskGiB * (long)ResourcePolicy.GiB + mediaBytes);

    public static void RequireStorage(CreatePlan plan, long mediaBytes)
    {
        var directory = SafePaths.NewOperationDirectory(plan.StorageRoot, plan.OperationId);
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < RequiredStorageBytes(plan.DiskGiB, mediaBytes))
            throw new IOException("The storage drive needs room for the full virtual disk capacity and boot media. Choose another folder or reduce the disk capacity.");
    }

    public static void RequireMedia(CreatePlan plan)
    {
        RequireVhdx(plan.EfiSource);
        if (!string.IsNullOrWhiteSpace(plan.RecoverySource)) RequireVhdx(plan.RecoverySource);
    }

    public static string RequireOwnedSystemDisk(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id || !Guid.TryParse(vm.Notes["pome:v1:".Length..], out var operation))
            throw new InvalidDataException("The original pome disk could not be identified.");
        var disk = details.Disks.SingleOrDefault(x => x.Controller == "SCSI 0:1")
            ?? throw new InvalidDataException("The original macOS disk is missing from SCSI 0:1.");
        var path = SafePaths.RequireLocal(disk.Path);
        if (!string.Equals(Path.GetFileName(path), "macOS.vhdx", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "pome-" + operation.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Disk growth is limited to the original macOS.vhdx in its pome operation directory. Remove checkpoints before retrying.");
        return path;
    }

    /// <summary>
    /// The pome-owned folder holding this VM's disks and configuration. Every attached disk and the
    /// configuration must lie inside it, its journal must name the same operation, and no reparse
    /// point may appear anywhere below it, so removing it cannot reach unrelated files.
    /// </summary>
    public static string RequireOwnedOperationFolder(VmInfo vm, VmDetails details)
    {
        if (!vm.Managed || details.VmId != vm.Id || !Guid.TryParse(vm.Notes["pome:v1:".Length..], out var operation))
            throw new InvalidDataException("The pome folder for this VM could not be identified.");
        if (details.Checkpoints.Length != 0) throw new InvalidDataException("Delete this VM's checkpoints before removing it.");
        if (details.Disks.Length == 0) throw new InvalidDataException("This VM has no disks pome can attribute to its folder.");
        var folder = Path.GetDirectoryName(SafePaths.RequireLocal(details.Disks[0].Path))
            ?? throw new InvalidDataException("The pome folder for this VM could not be identified.");
        folder = SafePaths.RequireLocal(folder);
        if (!string.Equals(Path.GetFileName(folder), "pome-" + operation.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This VM's disks are not in its original pome folder. Remove it in Hyper-V Manager instead.");
        foreach (var disk in details.Disks)
            if (!string.Equals(Path.GetDirectoryName(SafePaths.RequireLocal(disk.Path)), folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A disk attached to this VM is outside its pome folder. Detach it first, or remove the VM in Hyper-V Manager.");
        if (!SafePaths.IsWithin(Path.GetFullPath(details.ConfigurationLocation), folder))
            throw new InvalidDataException("This VM's configuration is outside its pome folder.");
        var marker = Path.Combine(folder, "operation.json");
        if (!File.Exists(marker) || Json.Read<OperationMarker>(File.ReadAllText(marker)).OperationId != operation)
            throw new InvalidDataException("The pome folder's journal does not match this VM.");
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The pome folder contains a link or junction; refusing to delete it.");
        return folder;
    }

    private static void RequireVhdx(string path)
    {
        var local = SafePaths.RequireLocal(path);
        if (!File.Exists(local) || !local.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an existing local VHDX file for boot media.");
    }
}
