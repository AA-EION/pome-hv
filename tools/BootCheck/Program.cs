using Pome.Core;
using Pome.Infrastructure;

// Developer boot-validation runner. Uses the same policies, media pipeline and
// constrained elevated helper as the UI. Never accepts a PowerShell command.
try
{
    if (args.Length < 3) throw new ArgumentException("Usage: Pome.BootCheck <inspect|prepare|create|start> <evidence-directory> <helper.exe> [--accept-license | --accept-guest-security | VM-ID]");
    var output = SafePaths.RequireLocal(args[1], false); Directory.CreateDirectory(output);
    var broker = new PrivilegeBroker(SafePaths.RequireLocal(args[2]));
    async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), Json.Write(value));
    switch (args[0])
    {
        case "inspect":
        {
            Console.WriteLine("Windows administrator approval is needed for this read-only inspection.");
            var result = await broker.ExecuteAsync(new(OperationKind.Inspect));
            await Save("inspection.json", result);
            Console.WriteLine(result.Message);
            if (result.Host is { } host) Console.WriteLine($"Hyper-V service: {host.HyperV?.ServiceState}; management: {host.HyperVAvailable}; available RAM: {host.AvailableMemoryBytes / ResourcePolicy.GiB} GB; switches: {string.Join(", ", host.Switches)}");
            return result.Success ? 0 : 1;
        }
        case "prepare":
        {
            if (!args.Contains("--accept-license")) throw new ArgumentException("Explicit --accept-license acknowledgment is required.");
            var host = Json.Read<OperationResult>(await File.ReadAllTextAsync(Path.Combine(output, "inspection.json"))).Host ?? throw new InvalidDataException("Run inspect first.");
            var advice = ResourcePolicy.Recommend(host);
            var network = host.Switches.FirstOrDefault(x => x == "Default Switch") ?? throw new InvalidDataException("No Default Switch found. Select a reviewed existing network in the UI.");
            var preliminary = new CreatePlan(1, Guid.NewGuid(), "pome-boot-validation", output, Math.Min(4, advice.CpuCount), 8, 64, network,
                "pending", UpstreamAssets.HyperVEfi.Sha256, null, null, "15", true, true);
            PlanValidator.RequireValid(preliminary, host, preparing: true);
            var cache = Path.Combine(output, "media");
            var progress = new Progress<string>(Console.WriteLine);
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var media = await BootMedia.PrepareAsync(cache, host.CpuVendor, PythonLocator.Find(), cancellation.Token, progress);
            var validation = Path.Combine(output, "validated-efi-" + Guid.NewGuid().ToString("N"));
            await BootMedia.ConfigureAsync(media.Archive, validation, media.CpuProfile, MachineIdentity.Create(), cancellation.Token);
            await BootMedia.ValidateAsync(validation, cancellation.Token);
            var plan = preliminary with { AcceptGuestFirmwareChange = false, EfiSource = media.Archive, BuildEfiFromArchive = true,
                CpuProfile = media.CpuProfile, RecoveryDmg = media.RecoveryDmg, RecoveryDmgSha256 = media.DmgHash,
                RecoveryChunklist = media.Chunklist, RecoveryChunklistSha256 = media.ChunklistHash };
            await Save("plan.json", plan);
            Console.WriteLine($"Prepared and validated: {plan.Name}, {plan.CpuCount} vCPUs, {plan.MemoryGiB} GB RAM, {plan.DiskGiB} GB disk, {network}. Guest security consent remains required; no VM created.");
            return 0;
        }
        case "create":
        {
            if (!args.Contains("--accept-guest-security")) throw new ArgumentException("Explicit --accept-guest-security is required: new guest Secure Boot off and upstream SIP 0x03 with injected unsigned kexts. Host security is unchanged.");
            var plan = Json.Read<CreatePlan>(await File.ReadAllTextAsync(Path.Combine(output, "plan.json"))) with { AcceptGuestFirmwareChange = true };
            var result = await broker.ExecuteAsync(new(OperationKind.Create, plan));
            await Save("creation.json", result); Console.WriteLine(Json.Write(result));
            return result.Success ? 0 : 1;
        }
        case "start":
        {
            if (args.Length != 4 || !Guid.TryParse(args[3], out var id)) throw new ArgumentException("An exact managed VM ID is required.");
            var result = await broker.ExecuteAsync(new(OperationKind.Start, VmId: id));
            await Save("start.json", result); Console.WriteLine(Json.Write(result));
            Console.WriteLine("Running is not proof of macOS boot. Observe the console and record the actual guest stage.");
            return result.Success ? 0 : 1;
        }
        default: throw new ArgumentException("Unsupported action.");
    }
}
catch (Exception e) { Console.Error.WriteLine(Redaction.Clean(e.Message)); return 1; }
