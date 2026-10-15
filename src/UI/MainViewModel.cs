using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pome.Core;
using Pome.Infrastructure;

namespace Pome.UI;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IHostDiscovery discovery = new WindowsHostDiscovery(new PowerShellRunner());
    private readonly IVirtualMachineManager machines = new HyperVManager(new PowerShellRunner());
    private readonly IPrivilegeBroker broker = new PrivilegeBroker(Path.Combine(AppContext.BaseDirectory, "helper", "Pome.Helper.exe"));
    private readonly SqliteJournal journal = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "operations.db"));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private bool busy;
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(Idle)); } }
    public bool Idle => !Busy;
    private string status = "Inspect your computer to begin.";
    public string Status { get => status; private set { status = value; Changed(); } }
    public HostInfo? Host { get; private set; }
    public string HostTitle => Host is null ? "Your computer" : $"{Host.Manufacturer} {Host.Model}";
    public string HostSummary => Host is null ? "Hardware details appear after a read-only assessment." : $"{Host.Cpu}\n{Host.TotalMemoryBytes / ResourcePolicy.GiB} GB installed · {Host.AvailableMemoryBytes / ResourcePolicy.GiB} GB available\n{Host.Os} · build {Host.Build} · BIOS {Host.Bios}";
    public string Advice { get; private set; } = "Inspect this computer for a resource recommendation.";
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];
    public ObservableCollection<VmInfo> VirtualMachines { get; } = [];
    public ObservableCollection<string> Switches { get; } = ["Disconnected"];
    public ObservableCollection<string> Gpus { get; } = [];
    public ObservableCollection<JournalEntry> Activity { get; } = [];
    public string VmName { get; set; } = "Sequoia";
    public string StorageRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public double CpuCount { get; set; } = 4;
    public double MemoryGiB { get; set; } = 8;
    public double DiskGiB { get; set; } = 100;
    public string SelectedSwitch { get; set; } = "Disconnected";
    public string EfiSource { get; set; } = "";
    public string EfiHash { get; set; } = "";
    public string RecoverySource { get; set; } = "";
    public string RecoveryHash { get; set; } = "";
    public bool AcceptLicense { get; set; }
    public bool AcceptFirmware { get; set; }
    public bool AutomaticMedia { get; set; } = true;
    public string PythonPath { get; set; } = "";

    public MainViewModel() => LoadActivity();
    public void LoadActivity() { Activity.Clear(); foreach (var item in journal.ReadAll()) Activity.Add(item); }
    public async Task RefreshAsync()
    {
        await Guard(async () =>
        {
            Status = "Reading hardware and Hyper-V configuration…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Host = await discovery.DiscoverAsync(timeout.Token);
            Changed(nameof(HostTitle)); Changed(nameof(HostSummary));
            Capabilities.Clear(); foreach (var item in HostCompatibility.Assess(Host)) Capabilities.Add(new(item.Name, Display(item.Status), item.Detail));
            Gpus.Clear(); foreach (var gpu in Host.Gpus) Gpus.Add($"{gpu.Name}\nDriver {gpu.DriverVersion} · {GpuSafety.Evaluate(Host, gpu).Detail}");
            Switches.Clear(); Switches.Add("Disconnected"); foreach (var name in Host.Switches) Switches.Add(name);
            SelectedSwitch = Host.Switches.Contains("Default Switch") ? "Default Switch" : "Disconnected"; Changed(nameof(SelectedSwitch));
            var advice = ResourcePolicy.Recommend(Host); Advice = advice.Explanation; Changed(nameof(Advice));
            CpuCount = advice.CpuCount; MemoryGiB = Math.Max(8, advice.MemoryGiB); Changed(nameof(CpuCount)); Changed(nameof(MemoryGiB));
            VirtualMachines.Clear();
            if (Host.HyperVAvailable == true) foreach (var vm in await machines.ListAsync(timeout.Token)) VirtualMachines.Add(vm);
            Status = Host.Warnings.Length > 0 ? string.Join(" ", Host.Warnings) : $"Assessment complete. {VirtualMachines.Count} virtual machines found. Guest compatibility remains unverified.";
            LoadActivity();
        });
    }
    public CreatePlan BuildPlan()
    {
        if (Host is null) throw new InvalidOperationException("Inspect this computer before creating a VM.");
        var plan = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), (int)CpuCount, (int)MemoryGiB, (int)DiskGiB,
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, EfiSource.Trim(), EfiHash.Trim(),
            string.IsNullOrWhiteSpace(RecoverySource) ? null : RecoverySource.Trim(), RecoveryHash.Trim(), "15", AcceptLicense, AcceptFirmware);
        PlanValidator.RequireValid(plan, Host); return plan;
    }
    public async Task<CreatePlan> PreparePlanAsync()
    {
        if (!AutomaticMedia) return BuildPlan();
        if (Host is null) throw new InvalidOperationException("Inspect this computer first.");
        if (!AcceptLicense || !AcceptFirmware) throw new InvalidOperationException("Review the license and guest security choices before preparing media.");
        var preliminary = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), (int)CpuCount, (int)MemoryGiB, (int)DiskGiB,
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, "pending", UpstreamAssets.HyperVEfi.Sha256, null, null, "15", true, true);
        PlanValidator.RequireValid(preliminary, Host);
        Busy = true;
        try
        {
            Status = "Downloading pinned EFI tools and Apple recovery; the upstream utility verifies the recovery chunklist. This may take several minutes…";
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "media");
            var media = await BootMedia.PrepareAsync(cache, Host.CpuVendor, PythonPath);
            var validation = Path.Combine(cache, "validate-" + Guid.NewGuid().ToString("N"));
            await BootMedia.ConfigureAsync(media.Archive, validation, media.CpuProfile, MachineIdentity.Create());
            await BootMedia.ValidateAsync(validation);
            Status = "Recovery verified and OpenCore configuration validated. Review the creation plan.";
            return preliminary with { EfiSource = media.Archive, BuildEfiFromArchive = true, CpuProfile = media.CpuProfile, RecoveryDmg = media.RecoveryDmg, RecoveryDmgSha256 = media.DmgHash, RecoveryChunklist = media.Chunklist, RecoveryChunklistSha256 = media.ChunklistHash };
        }
        finally { Busy = false; }
    }
    public async Task ExecuteAsync(OperationRequest request)
    {
        await Guard(async () =>
        {
            var id = request.Plan?.OperationId ?? Guid.NewGuid();
            journal.Write(new(id, request.Kind.ToString(), "Requested", "Waiting for elevation and helper completion.", DateTimeOffset.UtcNow, false));
            Status = "Waiting for Windows approval, then applying the reviewed operation…"; LoadActivity();
            try
            {
                var result = await broker.ExecuteAsync(request);
                Status = result.Message;
                journal.Write(new(id, request.Kind.ToString(), result.Success ? "Completed" : "Needs attention", result.Message, DateTimeOffset.UtcNow, result.Success));
            }
            catch (Exception e)
            {
                journal.Write(new(id, request.Kind.ToString(), "Needs attention", e.Message, DateTimeOffset.UtcNow, false)); throw;
            }
            finally { LoadActivity(); }
        });
    }
    public async Task Guard(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        try { await action(); } catch (Exception e) { Status = Redaction.Clean(e.Message); } finally { Busy = false; }
    }
    public void Report(string message) => Status = message;
    private static string Display(CapabilityStatus status) => status switch
    {
        CapabilityStatus.DetectedUntested => "Not yet validated", CapabilityStatus.HostOsRestricted => "Windows restriction",
        CapabilityStatus.HardwareIneligible => "Hardware ineligible", _ => status.ToString()
    };
}
public sealed record CapabilityRow(string Name, string State, string Detail);
