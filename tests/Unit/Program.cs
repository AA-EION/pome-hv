using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.IO.Compression;
using Pome.Core;
using Pome.Infrastructure;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Check(bool condition, string detail = "Assertion failed") { if (!condition) throw new Exception(detail); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
HostInfo Host(ulong total = 16, ulong free = 12, bool server = false) => new("Test", "Fixture", "fixture", "Windows 11 Pro", "Professional", 26100, server, "Fixture CPU", "GenuineIntel", 8, 16, total * ResourcePolicy.GiB, free * ResourcePolicy.GiB, true, true, true, true, ["Default Switch"], [], []);
CreatePlan Plan() => new(1, Guid.NewGuid(), "Test VM", Path.GetTempPath(), 4, 8, 100, "Default Switch", "C:\\media\\EFI.vhdx", new('a', 64), null, null, "15", true, true);
var temp = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);

AsyncTest("Recovery restoration preserves every decoded sector and emits a valid fixed VHD footer", async () => {
    foreach (var kind in new[] { 1u, 0x80000005u }) {
        var source = Path.Combine(temp, $"recovery-{kind}.dmg"); var output = source + ".vhd";
        var raw = RecoveryFixtures.Write(source, compression: kind);
        Check(RecoveryDisk.GetVirtualSize(source) == raw.Length);
        await RecoveryDisk.RestoreAsync(source, output);
        var restored = await File.ReadAllBytesAsync(output);
        Check(restored.Length == raw.Length + 512 && restored.AsSpan(0, raw.Length).SequenceEqual(raw));
        var footer = restored.AsSpan(raw.Length).ToArray();
        Check(Encoding.ASCII.GetString(footer, 0, 8) == "conectix");
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48)) == (ulong)raw.Length);
        Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60)) == 2);
        var expected = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(64)); footer.AsSpan(64, 4).Clear();
        Check(expected == ~footer.Aggregate(0u, (sum, b) => sum + b));
        await Reject<IOException>(() => RecoveryDisk.RestoreAsync(source, output));
        Check((await File.ReadAllBytesAsync(output)).AsSpan().SequenceEqual(restored));
    }
});
AsyncTest("Recovery restoration accepts Apple Partition Map images with 512- and 2048-byte blocks", async () => {
    foreach (var block in new[] { 512, 2048 }) {
        var source = Path.Combine(temp, $"apm-{block}.dmg"); var output = source + ".vhd";
        var raw = RecoveryFixtures.Write(source, apmBlock: block);
        await RecoveryDisk.RestoreAsync(source, output);
        Check((await File.ReadAllBytesAsync(output)).AsSpan(0, raw.Length).SequenceEqual(raw), $"APM {block} sectors changed");
    }
    var broken = Path.Combine(temp, "apm-bad.dmg"); RecoveryFixtures.Write(broken, apmBlock: 2048, badApm: true);
    await Reject<InvalidDataException>(() => RecoveryDisk.RestoreAsync(broken, broken + ".vhd"));
});
AsyncTest("Recovery restoration rejects corrupt GPT, unsupported compression and incomplete sector maps", async () => {
    foreach (var variant in new[] { "gpt", "gap", "codec" }) {
        var source = Path.Combine(temp, variant + ".dmg");
        RecoveryFixtures.Write(source, badGpt: variant == "gpt", gap: variant == "gap", compression: variant == "codec" ? 0x80000006u : 0x80000005u);
        await Reject<InvalidDataException>(() => RecoveryDisk.RestoreAsync(source, source + ".vhd"));
        Check(!File.Exists(source + ".vhd"));
    }
});
AsyncTest("Recovery cancellation cannot publish a disk or overwrite a prior partial", async () => {
    var source = Path.Combine(temp, "cancel-recovery.dmg"); RecoveryFixtures.Write(source);
    var output = source + ".vhd";
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Reject<OperationCanceledException>(() => RecoveryDisk.RestoreAsync(source, output, cancel.Token));
    Check(!File.Exists(output));
    await Reject<IOException>(() => RecoveryDisk.RestoreAsync(source, output));
});
Test("Automatic plans cannot ambiguously attach both restored and custom recovery media", () => {
    var plan = Plan() with { BuildEfiFromArchive = true, RecoverySource = "C:\\extra.vhdx", RecoverySha256 = new('a', 64) };
    Check(PlanValidator.Validate(plan).Any(x => x.Contains("own recovery disk")));
});

