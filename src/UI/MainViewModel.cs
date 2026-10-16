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
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(Idle)); Changed(nameof(CanEnableHyperV)); } }
    public bool Idle => !Busy;
    public bool CanEnableHyperV => Idle && Host is not null && !HostCompatibility.HyperVEnabled(Host);
    private CancellationTokenSource? preparation;
    public bool CanCancel => preparation is not null;
    public void CancelPreparation() => preparation?.Cancel();
    private string status = "Inspect your computer to begin.";
    public string Status { get => status; private set { status = value; Changed(); } }
    public HostInfo? Host { get; private set; }
    public string HostTitle => Host is null ? "Your computer" : $"{Host.Manufacturer} {Host.Model}";
    public string HostSummary => Host is null ? "Hardware details appear after a read-only assessment." : $"{Host.Cpu}\n{(Host.TotalMemoryBytes + ResourcePolicy.GiB / 2) / ResourcePolicy.GiB} GB installed · {Host.AvailableMemoryBytes / ResourcePolicy.GiB} GB available\n{Host.Os} · build {Host.Build} · BIOS {Host.Bios}";
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
    private bool acceptLicense;
    public bool AcceptLicense { get => acceptLicense; set { if (acceptLicense == value) return; acceptLicense = value; Changed(); } }
    private bool acceptFirmware;
    public bool AcceptFirmware { get => acceptFirmware; set { if (acceptFirmware == value) return; acceptFirmware = value; Changed(); } }
    private bool automaticMedia = true;
    public bool AutomaticMedia
    {
        get => automaticMedia;
        set
        {
            if (automaticMedia == value) return;
            automaticMedia = value;
            Changed();
            Changed(nameof(AutomaticMediaVisibility));
            Changed(nameof(ManualMediaVisibility));
        }
    }
    public Microsoft.UI.Xaml.Visibility AutomaticMediaVisibility => AutomaticMedia ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ManualMediaVisibility => AutomaticMedia ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string PythonPath { get; set; } = PythonLocator.Find();

    public MainViewModel() => LoadActivity();
    public void LoadActivity()
    {
        journal.ReconcileMarkers();
        Activity.Clear();
        foreach (var item in journal.ReadAll()) Activity.Add(item);
    }
    private Task? activeRefresh;
    public Task RefreshAsync()
    {
        if (activeRefresh is { IsCompleted: false }) return activeRefresh;
        return activeRefresh = Guard(async () =>
        {
            Status = "Reading hardware and Hyper-V configuration…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ApplyHost(await discovery.DiscoverAsync(timeout.Token));
            VirtualMachines.Clear();
            if (Host!.HyperVAvailable == true) foreach (var vm in await machines.ListAsync(timeout.Token)) VirtualMachines.Add(vm);
            Status = Host.Warnings.Length > 0 ? string.Join(" ", Host.Warnings) : $"Assessment complete. {VirtualMachines.Count} virtual machines found. Guest compatibility remains unverified.";
            LoadActivity();
        });
    }
    private void ApplyHost(HostInfo host)
    {
        Host = host;
        Changed(nameof(CanEnableHyperV));
        Changed(nameof(HostTitle)); Changed(nameof(HostSummary));
        Capabilities.Clear(); foreach (var item in HostCompatibility.Assess(Host)) Capabilities.Add(new(item.Name, Display(item.Status), item.Detail));
        Gpus.Clear(); foreach (var gpu in Host.Gpus) Gpus.Add($"{gpu.Name}\nDriver {gpu.DriverVersion} · {GpuSafety.Evaluate(Host, gpu).Detail}");
        Switches.Clear(); Switches.Add("Disconnected"); foreach (var name in Host.Switches) Switches.Add(name);
        SelectedSwitch = Host.Switches.Contains("Default Switch") ? "Default Switch" : (Host.Switches.FirstOrDefault() ?? "Disconnected");
        Changed(nameof(SelectedSwitch));
        var advice = ResourcePolicy.Recommend(Host); Advice = advice.Explanation; Changed(nameof(Advice));
        CpuCount = advice.CpuCount; MemoryGiB = Math.Max(4, advice.MemoryGiB); Changed(nameof(CpuCount)); Changed(nameof(MemoryGiB));
    }
    private async Task EnsureHostReadyAsync()
    {
        if (Host is null)
        {
            Status = "Reading hardware and Hyper-V configuration…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            ApplyHost(await discovery.DiscoverAsync(timeout.Token));
        }
        if (Host!.HyperVAvailable != true)
        {
            Status = "Requesting administrator access to load Hyper-V networks…";
            var inspect = await broker.ExecuteAsync(new(OperationKind.Inspect));
            if (inspect.Host is not null) ApplyHost(inspect.Host);
            if (inspect.VirtualMachines is not null)
            {
                VirtualMachines.Clear();
                foreach (var vm in inspect.VirtualMachines) VirtualMachines.Add(vm);
            }
            if (!inspect.Success && Host.HyperVAvailable != true)
                throw new InvalidOperationException(inspect.Message);
        }
        if (SelectedSwitch == "Disconnected" && Host.Switches.Length > 0)
        {
            SelectedSwitch = Host.Switches.Contains("Default Switch") ? "Default Switch" : Host.Switches[0];
            Changed(nameof(SelectedSwitch));
        }
    }
    public CreatePlan BuildPlan()
    {
        if (Host is null) throw new InvalidOperationException("Inspect this computer before creating a VM.");
        var plan = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), (int)CpuCount, (int)MemoryGiB, (int)DiskGiB,
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, EfiSource.Trim(), EfiHash.Trim(),
            string.IsNullOrWhiteSpace(RecoverySource) ? null : RecoverySource.Trim(), RecoveryHash.Trim(), "15", AcceptLicense, AcceptFirmware);
        PlanValidator.RequireValid(plan, Host, preparing: true); return plan;
    }
    public async Task<CreatePlan> PreparePlanAsync()
    {
        if (activeRefresh is { IsCompleted: false }) await activeRefresh;
        if (Busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        if (!AcceptLicense || !AcceptFirmware) throw new InvalidOperationException("Review the license and guest security choices before preparing media.");
        Busy = true;
        try
        {
            await EnsureHostReadyAsync();
        }
        finally
        {
            Busy = false;
        }
        if (!AutomaticMedia) return BuildPlan();
        var preliminary = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), (int)CpuCount, (int)MemoryGiB, (int)DiskGiB,
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, "pending", UpstreamAssets.HyperVEfi.Sha256, null, null, "15", true, true);
        PlanValidator.RequireValid(preliminary, Host, preparing: true);
        if (preliminary.SwitchName is null) throw new InvalidOperationException("Choose a virtual switch with internet access for Apple recovery.");
        var resourceDir = SafePaths.NewOperationDirectory(preliminary.StorageRoot, preliminary.OperationId);
        Busy = true;
        preparation = new CancellationTokenSource(); Changed(nameof(CanCancel));
        journal.Write(new(preliminary.OperationId, "Create", "PreparingMedia", "Downloading or verifying pinned EFI tools and Apple recovery media.", DateTimeOffset.UtcNow, false, resourceDir));
        LoadActivity();
        try
        {
            Status = "Preparing Pome's isolated Python environment, pinned EFI tools, and Apple recovery media…";
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "media");
            var progress = new Progress<string>(message => Status = message);
            var media = await BootMedia.PrepareAsync(cache, Host!.CpuVendor, null, preparation.Token, progress);
            var validation = Path.Combine(cache, "validate-" + Guid.NewGuid().ToString("N"));
            await BootMedia.ConfigureAsync(media.Archive, validation, media.CpuProfile, MachineIdentity.Create(), preparation.Token);
            await BootMedia.ValidateAsync(validation, preparation.Token);
            Status = "Recovery verified and OpenCore configuration validated. Review the creation plan.";
            journal.Write(new(preliminary.OperationId, "Create", "MediaPrepared", "Recovery verified and OpenCore configuration validated. Ready for administrator creation.", DateTimeOffset.UtcNow, false, resourceDir));
            return preliminary with { EfiSource = media.Archive, BuildEfiFromArchive = true, CpuProfile = media.CpuProfile, RecoveryDmg = media.RecoveryDmg, RecoveryDmgSha256 = media.DmgHash, RecoveryChunklist = media.Chunklist, RecoveryChunklistSha256 = media.ChunklistHash };
        }
        catch (OperationCanceledException)
        {
            const string cancelMessage = "Preparation cancelled. No VM was created. Completed recovery files will be rechecked before reuse; an incomplete Apple download restarts on retry.";
            journal.Write(new(preliminary.OperationId, "Create", "Cancelled", cancelMessage, DateTimeOffset.UtcNow, true, resourceDir));
            throw new InvalidOperationException(cancelMessage);
        }
        catch (Exception e)
        {
            journal.Write(new(preliminary.OperationId, "Create", "Needs attention", e.Message, DateTimeOffset.UtcNow, false, resourceDir));
            throw;
        }
        finally { preparation.Dispose(); preparation = null; Changed(nameof(CanCancel)); Busy = false; LoadActivity(); }
    }
    public async Task<OperationResult?> ExecuteAsync(OperationRequest request)
    {
        OperationResult? outcome = null;
        await Guard(async () =>
        {
            var id = request.Plan?.OperationId ?? Guid.NewGuid();
            var resourceDir = request.Plan is not null ? Path.Combine(Path.GetFullPath(request.Plan.StorageRoot), "pome-" + id.ToString("N")) : null;
            journal.Write(new(id, request.Kind.ToString(), "Requested", "Waiting for elevation and helper completion.", DateTimeOffset.UtcNow, false, resourceDir));
            Status = "Waiting for Windows approval, then applying the reviewed operation…"; LoadActivity();
            try
            {
                var result = await broker.ExecuteAsync(request);
                outcome = result;
                Status = result.Message;
                if (result.Host is not null) ApplyHost(result.Host);
                if (result.VirtualMachines is not null)
                { VirtualMachines.Clear(); foreach (var vm in result.VirtualMachines) VirtualMachines.Add(vm); }
                var stage = result.Stage is "NeedsAttention" ? "Needs attention" : result.Stage ?? (result.Success ? "Completed" : "Needs attention");
                journal.Write(new(id, request.Kind.ToString(), stage, result.Message, DateTimeOffset.UtcNow, result.Success, result.ResourceDirectory ?? resourceDir));
            }
            catch (Exception e)
            {
                var clean = Redaction.Clean(e.Message);
                outcome = new(false, clean, ResourceDirectory: resourceDir, Stage: "NeedsAttention");
                journal.Write(new(id, request.Kind.ToString(), "Needs attention", clean, DateTimeOffset.UtcNow, false, resourceDir)); throw;
            }
            finally { LoadActivity(); }
        });
        return outcome;
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
