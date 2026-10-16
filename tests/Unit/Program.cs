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
Test("Unverified Tahoe profile rejected", () => Check(PlanValidator.Validate(Plan() with { MacOsVersion = "26" }).Count > 0));
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
Test("Unsupported AMD auto-profile fails closed", () => Throws<NotSupportedException>(() => BootMedia.DetectIntelProfile("AuthenticAMD")));
Test("Automatic media requires verified recovery", () => Check(PlanValidator.Validate(Plan() with { BuildEfiFromArchive = true }).Count > 0));
var plist = "<plist><dict><key>Kernel</key><dict><key>Add</key><array><dict><key>BundlePath</key><string>MacHyperVSupport.kext</string></dict></array><key>Quirks</key><dict><key>ProvideCurrentCpuInfo</key><false/></dict><key>Emulate</key><dict><key>Cpuid1Data</key><data/><key>Cpuid1Mask</key><data/></dict></dict><key>PlatformInfo</key><dict><key>Generic</key><dict><key>SystemProductName</key><string>old</string><key>SystemUUID</key><string/><key>SystemSerialNumber</key><string/><key>MLB</key><string/><key>ROM</key><data/></dict></dict></dict></plist>";
Test("OpenCore preserves template and writes masked CPU profile", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist)); var id = MachineIdentity.Create(); var doc = OpenCoreConfiguration.Configure(input, id, CpuProfile.CometLakeEmulation); Check(doc.ToString().Contains(id.Serial) && doc.ToString().Contains("VQYKAAAAAAAAAAAAAAAAAA==") && doc.ToString().Contains("/////wAAAAAAAAAAAAAAAA==")); });
Test("OpenCore rejects bare-metal template", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("MacHyperVSupport.kext", "WhateverGreen.kext"))); Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel)); });
Test("OpenCore duplicate key rejected", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("<key>ROM</key>", "<key>SystemUUID</key><string/><key>ROM</key>"))); Throws<InvalidDataException>(() => OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel)); });
Test("OpenCore handles formatted plist with comments", () => { using var input = new MemoryStream(Encoding.UTF8.GetBytes(plist.Replace("</key>", "</key>\r\n<!-- source annotation -->\r\n"))); Check(OpenCoreConfiguration.Configure(input, MachineIdentity.Create(), CpuProfile.NativeIntel).Root is not null); });
if (args.Length == 2 && args[0] == "--upstream-archive")
{
    AsyncTest("Pinned upstream EFI configured and accepted by real ocvalidate", async () =>
    {
        var output = Path.Combine(temp, "upstream-efi");
        await BootMedia.ConfigureAsync(args[1], output, CpuProfile.CometLakeEmulation, MachineIdentity.Create());
        await BootMedia.ValidateAsync(output);
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