VmInfo Managed(string state = "Off") => new(Guid.NewGuid(), "Sequoia", state, 4, 0, 0, "pome:v1:" + Guid.NewGuid());
Test("Discovery preserves edits and a deliberately disconnected network", () => {
    var draft = new CreationDraft(); draft.ApplyHost(Host());
    draft.CpuCount = 3; draft.MemoryGiB = 5; draft.Network = "Disconnected";
    draft.ApplyHost(Host(free: 6));
    Check(draft.CpuCount == 3 && draft.MemoryGiB == 5 && draft.Network == "Disconnected");
});
Test("Untouched defaults follow host evidence and recommendations are explicit", () => {
    var draft = new CreationDraft(); draft.ApplyHost(Host());
    Check(draft.Network == "Default Switch" && draft.MemoryGiB == 8);
    draft.MemoryGiB = 4; draft.ApplyHost(Host(free: 7), useRecommendations: true);
    Check(draft.MemoryGiB == 5);
});
Test("Default names avoid case insensitive collisions without renaming edited drafts", () => {
    var draft = new CreationDraft();
    draft.ApplyMachines([Managed() with { Name = "sequoia" }, Managed() with { Name = "Sequoia 2" }]);
    Check(draft.Name == "Sequoia 3");
    draft.Name = "My Mac"; draft.ApplyMachines([Managed() with { Name = "My Mac" }]);
    Check(draft.Name == "My Mac");
});
Test("Blank and fractional number box values cannot become disk allocations", () => {
    foreach (var value in new[] { double.NaN, double.PositiveInfinity, 4.5, -1, (double)int.MaxValue + 1 })
        Throws<InvalidDataException>(() => CreationDraft.WholeNumber(value, "disk"));
    Check(CreationDraft.WholeNumber(100, "disk") == 100);
});
Test("Name preflight treats wildcard characters literally and rejects duplicate case", () => {
    Throws<InvalidDataException>(() => CreationPreflight.RequireUniqueName("SEQUOIA", [Managed()]));
    CreationPreflight.RequireUniqueName("Other", [Managed()]);
});
Test("Storage estimate reserves full disk capacity and checks arithmetic overflow", () => {
    Check(CreationPreflight.RequiredStorageBytes(100, 8L * (long)ResourcePolicy.GiB) == 108L * (long)ResourcePolicy.GiB);
    Throws<OverflowException>(() => CreationPreflight.RequiredStorageBytes(100, long.MaxValue));
});
Test("Manual preflight rejects non-VHDX inputs and missing files", () => {
    var text = Path.Combine(temp, "not-a-disk.txt"); File.WriteAllText(text, "fixture");
    Throws<InvalidDataException>(() => CreationPreflight.RequireMedia(Plan() with { EfiSource = text }));
    Throws<FileNotFoundException>(() => CreationPreflight.RequireMedia(Plan() with { EfiSource = Path.Combine(temp, "absent.vhdx") }));
});
Test("External VMs remain read-only across every management action", () => {
    var vm = Managed() with { Notes = "external" };
    VmManagementPolicy.RequireAllowed(new(OperationKind.InspectVm, VmId: vm.Id), vm);
    foreach (var kind in new[] { OperationKind.Start, OperationKind.Configure, OperationKind.Checkpoint, OperationKind.RestoreCheckpoint, OperationKind.DeleteCheckpoint, OperationKind.RepairBoot, OperationKind.Export, OperationKind.ResizeDisk, OperationKind.ExportBootLogs, OperationKind.Pause, OperationKind.Resume, OperationKind.Save, OperationKind.Shutdown })
        Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(kind, VmId: vm.Id), vm, Host()));
});
Test("Settings, checkpoints and export cannot mutate running, paused or saved VMs", () => {
    foreach (var state in new[] { "Running", "Paused", "Saved", "Starting", "Stopping" }) {
        var vm = Managed(state);
        foreach (var kind in new[] { OperationKind.Configure, OperationKind.Checkpoint, OperationKind.RestoreCheckpoint, OperationKind.DeleteCheckpoint, OperationKind.RepairBoot, OperationKind.Export, OperationKind.ResizeDisk, OperationKind.ExportBootLogs })
            Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(kind, VmId: vm.Id), vm, Host()));
    }
});
Test("Saved VMs may start; paused VMs resume without a second start", () => {
    var saved = Managed("Saved"); VmManagementPolicy.RequireAllowed(new(OperationKind.Start, VmId: saved.Id), saved);
    var paused = Managed("Paused"); VmManagementPolicy.RequireAllowed(new(OperationKind.Resume, VmId: paused.Id), paused);
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.Start, VmId: paused.Id), paused));
    var running = Managed("Running"); VmManagementPolicy.RequireAllowed(new(OperationKind.Save, VmId: running.Id), running);
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.Resume, VmId: running.Id), running));
});
Test("Configuration validates host budget, switch, name and VM identity", () => {
    var vm = Managed(); var settings = new VmConfiguration("My Mac", 4, 8, "Default Switch");
    var request = new OperationRequest(OperationKind.Configure, VmId: vm.Id, Configuration: settings);
    VmManagementPolicy.RequireAllowed(request, vm, Host(free: 4)); // powered off; start rechecks live RAM
    foreach (var bad in new[] { settings with { Name = "x'; Stop-VM *" }, settings with { CpuCount = 64 }, settings with { MemoryGiB = 128 }, settings with { SwitchName = "Gone" } })
        Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(request with { Configuration = bad }, vm, Host()));
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(request with { VmId = Guid.NewGuid() }, vm, Host()));
});
Test("Checkpoint restore requires an explicit checkpoint identifier", () => {
    var vm = Managed();
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.RestoreCheckpoint, VmId: vm.Id), vm));
    VmManagementPolicy.RequireAllowed(new(OperationKind.RestoreCheckpoint, VmId: vm.Id, CheckpointId: Guid.NewGuid()), vm);
});
Test("Management settings and details survive bounded IPC JSON", () => {
    var vm = Managed(); var config = new VmConfiguration("Mac", 4, 8, null);
    var request = new OperationRequest(OperationKind.Configure, VmId: vm.Id, Configuration: config);
    Check(Json.Read<OperationRequest>(Json.Write(request)).Configuration == config);
    var details = new VmDetails(vm.Id, vm.Name, 4, 8 * ResourcePolicy.GiB, null, "C:\\VMs", "Off", [], [new(Guid.NewGuid(), "Before install", DateTime.Now)], ["EFI"]);
    var result = Json.Read<OperationResult>(Json.Write(new OperationResult(true, "Loaded", Details: details)));
    Check(result.Details?.Checkpoints.Single().Name == "Before install" && result.Details.BootOrder.Single() == "EFI");
});
Test("Disk growth is limited to the original pome operation disk", () => {
    var operation = Guid.NewGuid(); var vm = Managed() with { Notes = "pome:v1:" + operation };
    var folder = Path.Combine(temp, "pome-" + operation.ToString("N")); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "macOS.vhdx"); File.WriteAllText(path, "fixture");
    var details = new VmDetails(vm.Id, vm.Name, 4, 8 * ResourcePolicy.GiB, null, folder, "Off", [new(path, 100 * ResourcePolicy.GiB, "SCSI 0:1")], [], []);
    Check(CreationPreflight.RequireOwnedSystemDisk(vm, details) == path);
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedSystemDisk(vm with { Notes = "pome:v1:" + Guid.NewGuid() }, details));
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedSystemDisk(vm, details with { Disks = [new(path, 100, "SCSI 0:0")] }));
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedSystemDisk(vm, details with { VmId = Guid.NewGuid() }));
});
Test("Disk growth rejects absent, small and excessive capacities", () => {
    var vm = Managed();
    foreach (var size in new int?[] { null, 0, 63, 2049 })
        Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.ResizeDisk, VmId: vm.Id, DiskGiB: size), vm));
    VmManagementPolicy.RequireAllowed(new(OperationKind.ResizeDisk, VmId: vm.Id, DiskGiB: 200), vm);
});

