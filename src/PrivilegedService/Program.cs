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
                if (result.Success && request.Kind is OperationKind.Create or OperationKind.Start or OperationKind.Shutdown)
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
            case OperationKind.Start:
            case OperationKind.Shutdown:
                if (request.VmId is null || request.VmId == Guid.Empty) throw new InvalidDataException("A VM identifier is required.");
                return Json.Read<OperationResult>(await runner.RunAsync("Lifecycle", request));
            case OperationKind.Create:
                var plan = request.Plan ?? throw new InvalidDataException("Missing creation plan.");
                var host = await new WindowsHostDiscovery(runner).DiscoverAsync();
                PlanValidator.RequireValid(plan, host, preparing: true);
                var root = SafePaths.NewOperationDirectory(plan.StorageRoot, plan.OperationId);
                var efi = SafePaths.RequireLocal(plan.EfiSource);
                if (!plan.BuildEfiFromArchive && !efi.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EFI must be a VHDX file.");
                if (plan.BuildEfiFromArchive && !plan.EfiSha256.Equals(UpstreamAssets.HyperVEfi.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only the pinned upstream EFI archive is accepted for automatic preparation.");
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
                    if (new FileInfo(plan.RecoveryDmg!).Length >= uint.MaxValue) throw new InvalidDataException("The recovery image exceeds the FAT32 per-file limit.");
                    sourceBytes = checked(sourceBytes + 2 * new FileInfo(plan.RecoveryDmg!).Length + 512L * 1024 * 1024);
                }
                if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < sourceBytes + (long)plan.DiskGiB * (long)ResourcePolicy.GiB)
                    throw new IOException("The storage volume needs room for the full virtual disk capacity and copied boot media.");
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
                        var files = Path.Combine(root, "BootFiles");
                        await BootMedia.ConfigureAsync(archive, files, plan.CpuProfile, identity);
                        var recoveryFolder = Path.Combine(files, "com.apple.recovery.boot"); Directory.CreateDirectory(recoveryFolder);
                        await CopyVerified(plan.RecoveryDmg!, Path.Combine(recoveryFolder, "BaseSystem.dmg"), plan.RecoveryDmgSha256!);
                        await CopyVerified(plan.RecoveryChunklist!, Path.Combine(recoveryFolder, "BaseSystem.chunklist"), plan.RecoveryChunklistSha256!);
                    }
                    else await CopyVerified(efi, Path.Combine(root, "EFI.vhdx"), plan.EfiSha256);
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
