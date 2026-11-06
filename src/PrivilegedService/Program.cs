using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pome.Core;
using Pome.Infrastructure;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent" || !int.TryParse(args[3], out var parent)
            || !System.Text.RegularExpressions.Regex.IsMatch(args[1], "^pome-[a-f0-9]{32}$")) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != parent) return 3;
            UserPipe.RequireCurrentUserOwner(pipe);
            var request = await PipeProtocol.ReadAsync<OperationRequest>(pipe, timeout.Token);
            OperationResult result;
            try
            {
                result = await Execute(request);
                if (result.Success && request.Kind is not (OperationKind.Inspect or OperationKind.EnableHyperV or OperationKind.GrantHyperVAccess))
                {
                    try { result = result with { VirtualMachines = (await new HyperVManager(new PowerShellRunner()).ListAsync()).ToArray() }; }
                    catch { /* The operation succeeded; a listing failure must not turn it into a retry. */ }
                }
            }
            catch (Exception e) { result = new(false, Redaction.Clean(e.Message)); }
            await PipeProtocol.WriteAsync(pipe, result, CancellationToken.None);
            return result.Success ? 0 : 1;
        }
        catch { return 4; }
    }

    private static async Task<OperationResult> Execute(OperationRequest request)
    {
        var runner = new PowerShellRunner();
        switch (request.Kind)
        {
            case OperationKind.Inspect:
                using (var inspectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    var inspection = await new WindowsHostDiscovery(runner).DiscoverAsync(inspectionTimeout.Token);
                    var vms = inspection.HyperVAvailable == true ? (await new HyperVManager(runner).ListAsync(inspectionTimeout.Token)).ToArray() : [];
                    return new(inspection.HyperVAvailable == true, inspection.HyperVAvailable == true ? "Administrator assessment complete. Networks and virtual machines are loaded." : "Administrator assessment could not access Hyper-V. Check the service state and Windows feature state.", Host: inspection, VirtualMachines: vms);
                }
            case OperationKind.EnableHyperV:
                return Json.Read<OperationResult>(await runner.RunAsync("EnableHyperV"));
            case OperationKind.GrantHyperVAccess:
                // The pipe owner check guarantees this helper runs as the requesting user; never take a SID from the request.
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return Json.Read<OperationResult>(await runner.RunAsync("GrantHyperVAccess", new { userSid = identity.User?.Value ?? throw new InvalidDataException("The signed-in user could not be identified.") }));
            case OperationKind.AddGpuPartition:
            case OperationKind.RemoveGpuPartition:
                {
                    var gpuVm = (await new HyperVManager(runner).ListAsync()).SingleOrDefault(x => x.Id == request.VmId)
                        ?? throw new InvalidDataException("The selected VM no longer exists.");
                    VmManagementPolicy.RequireAllowed(request, gpuVm);
                    var gpuDetails = await InspectWithGuestAsync(runner, gpuVm);
                    var gpuFolder = CreationPreflight.RequireOwnedOperationFolder(gpuVm, gpuDetails with { Checkpoints = [] });
                    if (request.Kind == OperationKind.AddGpuPartition) GpuPartitionPolicy.RequireCanAdd(gpuVm, gpuDetails, request.GpuInstancePath);
                    else GpuPartitionPolicy.RequireCanRemove(gpuVm, gpuDetails);
                    var changed = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new
                    {
                        kind = request.Kind.ToString(), vmId = gpuVm.Id, gpuInstancePath = request.GpuInstancePath, operationFolder = gpuFolder,
                        lowMmioBytes = GpuPartitionPolicy.LowMmioBytes, highMmioBytes = GpuPartitionPolicy.HighMmioBytes
                    }));
                    return changed.Details is null ? changed : changed with { Details = WithGuest(changed.Details, gpuVm) };
                }
            case OperationKind.EnableVirtualGpu:
            case OperationKind.DisableVirtualGpu:
                {
                    var vgVm = (await new HyperVManager(runner).ListAsync()).SingleOrDefault(x => x.Id == request.VmId)
                        ?? throw new InvalidDataException("The selected VM no longer exists.");
                    VmManagementPolicy.RequireAllowed(request, vgVm);
                    var vgDetails = await InspectWithGuestAsync(runner, vgVm);
                    var vgFolder = CreationPreflight.RequireOwnedOperationFolder(vgVm, vgDetails);
                    var enable = request.Kind == OperationKind.EnableVirtualGpu;
                    if (enable) VirtualGpuPolicy.RequireCanEnable(vgVm, vgDetails); else VirtualGpuPolicy.RequireCanDisable(vgVm, vgDetails);
                    // OpenCore must inject PVGTransport, so turning on needs the package beside pome.
                    var package = enable ? VirtualGpuHost.LocateGuestPackage()
                        ?? throw new InvalidDataException("The guest driver package (pvg-guest) is not installed next to pome. Build it with the macOS CI workflow first.") : null;
                    var working = Path.Combine(vgFolder, "config.plist.virtual-gpu");
                    var recordPath = Path.Combine(vgFolder, VirtualGpuPolicy.RecordFile);
                    Json.Read<OperationResult>(await runner.RunAsync("VirtualGpuMedia", new { vmId = vgVm.Id, mode = "read", configPath = working }));
                    try
                    {
                        System.Xml.Linq.XDocument config;
                        // A failed earlier attempt may have changed the EFI copy without writing the record.
                        // Enabling always starts from the original VirtualGpuMedia saved before its first write.
                        var original = Path.Combine(vgFolder, "config.plist.before-virtual-gpu");
                        if (enable && File.Exists(original)) File.Copy(original, working, true);
                        using (var input = File.OpenRead(working))
                        using (var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, MaxCharactersInDocument = 2_000_000 }))
                            config = System.Xml.Linq.XDocument.Load(reader);
                        VirtualGpuRecord? record = null;
                        if (enable) record = VirtualGpuPolicy.Enable(config);
                        else VirtualGpuPolicy.Disable(config, CreationPreflight.TryReadVirtualGpuRecord(vgVm, vgDetails) ?? throw new InvalidDataException("The virtual GPU record is missing; restore config.plist.before-virtual-gpu from the VM folder manually."));
                        using (var writer = System.Xml.XmlWriter.Create(working, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, NewLineChars = "\n" })) config.Save(writer);
                        // Register the socket service before the guest can ask for it; harmless to repeat.
                        if (enable) VirtualGpuService.Register();
                        var written = Json.Read<OperationResult>(await runner.RunAsync("VirtualGpuMedia", new
                        {
                            vmId = vgVm.Id, mode = "write", configPath = working,
                            packageDirectory = package, removePackage = !enable
                        }));
                        if (enable) await WriteAtomicAsync(recordPath, Json.Write(record!));
                        else { File.Delete(recordPath); File.Delete(Path.Combine(vgFolder, "config.plist.before-virtual-gpu")); }
                        var message = enable
                            ? "Virtual GPU turned on. The guest now boots with relaxed AMFI and SIP (csr-active-config 0x0A43), which the unsigned Metal layer needs. In macOS, run PomeGPU/install.sh from the POME-BOOT volume, then restart. " + written.Message
                            : "Virtual GPU turned off. The original boot arguments and SIP value were restored. " + written.Message;
                        var refreshed = await InspectWithGuestAsync(runner, vgVm);
                        return new(true, message, vgVm.Id, Details: refreshed);
                    }
                    finally { File.Delete(working); }
                }
            case OperationKind.DeleteVm:
                {
                    var target = (await new HyperVManager(runner).ListAsync()).SingleOrDefault(x => x.Id == request.VmId)
                        ?? throw new InvalidDataException("The selected VM no longer exists.");
                    VmManagementPolicy.RequireAllowed(request, target);
                    var inspected = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new OperationRequest(OperationKind.InspectVm, VmId: target.Id)));
                    var folder = CreationPreflight.RequireOwnedOperationFolder(target, inspected.Details ?? throw new InvalidDataException("VM details are unavailable."));
                    var removed = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new { kind = "DeleteVm", vmId = target.Id, operationFolder = folder }));
                    if (!request.DeleteFiles)
                        return removed with { Message = $"{target.Name} was removed from Hyper-V. Its files were kept in {folder}.", ResourceDirectory = folder };
                    try
                    {
                        // Directory.Delete removes links rather than following them; the folder was also checked for none.
                        Directory.Delete(folder, true);
                        return removed with { Message = $"{target.Name} and its files were deleted.", ResourceDirectory = null };
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return removed with { Message = $"{target.Name} was removed from Hyper-V, but some files could not be deleted: {Redaction.Clean(e.Message)} The remaining files are in {folder}.", ResourceDirectory = folder };
                    }
                }
            case OperationKind.Start:
            case OperationKind.Shutdown:
            case OperationKind.Pause:
            case OperationKind.Resume:
            case OperationKind.Save:
            case OperationKind.TurnOff:
            case OperationKind.Reset:
                if (request.VmId is null || request.VmId == Guid.Empty) throw new InvalidDataException("A VM identifier is required.");
                var lifecycleVm = (await new HyperVManager(runner).ListAsync()).SingleOrDefault(x => x.Id == request.VmId)
                    ?? throw new InvalidDataException("The selected VM no longer exists.");
                VmManagementPolicy.RequireAllowed(request, lifecycleVm);
                return Json.Read<OperationResult>(await runner.RunAsync("Lifecycle", request));
            case OperationKind.InspectVm:
            case OperationKind.Configure:
            case OperationKind.RepairBoot:
            case OperationKind.Checkpoint:
            case OperationKind.RestoreCheckpoint:
            case OperationKind.DeleteCheckpoint:
            case OperationKind.Export:
            case OperationKind.ResizeDisk:
            case OperationKind.ExportBootLogs:
                var selected = (await new HyperVManager(runner).ListAsync()).SingleOrDefault(x => x.Id == request.VmId)
                    ?? throw new InvalidDataException("The selected VM no longer exists.");
                var currentHost = request.Kind == OperationKind.Configure ? await new WindowsHostDiscovery(runner).DiscoverAsync() : null;
                VmManagementPolicy.RequireAllowed(request, selected, currentHost);
                if (request.Kind == OperationKind.Configure)
                    CreationPreflight.RequireUniqueName(request.Configuration!.Name, (await new HyperVManager(runner).ListAsync()).Where(x => x.Id != selected.Id));
                if (request.Kind == OperationKind.ResizeDisk)
                {
                    var inspectedDisk = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new OperationRequest(OperationKind.InspectVm, VmId: selected.Id)));
                    var systemDisk = CreationPreflight.RequireOwnedSystemDisk(selected, inspectedDisk.Details!);
                    var required = checked((long)request.DiskGiB!.Value * (long)ResourcePolicy.GiB - new FileInfo(systemDisk).Length);
                    if (new DriveInfo(Path.GetPathRoot(systemDisk)!).AvailableFreeSpace < required)
                        throw new IOException("The disk's drive needs room for the full expanded capacity. Free space before retrying.");
                    return Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new { kind = "ResizeDisk", vmId = selected.Id, diskGiB = request.DiskGiB, systemDisk }));
                }
                if (request.Kind == OperationKind.ExportBootLogs)
                {
                    var logsId = request.OperationId ?? Guid.NewGuid();
                    var logsRoot = SafePaths.NewOperationDirectory(request.Destination ?? "", logsId);
                    Directory.CreateDirectory(logsRoot);
                    await WriteMarkerAsync(logsRoot, logsId, "CollectingBootLogs", "Reading the stopped guest EFI disk without changing it.", false, selected.Id);
                    try
                    {
                        var logs = Json.Read<OperationResult>(await runner.RunAsync("CollectBootLogs", new { vmId = selected.Id, outputDirectory = logsRoot }));
                        await WriteMarkerAsync(logsRoot, logsId, "Exported", logs.Message, true, selected.Id);
                        return logs;
                    }
                    catch (Exception e)
                    {
                        await WriteMarkerAsync(logsRoot, logsId, "NeedsAttention", e.Message, false, selected.Id);
                        return new(false, "Boot log export failed. Partial files were preserved. " + Redaction.Clean(e.Message), selected.Id, logsRoot, Stage: "NeedsAttention");
                    }
                }
                if (request.Kind == OperationKind.InspectVm)
                {
                    var inspectedVm = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", request));
                    return inspectedVm.Details is null ? inspectedVm : inspectedVm with { Details = WithGuest(inspectedVm.Details, selected) };
                }
                if (request.Kind != OperationKind.Export)
                    return Json.Read<OperationResult>(await runner.RunAsync("ManageVm", request));
                var exportId = request.OperationId ?? Guid.NewGuid();
                var exportRoot = SafePaths.NewOperationDirectory(request.Destination ?? "", exportId);
                // Use allocated disk sizes for the preflight; Hyper-V may need additional
                // temporary space, so a later export failure preserves partial files.
                var detailResult = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new OperationRequest(OperationKind.InspectVm, VmId: selected.Id)));
                var diskBytes = detailResult.Details!.Disks.Aggregate(0L, (sum, disk) => checked(sum + new FileInfo(SafePaths.RequireLocal(disk.Path)).Length));
                if (new DriveInfo(Path.GetPathRoot(exportRoot)!).AvailableFreeSpace < checked(diskBytes + (long)ResourcePolicy.GiB))
                    throw new IOException("The backup drive needs space for the VM disks plus at least 1 GB of overhead.");
                Directory.CreateDirectory(exportRoot);
                await WriteMarkerAsync(exportRoot, exportId, "Exporting", "Exporting VM configuration and disks.", false, selected.Id);
                try
                {
                    var exported = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new { kind = "Export", vmId = selected.Id, exportDirectory = exportRoot }));
                    await WriteMarkerAsync(exportRoot, exportId, "Exported", exported.Message, true, selected.Id);
                    return exported with { Stage = "Exported", ResourceDirectory = exportRoot };
                }
                catch (Exception e)
                {
                    await WriteMarkerAsync(exportRoot, exportId, "NeedsAttention", e.Message, false, selected.Id);
                    return new(false, "Export failed. Partial files were preserved. " + Redaction.Clean(e.Message), selected.Id, exportRoot, Stage: "NeedsAttention");
                }
            case OperationKind.Create:
                var plan = request.Plan ?? throw new InvalidDataException("Missing creation plan.");
                var host = await new WindowsHostDiscovery(runner).DiscoverAsync();
                PlanValidator.RequireValid(plan, host, preparing: true);
                CreationPreflight.RequireUniqueName(plan.Name, await new HyperVManager(runner).ListAsync());
                var root = SafePaths.NewOperationDirectory(plan.StorageRoot, plan.OperationId);
                var efi = SafePaths.RequireLocal(plan.EfiSource);
                if (!plan.BuildEfiFromArchive && !efi.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EFI must be a VHDX file.");
                if (plan.BuildEfiFromArchive && !plan.EfiSha256.Equals(UpstreamAssets.HyperVEfi.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only the pinned upstream EFI archive is accepted for automatic preparation.");
                var release = MacOsCatalog.Require(plan.MacOsVersion);
                var needsDriver = plan.BuildEfiFromArchive && GuestDriverPolicy.UsesUpdatedDriver(release);
                if (needsDriver && !string.Equals(plan.HyperVDriverSha256, UpstreamAssets.MacHyperVSupport.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only the pinned MacHyperVSupport release is accepted for automatic preparation.");
                var sourceBytes = new FileInfo(efi).Length;
                string? recovery = null;
                if (!string.IsNullOrWhiteSpace(plan.RecoverySource))
                {
                    recovery = SafePaths.RequireLocal(plan.RecoverySource);
                    if (!recovery.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Recovery must be a VHDX file.");
                    sourceBytes = checked(sourceBytes + new FileInfo(recovery).Length);
                }
                if (plan.BuildEfiFromArchive)
                {
                    SafePaths.RequireLocal(plan.RecoveryDmg!); SafePaths.RequireLocal(plan.RecoveryChunklist!);
                    sourceBytes = checked(sourceBytes + 2 * new FileInfo(plan.RecoveryDmg!).Length
                        + 2 * RecoveryDisk.GetVirtualSize(plan.RecoveryDmg!) + 512L * 1024 * 1024);
                    if (needsDriver) sourceBytes = checked(sourceBytes + 2 * new FileInfo(SafePaths.RequireLocal(plan.HyperVDriverArchive!)).Length);
                    if (plan.CpuProfile == CpuProfile.AmdVanilla && !string.Equals(plan.AmdPatchesSha256, UpstreamAssets.AmdVanillaPatches.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Only the pinned AMD_Vanilla patches are accepted for automatic preparation.");
                }
                CreationPreflight.RequireStorage(plan, sourceBytes);
                Directory.CreateDirectory(root);
                await WriteMarkerAsync(root, plan.OperationId, "CopyingMedia", "Validating and copying verified boot media into the new operation directory.", false, null);
                try
                {
                    if (plan.BuildEfiFromArchive)
                    {
                        var archive = Path.Combine(root, "upstream.zip");
                        await CopyVerified(efi, archive, UpstreamAssets.HyperVEfi.Sha256);
                        var identity = MachineIdentity.Create();
                        await WriteAtomicAsync(Path.Combine(root, "identity.json"), Json.Write(identity));
                        await WriteAtomicAsync(Path.Combine(root, "guest.json"), Json.Write(new GuestRecord(1, release.Version, plan.CpuProfile, DateTimeOffset.UtcNow)));
                        var files = Path.Combine(root, "BootFiles");
                        string? driver = null;
                        if (needsDriver)
                        {
                            driver = Path.Combine(root, "MacHyperVSupport.zip");
                            await CopyVerified(SafePaths.RequireLocal(plan.HyperVDriverArchive!), driver, UpstreamAssets.MacHyperVSupport.Sha256);
                        }
                        string? amdPatches = null;
                        if (plan.CpuProfile == CpuProfile.AmdVanilla)
                        {
                            amdPatches = Path.Combine(root, "AMD_Vanilla-patches.plist");
                            await CopyVerified(SafePaths.RequireLocal(plan.AmdPatches!), amdPatches, UpstreamAssets.AmdVanillaPatches.Sha256);
                        }
                        await BootMedia.ConfigureAsync(archive, files, plan.CpuProfile, identity, default, release, driver, amdPatches, plan.CpuCount);
                        var recoveryFolder = Path.Combine(root, "RecoverySource"); Directory.CreateDirectory(recoveryFolder);
                        await CopyVerified(plan.RecoveryDmg!, Path.Combine(recoveryFolder, "BaseSystem.dmg"), plan.RecoveryDmgSha256!);
                        await CopyVerified(plan.RecoveryChunklist!, Path.Combine(recoveryFolder, "BaseSystem.chunklist"), plan.RecoveryChunklistSha256!);
                        await WriteMarkerAsync(root, plan.OperationId, "BuildingRecovery", "Restoring verified Apple recovery to a separate disk so it does not occupy guest boot memory.", false, null);
                        await RecoveryDisk.RestoreAsync(Path.Combine(recoveryFolder, "BaseSystem.dmg"), Path.Combine(root, "Recovery.vhd"));
                    }
                    else
                    {
                        await CopyVerified(efi, Path.Combine(root, "EFI.vhdx"), plan.EfiSha256);
                        await WriteAtomicAsync(Path.Combine(root, "guest.json"), Json.Write(new GuestRecord(1, release.Version, plan.CpuProfile, DateTimeOffset.UtcNow)));
                    }
                    if (recovery is not null) await CopyVerified(recovery, Path.Combine(root, "Recovery.vhdx"), plan.RecoverySha256!);
                }
                catch (Exception e)
                {
                    var detail = Redaction.Clean(e.Message) + " Files were preserved; inspect operation.json before retrying.";
                    await WriteMarkerAsync(root, plan.OperationId, "NeedsAttention", detail, false, null);
                    return new(false, detail, ResourceDirectory: root, Stage: "NeedsAttention");
                }
                return Json.Read<OperationResult>(await runner.RunAsync("CreateVm", new { plan, resourceDirectory = root }));
            default: throw new InvalidDataException("Operation is not allowed.");
        }
    }

    private static VmDetails WithGuest(VmDetails details, VmInfo vm) =>
        details with { GuestMacOsVersion = CreationPreflight.TryReadGuestRecord(vm, details)?.MacOsVersion,
                       VirtualGpu = CreationPreflight.TryReadVirtualGpuRecord(vm, details) is not null };

    private static async Task<VmDetails> InspectWithGuestAsync(PowerShellRunner runner, VmInfo vm)
    {
        var inspected = Json.Read<OperationResult>(await runner.RunAsync("ManageVm", new OperationRequest(OperationKind.InspectVm, VmId: vm.Id)));
        return WithGuest(inspected.Details ?? throw new InvalidDataException("VM details are unavailable."), vm);
    }

    private static async Task WriteMarkerAsync(string root, Guid operationId, string stage, string detail, bool terminal, Guid? vmId)
    {
        var record = new OperationMarker(operationId, stage, Redaction.Clean(detail), terminal, vmId, DateTimeOffset.UtcNow);
        await WriteAtomicAsync(Path.Combine(root, "operation.json"), Json.Write(record));
    }

    private static async Task WriteAtomicAsync(string path, string content)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static async Task CopyVerified(string source, string target, string hash)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        { await input.CopyToAsync(output); await output.FlushAsync(); output.Flush(true); }
        await SafePaths.VerifyHashAsync(target, hash);
    }
}