Test("Running VMMS confirms enabled despite management access denial", () => {
    var host = Host() with { HyperVAvailable = false, HyperV = new(null, true, "Running", "Access denied") };
    Check(HostCompatibility.HyperVEnabled(host));
    Check(HostCompatibility.HyperVCapability(host).Status == CapabilityStatus.Supported);
    Check(PlanValidator.Validate(Plan(), host).Any(x => x.Contains("administrator access")));
});
Test("VBS hypervisor alone does not prove Hyper-V feature enabled", () => {
    var host = Host() with { HyperVAvailable = false, HyperV = new(null, false, null, "Not installed") };
    Check(!HostCompatibility.HyperVEnabled(host));
    Check(HostCompatibility.HyperVCapability(host).Status == CapabilityStatus.Unknown);
});
Test("Explicit disabled feature is distinguished from access failure", () => Check(HostCompatibility.HyperVCapability(Host() with { HyperVAvailable = false, HyperV = new(false, true, "Stopped", null) }).Status == CapabilityStatus.Unsupported));
Test("Feature enabled but service stopped remains enabled", () => Check(HostCompatibility.HyperVEnabled(Host() with { HyperVAvailable = false, HyperV = new(true, true, "Stopped", null) })));
Test("Elevated host evidence survives IPC serialization", () => {
    var result = new OperationResult(true, "Inspected", Host: Host() with { HyperV = new(true, true, "Running", null) }, VirtualMachines: []);
    var read = Json.Read<OperationResult>(Json.Write(result));
    Check(read.Host?.HyperV?.FeatureEnabled == true && read.VirtualMachines?.Length == 0);
});
Test("Automatic recovery requires a connected network", () => {
    var plan = Plan() with { BuildEfiFromArchive = true, SwitchName = null, RecoveryDmg = "C:\\recovery.dmg", RecoveryChunklist = "C:\\recovery.chunklist", RecoveryDmgSha256 = new('a', 64), RecoveryChunklistSha256 = new('b', 64) };
    Check(PlanValidator.Validate(plan, Host()).Any(x => x.Contains("internet")));
});
AsyncTest("User-only pipe ACL permits local exchange without broad grants", async () => {
    var name = "pome-test-" + Guid.NewGuid().ToString("N");
    using var server = UserPipe.CreateServer(name);
    var acl = System.IO.Pipes.PipesAclExtensions.GetAccessControl(server);
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    Check(acl.AreAccessRulesProtected);
    foreach (System.IO.Pipes.PipeAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        Check(rule.IdentityReference.Equals(identity.User));
    using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
    UserPipe.RequireCurrentUserOwner(client);
    var readTask = PipeProtocol.ReadAsync<OperationResult>(client, timeout.Token);
    await PipeProtocol.WriteAsync(server, new OperationResult(true, "test"), timeout.Token);
    Check((await readTask).Success);
});
Test("UserPipe rejects non-current-user owner SID", () => {
    var world = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
    Throws<UnauthorizedAccessException>(() => UserPipe.RequireOwnerMatches(world, new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null)));
});
AsyncTest("PrivilegeBroker rejects missing helper binary cleanly", async () => {
    var broker = new PrivilegeBroker(Path.Combine(temp, "missing-helper.exe"));
    await Reject<FileNotFoundException>(() => broker.ExecuteAsync(new(OperationKind.Inspect)));
});
AsyncTest("Helper CLI rejects malformed arguments and invalid pipe names", async () => {
    var helperDll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PrivilegedService", "bin", "Release", "net10.0-windows10.0.19041.0", "Pome.Helper.dll");
    if (!File.Exists(helperDll)) helperDll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PrivilegedService", "bin", "Debug", "net10.0-windows10.0.19041.0", "Pome.Helper.dll");
    Check(File.Exists(helperDll), "Helper assembly must exist");
    using var p1 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{helperDll}\" --invalid") { UseShellExecute = false, CreateNoWindow = true })!;
    await p1.WaitForExitAsync();
    Check(p1.ExitCode == 2);
    using var p2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{helperDll}\" --pipe badname --parent {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true })!;
    await p2.WaitForExitAsync();
    Check(p2.ExitCode == 2);
});
AsyncTest("Helper CLI rejects mismatched parent PID on valid user pipe", async () => {
    var helperDll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PrivilegedService", "bin", "Release", "net10.0-windows10.0.19041.0", "Pome.Helper.dll");
    if (!File.Exists(helperDll)) helperDll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PrivilegedService", "bin", "Debug", "net10.0-windows10.0.19041.0", "Pome.Helper.dll");
    var name = "pome-" + Guid.NewGuid().ToString("N");
    await using var server = UserPipe.CreateServer(name);
    using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{helperDll}\" --pipe {name} --parent 999999") { UseShellExecute = false, CreateNoWindow = true })!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await server.WaitForConnectionAsync(timeout.Token);
    await p.WaitForExitAsync(timeout.Token);
    Check(p.ExitCode == 3);
});
Test("Preparation defers live RAM check but creation enforces it", () => {
    Check(PlanValidator.Validate(Plan(), Host(free: 5), preparing: true).Count == 0);
    Check(PlanValidator.Validate(Plan(), Host(free: 5)).Count > 0);
});
Test("16 GB host preserves 6 GB installed headroom", () => Check(ResourcePolicy.Recommend(Host()).MaximumMemoryGiB == 10));
Test("Memory pressure blocks creation", () => Check(!ResourcePolicy.Recommend(Host(free: 5)).CanCreate));
Test("16 GB laptop with 7 GB free RAM allows 5 GB VM creation", () => {
    var advice = ResourcePolicy.Recommend(Host(free: 7));
    Check(advice.CanCreate && advice.MemoryGiB == 5);
});
Test("Low installed RAM does not underflow", () => Check(ResourcePolicy.Recommend(Host(total: 4, free: 3)).MaximumMemoryGiB == 0));
Test("Two processors are reserved", () => Check(ResourcePolicy.Recommend(Host()).MaximumCpuCount == 14));
Test("Valid plan passes", () => Check(PlanValidator.Validate(Plan(), Host()).Count == 0));
Test("Shell syntax in VM name rejected", () => Check(PlanValidator.Validate(Plan() with { Name = "a'; Stop-VM *" }).Count > 0));
Test("Consent is enforced in backend", () => Check(PlanValidator.Validate(Plan() with { AcceptGuestFirmwareChange = false }).Count > 0));
Test("Unknown macOS profile rejected", () => Check(PlanValidator.Validate(Plan() with { MacOsVersion = "16" }).Count > 0));
Test("Stale virtual switch rejected", () => Check(PlanValidator.Validate(Plan() with { SwitchName = "missing" }, Host()).Count > 0));
Test("Allocation rechecked against free memory", () => Check(PlanValidator.Validate(Plan(), Host(free: 6)).Count > 0));
Test("Client DDA restricted even with GPU", () => Check(GpuSafety.Evaluate(Host(), new("GPU", "PCI", "1", "PCIROOT", false)).Status == CapabilityStatus.HostOsRestricted));
Test("Unknown display ownership blocks Server DDA", () => Check(GpuSafety.Evaluate(Host(server: true), new("GPU", "PCI", "1", "PCIROOT")).Status == CapabilityStatus.Unknown));
Test("Server display evidence does not prove acceleration", () => Check(GpuSafety.Evaluate(Host(server: true), new("GPU", "PCI", "1", "PCIROOT", false)).Status == CapabilityStatus.DetectedUntested));
Test("BAR64 allocation starts above 4 GB", () => Check(MmioAllocator.Plan([new(0, 0, 256UL << 20, true)], 0xC0000000, 0x40000000, 0x1_00000000, 0x2_00000000).Allocations[0].Address == 0x1_00000000));
Test("BAR32 never spills into high aperture", () => Throws<InvalidDataException>(() => MmioAllocator.Plan([new(0, 0, 2UL << 30, false)], 0xC0000000, 0x40000000, 0x1_00000000, 0x2_00000000)));
Test("BAR alignment accounted for", () => Check(MmioAllocator.Plan([new(0, 0, 0x1000, true)], 0, 0, 0x1_00000001, 0x10000).Allocations[0].Address == 0x1_00001000));
Test("Non-power-of-two BAR rejected", () => Throws<InvalidDataException>(() => MmioAllocator.Plan([new(0, 0, 123, true)], 0, 0, 0x1_00000000, 0x10000)));
Test("Address overflow rejected", () => Throws<OverflowException>(() => MmioAllocator.Plan([], 0, 0, ulong.MaxValue, 10)));
Test("BAR64 cannot start at last register", () => Throws<InvalidDataException>(() => MmioAllocator.Plan([new(0, 5, 4096, true)], 0, 0, 0x1_00000000, 0x10000)));
Test("BAR register overlap rejected", () => Throws<InvalidDataException>(() => MmioAllocator.Plan([new(0, 0, 4096, true), new(0, 1, 4096, false)], 0xC0000000, 0x40000000, 0x1_00000000, 0x10000)));
Test("Multiple PCI functions allocate separately", () => Check(MmioAllocator.Plan([new(0, 0, 4096, true), new(1, 0, 4096, true)], 0, 0, 0x1_00000000, 0x10000).HighBytes == 8192));
Test("Kext dependencies ordered", () => Check(KextResolver.Resolve([new("B", "B.kext", ["A"], 20, 25), new("A", "A.kext", [], 20, 25)], 24)[0].Id == "A"));
Test("Kext cycle rejected", () => Throws<InvalidDataException>(() => KextResolver.Resolve([new("A", "A.kext", ["B"], 20, 25), new("B", "B.kext", ["A"], 20, 25)], 24)));
Test("Missing kext rejected", () => Throws<InvalidDataException>(() => KextResolver.Resolve([new("A", "A.kext", ["B"], 20, 25)], 24)));
Test("Kernel bounds checked", () => Throws<InvalidDataException>(() => KextResolver.Resolve([new("A", "A.kext", [], 20, 23)], 24)));
Test("UNC paths rejected", () => Throws<InvalidDataException>(() => SafePaths.RequireLocal(@"\\server\share\efi.vhdx", false)));
Test("Alternate stream paths rejected", () => Throws<InvalidDataException>(() => SafePaths.RequireLocal(@"C:\media\efi.vhdx:stream", false)));
Test("Sibling prefix is not containment", () => Check(!SafePaths.IsWithin(@"C:\Windows-other", @"C:\Windows")));
Test("Existing operation directory rejected", () => { var id = Guid.NewGuid(); Directory.CreateDirectory(Path.Combine(temp, "pome-" + id.ToString("N"))); Throws<IOException>(() => SafePaths.NewOperationDirectory(temp, id)); });
Test("Journal persists incomplete state across reopen", () => { var path = Path.Combine(temp, "journal.db"); var id = Guid.NewGuid(); new SqliteJournal(path).Write(new(id, "Create", "CopyingMedia", "password=secret", DateTimeOffset.UtcNow, false)); var entry = new SqliteJournal(path).ReadAll().Single(); Check(!entry.Terminal && entry.Detail == "password=[redacted]"); });
Test("Journal upsert retains operation identity", () => { var journal = new SqliteJournal(Path.Combine(temp, "journal2.db")); var id = Guid.NewGuid(); journal.Write(new(id, "Create", "Preparing", "first", DateTimeOffset.UtcNow, false)); journal.Write(new(id, "Create", "Completed", "second", DateTimeOffset.UtcNow, true)); Check(journal.ReadAll().Single().Terminal); });
Test("Journal reconciles per-VM operation.json marker after helper failure or completion", () => {
    var dbPath = Path.Combine(temp, "reconcile.db");
    var opId = Guid.NewGuid();
    var opDir = Path.Combine(temp, "pome-" + opId.ToString("N"));
    Directory.CreateDirectory(opDir);
    var journal = new SqliteJournal(dbPath);
    journal.Write(new(opId, "Create", "Requested", "Waiting for elevation", DateTimeOffset.UtcNow, false, opDir));
    var marker = new OperationMarker(opId, "NeedsAttention", "Media copy failed: SHA-256 verification failed.", false, null, DateTimeOffset.UtcNow);
    File.WriteAllText(Path.Combine(opDir, "operation.json"), Json.Write(marker));
    Check(journal.ReconcileMarkers() == 1);
    var reconciled = journal.ReadAll().Single();
    Check(reconciled.Stage == "Needs attention" && reconciled.Detail.Contains("SHA-256") && !reconciled.Terminal && reconciled.ResourceDirectory == opDir);
    var completed = marker with { Stage = "Created", Detail = "VM configured.", Terminal = true, VmId = Guid.NewGuid() };
    File.WriteAllText(Path.Combine(opDir, "operation.json"), Json.Write(completed));
    Check(journal.ReconcileMarkers() == 1);
    var finalEntry = journal.ReadAll().Single();
    Check(finalEntry.Stage == "Created" && finalEntry.Terminal);
});
Test("Machine identities unique and locally administered", () => { var a = MachineIdentity.Create(); var b = MachineIdentity.Create(); Check(a.SystemUuid != b.SystemUuid && a.Serial != b.Serial && a.Rom.Length == 6 && (a.Rom[0] & 3) == 2); });
AsyncTest("IPC frame round trip", async () => { using var stream = new MemoryStream(); await PipeProtocol.WriteAsync(stream, new OperationRequest(OperationKind.Start, VmId: Guid.NewGuid()), default); stream.Position = 0; Check((await PipeProtocol.ReadAsync<OperationRequest>(stream, default)).Kind == OperationKind.Start); });
AsyncTest("IPC oversized frame rejected before allocation", async () => { using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue)); await Reject<InvalidDataException>(async () => await PipeProtocol.ReadAsync<OperationRequest>(stream, default)); });
AsyncTest("IPC truncated frame rejected", async () => { using var stream = new MemoryStream([4, 0, 0, 0, 1]); await Reject<EndOfStreamException>(async () => await PipeProtocol.ReadAsync<OperationRequest>(stream, default)); });
AsyncTest("Incorrect hash rejected", async () => { var path = Path.Combine(temp, "hash"); await File.WriteAllTextAsync(path, "changed"); await Reject<InvalidDataException>(() => SafePaths.VerifyHashAsync(path, new('0', 64))); });

