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

Test("16 GB host preserves 6 GB installed headroom", () => Check(ResourcePolicy.Recommend(Host()).MaximumMemoryGiB == 10));
Test("Memory pressure blocks creation", () => Check(!ResourcePolicy.Recommend(Host(free: 6)).CanCreate));
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
Test("Machine identities unique and locally administered", () => { var a = MachineIdentity.Create(); var b = MachineIdentity.Create(); Check(a.SystemUuid != b.SystemUuid && a.Serial != b.Serial && a.Rom.Length == 6 && (a.Rom[0] & 3) == 2); });
AsyncTest("IPC frame round trip", async () => { using var stream = new MemoryStream(); await PipeProtocol.WriteAsync(stream, new OperationRequest(OperationKind.Start, VmId: Guid.NewGuid()), default); stream.Position = 0; Check((await PipeProtocol.ReadAsync<OperationRequest>(stream, default)).Kind == OperationKind.Start); });
AsyncTest("IPC oversized frame rejected before allocation", async () => { using var stream = new MemoryStream(BitConverter.GetBytes(int.MaxValue)); await Reject<InvalidDataException>(async () => await PipeProtocol.ReadAsync<OperationRequest>(stream, default)); });
AsyncTest("IPC truncated frame rejected", async () => { using var stream = new MemoryStream([4, 0, 0, 0, 1]); await Reject<EndOfStreamException>(async () => await PipeProtocol.ReadAsync<OperationRequest>(stream, default)); });
AsyncTest("Incorrect hash rejected", async () => { var path = Path.Combine(temp, "hash"); await File.WriteAllTextAsync(path, "changed"); await Reject<InvalidDataException>(() => SafePaths.VerifyHashAsync(path, new('0', 64))); });

var bytes = Encoding.UTF8.GetBytes("a small pinned upstream asset");
var asset = new DownloadAsset(new("https://github.com/owner/repo/releases/download/v1/file"), Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, "fixture");
AsyncTest("Download verified and atomically promoted", async () => { using var client = new HttpClient(new FakeHttp(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })); var path = Path.Combine(temp, "download"); await new VerifiedDownloader(client).DownloadAsync(asset, path); Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(path + ".part")); });
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