var bytes = Encoding.UTF8.GetBytes("a small pinned upstream asset");
var asset = new DownloadAsset(new("https://github.com/owner/repo/releases/download/v1/file"), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "fixture");
AsyncTest("Download verified and atomically promoted", async () => { using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })); var path = Path.Combine(temp, "download"); await new VerifiedDownloader(client).DownloadAsync(asset, path); Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(path + ".part")); });
AsyncTest("Download cancellation aborts cleanly and preserves partial file", async () => {
    using var cts = new CancellationTokenSource();
    var path = Path.Combine(temp, "cancelled-download");
    using var client = new HttpClient(new FakeHttp(_ => { cts.Cancel(); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
    await Reject<OperationCanceledException>(() => new VerifiedDownloader(client).DownloadAsync(asset, path, cts.Token));
    Check(!File.Exists(path));
});
AsyncTest("VerifyRecoveryAsync rejects corrupted cached recovery files", async () => {
    var python = PythonLocator.Find();
    if (string.IsNullOrEmpty(python)) return;
    var badUtility = Path.Combine(temp, "fake_macrecovery.py");
    await File.WriteAllTextAsync(badUtility, "def verify_image(dmg, chunklist):\n    raise ValueError('Chunklist signature mismatch')\n");
    var dmg = Path.Combine(temp, "bad.dmg"); var chunks = Path.Combine(temp, "bad.chunklist");
    await File.WriteAllTextAsync(dmg, "corrupt"); await File.WriteAllTextAsync(chunks, "corrupt");
    await Reject<InvalidDataException>(() => BootMedia.VerifyRecoveryAsync(python, badUtility, dmg, chunks));
});
AsyncTest("Download resumes verified byte range", async () => { var path = Path.Combine(temp, "resumed"); await File.WriteAllBytesAsync(path + ".part", bytes[..5]); using var client = new HttpClient(new FakeHttp(request => { Check(request.Headers.Range?.Ranges.Single().From == 5); var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[5..]) }; response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, bytes.Length - 1, bytes.Length); return response; })); await new VerifiedDownloader(client).DownloadAsync(asset, path); Check(File.ReadAllBytes(path).SequenceEqual(bytes)); });
AsyncTest("Server ignoring range restarts download", async () => { var path = Path.Combine(temp, "restart"); await File.WriteAllBytesAsync(path + ".part", bytes[..5]); using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })); await new VerifiedDownloader(client).DownloadAsync(asset, path); Check(File.ReadAllBytes(path).SequenceEqual(bytes)); });
AsyncTest("Untrusted redirect rejected", async () => { using var client = new HttpClient(new FakeHttp(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://evil.example/file"); return r; })); await Reject<InvalidDataException>(() => new VerifiedDownloader(client).DownloadAsync(asset, Path.Combine(temp, "redirect"))); });
AsyncTest("Corrupt download quarantined", async () => { using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length]) })); var path = Path.Combine(temp, "corrupt"); await Reject<InvalidDataException>(() => new VerifiedDownloader(client).DownloadAsync(asset, path)); Check(!File.Exists(path) && Directory.GetFiles(temp, "corrupt.part.rejected-*").Length == 1); });
AsyncTest("Oversized body rejected", async () => { using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[bytes.Length + 1]) })); await Reject<InvalidDataException>(() => new VerifiedDownloader(client).DownloadAsync(asset, Path.Combine(temp, "oversize"))); });
AsyncTest("Interrupted download retains partial file", async () => { using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..5]) })); var path = Path.Combine(temp, "interrupted"); await Reject<IOException>(() => new VerifiedDownloader(client).DownloadAsync(asset, path)); Check(new FileInfo(path + ".part").Length == 5 && !File.Exists(path)); });
Test("Zip traversal rejected before extraction", () => { var path = Path.Combine(temp, "traversal.zip"); using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) { using var w = new StreamWriter(z.CreateEntry("../escape.txt").Open()); w.Write("no"); } Throws<InvalidDataException>(() => SafeArchive.Extract(path, Path.Combine(temp, "unsafe"))); Check(!File.Exists(Path.Combine(temp, "escape.txt"))); });
Test("Zip duplicate case names rejected", () => { var path = Path.Combine(temp, "duplicate.zip"); using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) { z.CreateEntry("EFI/A"); z.CreateEntry("efi/a"); } Throws<InvalidDataException>(() => SafeArchive.Extract(path, Path.Combine(temp, "duplicate"))); });
Test("Safe zip preserves directory structure", () => { var path = Path.Combine(temp, "safe.zip"); using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) { using var w = new StreamWriter(z.CreateEntry("EFI/OC/test.txt").Open()); w.Write("safe"); } var output = Path.Combine(temp, "safe"); SafeArchive.Extract(path, output); Check(File.ReadAllText(Path.Combine(output, "EFI/OC/test.txt")) == "safe"); });
// AMD hosts are supported (AMD_Vanilla), so test the vendor-mismatch rule, which holds on any runner.
Test("A recorded processor vendor that differs from this CPU fails closed", () =>
{
    var leaf0 = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
    var amdHost = leaf0.Ebx == 0x68747541; // "Auth"enticAMD
    Throws<NotSupportedException>(() => BootMedia.DetectIntelProfile(amdHost ? "GenuineIntel" : "AuthenticAMD"));
});
Test("Automatic media requires verified recovery", () => Check(PlanValidator.Validate(Plan() with { BuildEfiFromArchive = true }).Count > 0));
var plist = "<plist><dict><key>Kernel</key><dict><key>Add</key><array><dict><key>BundlePath</key><string>MacHyperVSupport.kext</string></dict></array><key>Quirks</key><dict><key>ProvideCurrentCpuInfo</key><false/></dict><key>Emulate</key><dict><key>Cpuid1Data</key><data/><key>Cpuid1Mask</key><data/></dict></dict><key>PlatformInfo</key><dict><key>Generic</key><dict><key>SystemProductName</key><string>old</string><key>SystemUUID</key><string/><key>SystemSerialNumber</key><string/><key>MLB</key><string/><key>ROM</key><data/></dict></dict></dict></plist>";
plist = plist.Replace("<key>PlatformInfo</key>", "<key>NVRAM</key><dict><key>Add</key><dict><key>7C436110-AB2A-4BBB-A880-FE41995C9F82</key><dict><key>boot-args</key><string>-v keepsyms=1 cpus=8 cpus=2</string></dict></dict></dict><key>PlatformInfo</key>");
plist = plist.Replace("<key>PlatformInfo</key>", "<key>Misc</key><dict><key>Security</key><dict><key>DmgLoading</key><string>Signed</string></dict><key>Debug</key><dict><key>AppleDebug</key><false/><key>ApplePanic</key><false/><key>Target</key><integer>3</integer></dict></dict><key>PlatformInfo</key>");
Test("OpenCore compatibility boot preserves flags and replaces conflicting CPU limits", () => {
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist));
    var doc = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel);
    var bootArgs = doc.Descendants("key").Single(x => x.Value == "boot-args").ElementsAfterSelf().First().Value;
    Check(bootArgs == "-v keepsyms=1 cpus=1");
    Check(doc.Descendants("key").Single(x => x.Value == "DmgLoading").ElementsAfterSelf().First().Value == "Disabled");
    Check(doc.Descendants("key").Single(x => x.Value == "AppleDebug").ElementsAfterSelf().First().Name == "true");
    Check(doc.Descendants("key").Single(x => x.Value == "Target").ElementsAfterSelf().First().Value == "67");
    using var again = new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString()));
    Check(OpenCoreConfiguration.Configure(again, MachineIdentity.Create(), CpuProfile.NativeIntel).Descendants("string").Count(x => x.Value.Contains("cpus=1")) == 1);
});
Test("Virtual GPU relaxes AMFI and SIP and restores exactly what it changed", () => {
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist));
    var doc = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel);
    string Arg() => doc.Descendants("key").Single(x => x.Value == "boot-args").ElementsAfterSelf().First().Value;
    var before = Arg();
    var record = VirtualGpuPolicy.Enable(doc);
    Check(Arg() == before + " amfi_get_out_of_my_way=0x1 amfi=0x80 ipc_control_port_options=0", Arg());
    Check(doc.Descendants("key").Single(x => x.Value == "csr-active-config").ElementsAfterSelf().First().Value == "QwoAAA==");
    var deletes = doc.Descendants("key").Single(x => x.Value == "Delete").ElementsAfterSelf().First();
    Check(deletes.Descendants("string").Select(x => x.Value).Order().SequenceEqual(new[] { "boot-args", "csr-active-config" }));
    Check(record.OriginalBootArgs == before && record.OriginalCsrActiveConfig is null && record.AddedBootArgsDelete && record.AddedCsrDelete);
    // Enabling twice must not duplicate flags (a retried operation).
    VirtualGpuPolicy.Enable(doc);
    Check(Arg().Split(' ').Count(x => x.StartsWith("amfi=", StringComparison.Ordinal)) == 1);
    VirtualGpuPolicy.Disable(doc, Json.Read<VirtualGpuRecord>(Json.Write(record)));
    Check(Arg() == before, Arg());
    Check(!doc.Descendants("key").Any(x => x.Value == "csr-active-config"));
    Check(!deletes.Descendants("string").Any());
    // A user's own amfi flag that existed before is kept on Disable.
    doc.Descendants("key").Single(x => x.Value == "boot-args").ElementsAfterSelf().First().Value = before + " amfi=0x1";
    var mine = VirtualGpuPolicy.Enable(doc);
    VirtualGpuPolicy.Disable(doc, mine);
    Check(Arg() == before + " amfi=0x1", Arg());
});
Test("Virtual GPU is limited to managed Tahoe VMs", () => {
    var vm = new VmInfo(Guid.NewGuid(), "t", "Off", 4, 0, 0, "pome:v1:" + Guid.NewGuid());
    VmDetails D(string? version, bool on = false) => new(vm.Id, "t", 4, 4UL << 30, null, "C:\\x", "Off", [], [], [], GuestMacOsVersion: version, VirtualGpu: on);
    VirtualGpuPolicy.RequireCanEnable(vm, D("26"));
    Throws<InvalidDataException>(() => VirtualGpuPolicy.RequireCanEnable(vm, D("15")));
    Throws<InvalidDataException>(() => VirtualGpuPolicy.RequireCanEnable(vm, D(null)));
    Throws<InvalidDataException>(() => VirtualGpuPolicy.RequireCanEnable(vm, D("26", on: true)));
    Throws<InvalidDataException>(() => VirtualGpuPolicy.RequireCanEnable(vm with { Notes = "" }, D("26")));
    VirtualGpuPolicy.RequireCanDisable(vm, D("26", on: true));
    Throws<InvalidDataException>(() => VirtualGpuPolicy.RequireCanDisable(vm, D("26")));
    Check(VirtualGpuPolicy.ServiceId == new Guid("50564731-facb-11e6-bd58-64006a7986d3"));
});
Test("OpenCore preserves template and writes masked CPU profile", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist)); var id = MachineIdentity.Create(); var doc = OpenCoreConfiguration.Configure(input, id, CpuProfile.CometLakeEmulation); Check(doc.ToString().Contains(id.Serial) && doc.ToString().Contains("VQYKAAAAAAAAAAAAAAAAAA==") && doc.ToString().Contains("/////wAAAAAAAAAAAAAAAA==")); });
Test("OpenCore rejects bare-metal template", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("MacHyperVSupport.kext", "WhateverGreen.kext"))); Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel)); });
Test("OpenCore duplicate key rejected", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("<key>ROM</key>", "<key>SystemUUID</key><string/><key>ROM</key>"))); Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel)); });
Test("OpenCore handles formatted plist with comments", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("</key>", "</key>\r\n<!-- source annotation -->\r\n"))); Check(OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel).Root is not null); });

// --- macOS version catalog -------------------------------------------------
Test("Catalog covers OS X 10.7 through macOS 26 with unique versions", () => {
    var versions = MacOsCatalog.Releases.Select(x => x.Version).ToArray();
    Check(versions.Distinct().Count() == versions.Length && versions.First() == "26" && versions.Last() == "10.7" && MacOsCatalog.Default.Version == "15");
    Check(MacOsCatalog.Releases.Zip(MacOsCatalog.Releases.Skip(1)).All(p => p.First.Darwin > p.Second.Darwin), "Releases must be ordered newest first");
});
Test("Recovery arguments follow OpenCore's recovery table", () => {
    Check(MacOsCatalog.Require("26").RecoveryArguments.SequenceEqual(["-b", "Mac-CFF7D910A743CAAF", "-m", "00000000000000000", "-os", "latest"]));
    Check(MacOsCatalog.Require("10.14").RecoveryArguments.SequenceEqual(["-b", "Mac-7BA5B2DFE22DDD8C", "-m", "00000000000KXPG00"]));
    Check(MacOsCatalog.Require("15").RecoveryCacheName != MacOsCatalog.Require("10.13").RecoveryCacheName, "Releases sharing a board ID must not share a recovery cache");
});
Test("SMBIOS models exist in each release", () => {
    Check(MacOsCatalog.Releases.Where(x => x.Darwin >= 20).All(x => x.SmbiosModel == "iMac20,1"));
    Check(MacOsCatalog.Require("10.15").SmbiosModel == "iMac19,1" && MacOsCatalog.Require("10.13").SmbiosModel == "iMac18,3" && MacOsCatalog.Require("10.7").SmbiosModel == "iMac12,2");
});
Test("Every catalog version is accepted and unknown versions are rejected", () => {
    foreach (var release in MacOsCatalog.Releases) Check(PlanValidator.Validate(Plan() with { MacOsVersion = release.Version }).Count == 0, release.Version);
    Check(PlanValidator.Validate(Plan() with { MacOsVersion = "10.6" }).Count > 0 && PlanValidator.Validate(Plan() with { MacOsVersion = "27" }).Count > 0);
});
Test("Automatic Tahoe media requires the pinned MacHyperVSupport update", () => {
    var plan = Plan() with { MacOsVersion = "26", BuildEfiFromArchive = true, RecoveryDmg = "C:\\r\\BaseSystem.dmg", RecoveryDmgSha256 = new('b', 64), RecoveryChunklist = "C:\\r\\BaseSystem.chunklist", RecoveryChunklistSha256 = new('c', 64) };
    Check(PlanValidator.Validate(plan).Any(x => x.Contains("MacHyperVSupport")));
    Check(!PlanValidator.Validate(plan with { HyperVDriverArchive = "C:\\r\\driver.zip", HyperVDriverSha256 = UpstreamAssets.MacHyperVSupport.Sha256 }).Any(x => x.Contains("MacHyperVSupport")));
    Check(!PlanValidator.Validate(plan with { MacOsVersion = "15" }).Any(x => x.Contains("MacHyperVSupport")));
});
Test("Driver pin points at the official acidanthera release", () =>
    Check(UpstreamAssets.MacHyperVSupport.Url.AbsoluteUri.StartsWith("https://github.com/acidanthera/MacHyperVSupport/releases/download/1.0.0/") && UpstreamAssets.MacHyperVSupport.Size == 4852457));

// --- CPU profiles ------------------------------------------------------------
CpuProfile Cpu(int model, string version, bool avx2 = true) => CpuCompatibility.Choose(true, 6, model, avx2, MacOsCatalog.Require(version));
Test("Raptor Lake is presented as a CPU each release recognizes", () => {
    Check(Cpu(0xBA, "26") == CpuProfile.CometLakeEmulation && Cpu(0xBA, "10.15") == CpuProfile.CometLakeEmulation);
    Check(Cpu(0xBA, "10.14") == CpuProfile.HaswellEmulation && Cpu(0xBA, "10.8") == CpuProfile.HaswellEmulation);
    Check(Cpu(0xBA, "10.7") == CpuProfile.IvyBridgeEmulation);
});
Test("Newer Intel generations are recognized", () => {
    foreach (var model in new[] { 0xA7, 0xAA, 0xAC, 0xC5, 0xC6, 0xBD, 0xBE }) Check(Cpu(model, "15") == CpuProfile.CometLakeEmulation, $"0x{model:X2}");
    Check(Cpu(0x7E, "15") == CpuProfile.NativeIntel && Cpu(0x7E, "10.14") == CpuProfile.HaswellEmulation, "Ice Lake is native only from Catalina");
});
Test("Native CPUs older than a release's minimum are emulated, not rejected", () => {
    Check(Cpu(0x9E, "15") == CpuProfile.NativeIntel && Cpu(0x9E, "10.12") == CpuProfile.HaswellEmulation);
    Check(Cpu(0x3C, "10.8") == CpuProfile.NativeIntel && Cpu(0x3C, "10.7") == CpuProfile.IvyBridgeEmulation);
});
Test("Sandy and Ivy Bridge stop at Monterey and Ventura+ requires AVX2", () => {
    Check(Cpu(0x3A, "12", avx2: false) == CpuProfile.NativeIntel && Cpu(0x2A, "10.7", avx2: false) == CpuProfile.NativeIntel);
    Throws<NotSupportedException>(() => Cpu(0x3A, "13", avx2: false));
    Throws<NotSupportedException>(() => Cpu(0x3A, "26", avx2: false));
    Throws<NotSupportedException>(() => Cpu(0x2A, "13", avx2: true));
});
Test("AMD and unknown Intel models fail closed", () => {
    Throws<NotSupportedException>(() => CpuCompatibility.Choose(false, 25, 0x21, true, MacOsCatalog.Default));
    Throws<NotSupportedException>(() => Cpu(0x01, "15"));
    Throws<NotSupportedException>(() => CpuCompatibility.Choose(true, 15, 0x01, true, MacOsCatalog.Default));
});

// --- creation draft and resources ---------------------------------------------
Test("Default VM name follows the chosen release until edited", () => {
    var draft = new CreationDraft();
    draft.ApplyMachines([Managed() with { Name = "Tahoe" }]);
    draft.Release = MacOsCatalog.Require("26"); Check(draft.Name == "Tahoe 2");
    draft.Release = MacOsCatalog.Require("10.13"); Check(draft.Name == "High Sierra" && PlanValidator.IsValidName(draft.Name));
    draft.Name = "Work Mac"; draft.Release = MacOsCatalog.Require("14"); Check(draft.Name == "Work Mac");
});
Test("Compatibility boot recommends two virtual processors", () => Check(ResourcePolicy.Recommend(Host()).CpuCount == 2 && new CreationDraft().CpuCount == 2));
Test("Power-of-two processor counts are identified", () => Check(new[] { 1, 2, 4, 8, 16 }.All(ResourcePolicy.IsPowerOfTwo) && !new[] { 0, 3, 6, 12 }.Any(ResourcePolicy.IsPowerOfTwo)));

// --- OpenCore per-release configuration -----------------------------------------
var legacyPlist = plist.Replace("<key>PlatformInfo</key>", "<key>Booter</key><dict><key>Quirks</key><dict><key>AllowRelocationBlock</key><false/><key>ForceExitBootServices</key><false/></dict></dict><key>PlatformInfo</key>");
string PlistValue(XDocument doc, string key) => doc.Descendants("key").Single(x => x.Value == key).ElementsAfterSelf().First() is var e && e.Name == "string" ? e.Value : e.Name.LocalName;
Test("OpenCore writes the release's SMBIOS model", () => {
    foreach (var (version, model) in new[] { ("26", "iMac20,1"), ("10.15", "iMac19,1"), ("10.12", "iMac17,1") })
    { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist)); Check(PlistValue(OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel, MacOsCatalog.Require(version)), "SystemProductName") == model, version); }
});
Test("OpenCore enables legacy booter quirks only for OS X 10.7", () => {
    using var lion = new MemoryStream(Encoding.UTF8.GetBytes(legacyPlist));
    var doc = OpenCoreConfiguration.Configure(lion, MachineIdentity.Create(), CpuProfile.IvyBridgeEmulation, MacOsCatalog.Require("10.7"));
    Check(PlistValue(doc, "AllowRelocationBlock") == "true" && PlistValue(doc, "ForceExitBootServices") == "true");
    using var sequoia = new MemoryStream(Encoding.UTF8.GetBytes(legacyPlist));
    Check(PlistValue(OpenCoreConfiguration.Configure(sequoia, MachineIdentity.Create(), CpuProfile.NativeIntel), "AllowRelocationBlock") == "false");
});
Test("OpenCore writes Haswell and Ivy Bridge CPUID signatures", () => {
    foreach (var (profile, data) in new[] { (CpuProfile.HaswellEmulation, "wwYDAAAAAAAAAAAAAAAAAA=="), (CpuProfile.IvyBridgeEmulation, "qQYDAAAAAAAAAAAAAAAAAA==") })
    { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist)); var text = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), profile, MacOsCatalog.Require("10.13")).ToString(); Check(text.Contains(data) && text.Contains("/////wAAAAAAAAAAAAAAAA=="), profile.ToString()); }
});

// --- power actions and removal -----------------------------------------------------
Test("Turn off and reset are offered only in states Hyper-V accepts", () => {
    Check(Managed("Running").CanTurnOff && Managed("Paused").CanTurnOff && Managed("Starting").CanTurnOff && !Managed("Off").CanTurnOff && !Managed("Saved").CanTurnOff);
    Check(Managed("Running").CanReset && !Managed("Paused").CanReset && Managed("Off").CanDelete && !Managed("Running").CanDelete);
    var external = Managed("Running") with { Notes = "" };
    Check(!external.CanTurnOff && !external.CanReset && !(external with { State = "Off" }).CanDelete);
});
Test("Policy rechecks power and removal state", () => {
    var running = Managed("Running");
    VmManagementPolicy.RequireAllowed(new(OperationKind.TurnOff, VmId: running.Id), running);
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.DeleteVm, VmId: running.Id), running));
    var off = Managed();
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.TurnOff, VmId: off.Id), off));
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.DeleteVm, VmId: off.Id), off with { Notes = "external" }));
});
Test("Only power actions may bypass the elevated helper", () => {
    Check(HyperVAccess.IsLifecycle(OperationKind.TurnOff) && HyperVAccess.IsLifecycle(OperationKind.Start));
    Check(!new[] { OperationKind.Create, OperationKind.DeleteVm, OperationKind.ResizeDisk, OperationKind.Export, OperationKind.GrantHyperVAccess, OperationKind.ExportBootLogs }.Any(HyperVAccess.IsLifecycle));
});
Test("Removal is limited to the VM's own pome folder", () => {
    var operation = Guid.NewGuid();
    var vm = new VmInfo(Guid.NewGuid(), "Mac", "Off", 2, 0, 0, "pome:v1:" + operation);
    var folder = Path.Combine(temp, "pome-" + operation.ToString("N")); Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "operation.json"), Json.Write(new OperationMarker(operation, "Created", "", true, vm.Id, DateTimeOffset.UtcNow)));
    foreach (var name in new[] { "EFI.vhdx", "macOS.vhdx" }) File.WriteAllText(Path.Combine(folder, name), "disk");
    VmDisk Disk(string path, string slot) => new(path, 0, slot);
    var details = new VmDetails(vm.Id, "Mac", 2, 0, null, Path.Combine(folder, "Mac"), "Off", [Disk(Path.Combine(folder, "EFI.vhdx"), "SCSI 0:0"), Disk(Path.Combine(folder, "macOS.vhdx"), "SCSI 0:1")], [], []);
    Check(CreationPreflight.RequireOwnedOperationFolder(vm, details) == folder);
    var outside = Path.Combine(temp, "outside.vhdx"); File.WriteAllText(outside, "disk");
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedOperationFolder(vm, details with { Disks = [.. details.Disks, Disk(outside, "SCSI 0:2")] }));
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedOperationFolder(vm, details with { Checkpoints = [new(Guid.NewGuid(), "c", DateTime.Now)] }));
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedOperationFolder(vm, details with { ConfigurationLocation = temp }));
    Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedOperationFolder(vm with { Notes = "pome:v1:" + Guid.NewGuid() }, details));
    var junction = Path.Combine(folder, "link");
    using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, temp }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) mklink.WaitForExit();
    if (Directory.Exists(junction))
    {
        Throws<InvalidDataException>(() => CreationPreflight.RequireOwnedOperationFolder(vm, details));
        Directory.Delete(junction);
    }
});

// --- AMD profile -------------------------------------------------------------------
Test("AMD families map to AMD_Vanilla from High Sierra", () => {
    foreach (var family in new[] { 0x17, 0x19, 0x1A }) Check(CpuCompatibility.Choose("AuthenticAMD", family, 0x21, true, MacOsCatalog.Require("26")) == CpuProfile.AmdVanilla, $"{family:X2}h");
    Check(CpuCompatibility.Choose("AuthenticAMD", 0x15, 0x02, false, MacOsCatalog.Require("12")) == CpuProfile.AmdVanilla);
    Throws<NotSupportedException>(() => CpuCompatibility.Choose("AuthenticAMD", 0x19, 0x21, true, MacOsCatalog.Require("10.12")));
    Throws<NotSupportedException>(() => CpuCompatibility.Choose("AuthenticAMD", 0x15, 0x02, true, MacOsCatalog.Require("13")));
    Throws<NotSupportedException>(() => CpuCompatibility.Choose("AuthenticAMD", 0x10, 0x02, true, MacOsCatalog.Require("12")));
    Throws<NotSupportedException>(() => CpuCompatibility.Choose("HygonGenuine", 0x18, 0x00, true, MacOsCatalog.Require("12")));
});
string AmdPatchPlist()
{
    string Patch(string comment, string replace) => $"<dict><key>Comment</key><string>{comment}</string><key>Find</key><data>AAAA</data><key>Identifier</key><string>kernel</string><key>Replace</key><data>{replace}</data></dict>";
    var core = new[] { "uAAAAAAA", "ugAAAAAA", "ugAAAACQ", "ugAAAAA=" }.Select(r => Patch("algrey | Force cpuid_cores_per_package to constant (user-specified) | 10.x", r));
    var other = Enumerable.Range(0, 8).Select(i => Patch($"algrey | other patch {i}", "kJA="));
    return $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>Kernel</key><dict><key>Patch</key><array>{string.Concat(core.Concat(other))}</array></dict></dict></plist>";
}
var amdConfig = plist.Replace("<key>Emulate</key><dict>", "<key>Patch</key><array><dict><key>Comment</key><string>hv_vmm_present</string></dict></array><key>Emulate</key><dict><key>DummyPowerManagement</key><false/>");
Test("AMD patches receive the VM core count, DummyPowerManagement and the Hyper-V wrmsr patch", () => {
    var patches = AmdKernelPatches.Read(new MemoryStream(Encoding.UTF8.GetBytes(AmdPatchPlist())));
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(amdConfig));
    var doc = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.AmdVanilla, MacOsCatalog.Require("15"), patches, 4);
    var patchArray = doc.Descendants("key").First(x => x.Value == "Patch").ElementsAfterSelf().First();
    var replaces = patchArray.Elements("dict").Where(d => d.ToString().Contains("cores_per_package")).Select(d => Convert.FromBase64String(d.Elements("key").Single(k => k.Value == "Replace").ElementsAfterSelf().First().Value)).ToArray();
    Check(replaces.Length == 4 && replaces.All(r => r[1] == 4 && r[0] is 0xB8 or 0xBA), "Core count not written");
    Check(patchArray.Elements("dict").Count() == 1 + 12 + 1, "Existing template patches must be kept and AMD/Hyper-V patches added once");
    Check(doc.ToString().Contains(AmdKernelPatches.HyperVWrmsrComment) && PlistValue(doc, "DummyPowerManagement") == "true");
    Check(doc.ToString().Contains("AAAAAAAAAAAAAAAAAAAAAA=="), "AMD must not receive an Intel CPUID spoof");
    using var again = new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString()));
    var twice = OpenCoreConfiguration.Configure(again, MachineIdentity.Create(), CpuProfile.AmdVanilla, MacOsCatalog.Require("15"), patches, 8);
    Check(twice.Descendants("key").First(x => x.Value == "Patch").ElementsAfterSelf().First().Elements("dict").Count() == 14, "Reapplying duplicated AMD patches");
});
Test("AMD patch lists with unexpected placeholders are rejected", () => {
    Throws<InvalidDataException>(() => AmdKernelPatches.Read(new MemoryStream(Encoding.UTF8.GetBytes(AmdPatchPlist().Replace("Force cpuid_cores_per_package", "x")))));
    var patches = AmdKernelPatches.Read(new MemoryStream(Encoding.UTF8.GetBytes(AmdPatchPlist().Replace("uAAAAAAA", "uAQAAAAA"))));
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(amdConfig));
    Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.AmdVanilla, MacOsCatalog.Require("15"), patches, 4));
    using var missing = new MemoryStream(Encoding.UTF8.GetBytes(amdConfig));
    Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(missing, MachineIdentity.Create(), CpuProfile.AmdVanilla, MacOsCatalog.Require("15")));
});
Test("AMD plans need verified patches and power-of-two processors", () => {
    var plan = Plan() with { BuildEfiFromArchive = true, CpuProfile = CpuProfile.AmdVanilla, RecoveryDmg = "C:\\r\\a.dmg", RecoveryDmgSha256 = new('b', 64), RecoveryChunklist = "C:\\r\\a.chunklist", RecoveryChunklistSha256 = new('c', 64) };
    Check(PlanValidator.Validate(plan).Any(x => x.Contains("AMD_Vanilla")));
    var ready = plan with { AmdPatches = "C:\\r\\p.plist", AmdPatchesSha256 = UpstreamAssets.AmdVanillaPatches.Sha256 };
    Check(!PlanValidator.Validate(ready).Any(x => x.Contains("AMD")));
    Check(PlanValidator.Validate(ready with { CpuCount = 6 }).Any(x => x.Contains("2, 4, 8")));
    Check(PlanValidator.Validate(ready with { MacOsVersion = "10.12" }).Any(x => x.Contains("High Sierra")));
});
Test("AMD hosts keep the processor count that the boot media encodes", () => {
    var vm = Managed();
    var amdHost = Host() with { CpuVendor = "AuthenticAMD" };
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.Configure, VmId: vm.Id, Configuration: new("Mac", 8, 8, "Default Switch")), vm, amdHost));
    VmManagementPolicy.RequireAllowed(new(OperationKind.Configure, VmId: vm.Id, Configuration: new("Mac", vm.ProcessorCount, 8, "Default Switch")), vm, amdHost);
    VmManagementPolicy.RequireAllowed(new(OperationKind.Configure, VmId: vm.Id, Configuration: new("Mac", 8, 8, "Default Switch")), vm, Host());
});
Test("AMD patch pin points at a fixed AMD_Vanilla commit on an allowlisted host", () =>
    Check(UpstreamAssets.AmdVanillaPatches.Url.AbsoluteUri == "https://raw.githubusercontent.com/AMD-OSX/AMD_Vanilla/eaf52ef292abf4ebec899df6d48626569ba50cc6/patches.plist"));
Test("Multi-CPU boots stay disabled until a fixed driver is pinned", () => {
    Check(!GuestDriverPolicy.PinnedDriverFixesSmp && GuestDriverPolicy.LimitToOneCpu(true) && GuestDriverPolicy.LimitToOneCpu(false));
    Check(GuestDriverPolicy.UsesUpdatedDriver(MacOsCatalog.Require("26")) && !GuestDriverPolicy.UsesUpdatedDriver(MacOsCatalog.Require("15")));
    using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist));
    var smp = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel, limitToOneCpu: false);
    Check(PlistValue(smp, "boot-args") == "-v keepsyms=1", "A fixed driver must remove every cpus= limit");
});

// --- GPU partitioning (experimental, Tahoe only) ------------------------------------
VmDetails GpuDetails(VmInfo vm, string? version, string[]? partitions = null, VmCheckpoint[]? checkpoints = null) =>
    new(vm.Id, vm.Name, 2, 8UL << 30, null, "C:\\vm", "Off", [], checkpoints ?? [], [], partitions ?? [],
        [new(@"\\?\PCI#VEN_10DE&DEV_28E0&SUBSYS_3D4317AA&REV_A1#4&2f1b5d7c&0&0008#{064092b3-625e-43bf-9eb5-dc845897dd59}", "NVIDIA GeForce RTX 4060 Laptop GPU")], version);
Test("GPU partitions are limited to recorded Tahoe guests", () => {
    var vm = Managed(); var gpu = GpuDetails(vm, "26").PartitionableGpus![0].InstancePath;
    GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, "26"), gpu);
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, "15"), gpu));
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, null), gpu));
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm with { Notes = "external" }, GpuDetails(vm, "26"), gpu));
});
Test("GPU partition attach rechecks checkpoints, duplicates and the host GPU list", () => {
    var vm = Managed(); var gpu = GpuDetails(vm, "26").PartitionableGpus![0].InstancePath;
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, "26", checkpoints: [new(Guid.NewGuid(), "c", DateTime.Now)]), gpu));
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, "26", partitions: [gpu]), gpu));
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanAdd(vm, GpuDetails(vm, "26"), @"\\?\PCI#VEN_1002&DEV_73DF#x#{y}"));
    Throws<InvalidDataException>(() => GpuPartitionPolicy.RequireCanRemove(vm, GpuDetails(vm, "26")));
    GpuPartitionPolicy.RequireCanRemove(vm, GpuDetails(vm, "15", partitions: [gpu]));
    Throws<InvalidDataException>(() => VmManagementPolicy.RequireAllowed(new(OperationKind.AddGpuPartition, VmId: vm.Id), vm with { State = "Running" }));
});
Test("GPU instance paths map to display adapter PnP IDs", () =>
    Check(GpuPartitionPolicy.PnpIdFromInstancePath(@"\\?\PCI#VEN_10DE&DEV_28E0&SUBSYS_3D4317AA&REV_A1#4&2f1b5d7c&0&0008#{064092b3-625e-43bf-9eb5-dc845897dd59}")
        == @"PCI\VEN_10DE&DEV_28E0&SUBSYS_3D4317AA&REV_A1\4&2f1b5d7c&0&0008"));
Test("Guest records are read only from the VM's own pome folder", () => {
    var operation = Guid.NewGuid(); var vm = new VmInfo(Guid.NewGuid(), "Mac", "Off", 2, 0, 0, "pome:v1:" + operation);
    var folder = Path.Combine(temp, "pome-" + operation.ToString("N")); Directory.CreateDirectory(folder);
    var details = new VmDetails(vm.Id, "Mac", 2, 0, null, folder, "Off", [new(Path.Combine(folder, "macOS.vhdx"), 0, "SCSI 0:1")], [], []);
    Check(CreationPreflight.TryReadGuestRecord(vm, details) is null, "Missing record must read as unknown");
    File.WriteAllText(Path.Combine(folder, "guest.json"), Json.Write(new GuestRecord(1, "26", CpuProfile.CometLakeEmulation, DateTimeOffset.UtcNow)));
    Check(CreationPreflight.TryReadGuestRecord(vm, details)?.MacOsVersion == "26");
    Check(CreationPreflight.TryReadGuestRecord(vm with { Notes = "pome:v1:" + Guid.NewGuid() }, details) is null, "Another operation's folder was trusted");
    File.WriteAllText(Path.Combine(folder, "guest.json"), "{\"schemaVersion\":1,\"macOsVersion\":\"99\"}");
    Check(CreationPreflight.TryReadGuestRecord(vm, details) is null, "Unknown versions must not be trusted");
});
var amdArgument = Array.IndexOf(args, "--amd-patches");
if (amdArgument >= 0 && amdArgument + 1 < args.Length)
{
    AsyncTest("Real pinned AMD_Vanilla patches parse and receive the core count", async () => {
        var file = SafePaths.RequireLocal(args[amdArgument + 1]);
        await SafePaths.VerifyHashAsync(file, UpstreamAssets.AmdVanillaPatches.Sha256);
        await using var stream = File.OpenRead(file);
        var patches = AmdKernelPatches.Read(stream);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(amdConfig));
        var doc = OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.AmdVanilla, MacOsCatalog.Require("26"), patches, 8);
        Check(doc.Descendants("data").Count(x => x.Value is "uAgAAAAA" or "uggAAAAA" or "uggAAACQ" or "uggAAAA=") == 4, "Real core-count patches were not set to 8");
    });
}
var archiveArgument = Array.IndexOf(args, "--upstream-archive");
if (archiveArgument >= 0 && archiveArgument + 1 < args.Length)
{
    AsyncTest("Pinned upstream EFI configured and accepted by real ocvalidate", async () =>
    {
        var output = Path.Combine(temp, "upstream-efi");
        await BootMedia.ConfigureAsync(args[archiveArgument + 1], output, CpuProfile.CometLakeEmulation, MachineIdentity.Create());
        await BootMedia.ValidateAsync(output);
    });
}
var recoveryArgument = Array.IndexOf(args, "--recovery-image");
if (recoveryArgument >= 0 && recoveryArgument + 1 < args.Length)
{
    AsyncTest("Real cached recovery restores to a separate GPT disk without mounting", async () => {
        var source = SafePaths.RequireLocal(args[recoveryArgument + 1]);
        var output = Path.Combine(temp, "actual-recovery.vhd");
        var size = RecoveryDisk.GetVirtualSize(source);
        await RecoveryDisk.RestoreAsync(source, output);
        Check(new FileInfo(output).Length == size + 512);
        Console.WriteLine($"Recovery validation artifact: {output}");
    });
}
if (args.Contains("--host-survey"))
{
    AsyncTest("Read-only physical host discovery returns actual CPU and RAM", async () =>
    {
        var host = await new WindowsHostDiscovery(new PowerShellRunner()).DiscoverAsync();
        Check(host.LogicalProcessors > 0 && host.TotalMemoryBytes > 0 && !string.IsNullOrWhiteSpace(host.Cpu));
        Check(host.HyperV is not null);
        if (host.HyperV!.ServiceRunning) Check(HostCompatibility.HyperVEnabled(host), "Running VMMS must not report Unknown");
        Console.WriteLine($"HOST EVIDENCE: VMMS={host.HyperV.ServiceState}; feature={host.HyperV.FeatureEnabled}; management={host.HyperVAvailable}; hypervisor={host.HypervisorPresent}; enabled={HostCompatibility.HyperVEnabled(host)}");
    });
}

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL {test.Name}: {e}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed. Policy, local storage, IPC and simulated HTTP tests only; no physical Hyper-V/GPU tests.");
// Keep the uniquely named test directory for inspection; never recursively delete a computed user path.
return failures == 0 ? 0 : 1;

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
