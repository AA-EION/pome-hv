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
    private readonly PrivilegeBroker broker = new(Path.Combine(AppContext.BaseDirectory, "helper", "Pome.Helper.exe"));
    public void SetElevationOwner(nint window) => broker.OwnerWindow = window;
    private readonly SqliteJournal journal = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "operations.db"));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name == nameof(CanCancel)) PropertyChanged?.Invoke(this, new(nameof(CancelVisibility)));
    }
    private bool busy;
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(Idle)); Changed(nameof(CanEnableHyperV)); Changed(nameof(BusyVisibility)); NotifySelection(); } }
    public bool Idle => !Busy;
    public Microsoft.UI.Xaml.Visibility BusyVisibility => Busy ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool CanEnableHyperV => Idle && Host is not null && !HostCompatibility.HyperVEnabled(Host);
    private CancellationTokenSource? preparation;
    public bool CanCancel => preparation is not null;
    public Microsoft.UI.Xaml.Visibility CancelVisibility => CanCancel ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public void CancelPreparation() => preparation?.Cancel();
    private string status = "Inspect your computer to begin.";
    public string Status { get => status; private set { status = value; Changed(); } }
    public HostInfo? Host { get; private set; }
    public string HostTitle => Host is null ? "Your computer" : $"{Host.Manufacturer} {Host.Model}";
    public string HostSummary => Host is null ? "Hardware details appear after a read-only assessment." : $"{Host.Cpu}\n{(Host.TotalMemoryBytes + ResourcePolicy.GiB / 2) / ResourcePolicy.GiB} GB installed · {Host.AvailableMemoryBytes / ResourcePolicy.GiB} GB available\n{Host.Os} · build {Host.Build} · BIOS {Host.Bios}";
    public string Advice { get; private set; } = "Inspect this computer for a resource recommendation.";
    public ObservableCollection<CapabilityRow> Capabilities { get; } = [];
    public ObservableCollection<VmInfo> VirtualMachines { get; } = [];
    public ObservableCollection<VmInfo> FilteredMachines { get; } = [];
    private string searchText = "";
    public string SearchText { get => searchText; set { searchText = value; Changed(); FilterMachines(); } }
    private VmInfo? selectedVm;
    private bool changingList, changingSwitches;
    public VmInfo? SelectedVm
    {
        get => selectedVm;
        set
        {
            if (changingList) return;
            var changedId = selectedVm?.Id != value?.Id;
            selectedVm = value;
            if (changedId) { Details = null; Disks.Clear(); Checkpoints.Clear(); BootOrder.Clear(); PartitionableGpus.Clear(); SelectedCheckpoint = null; Changed(nameof(SelectedCheckpoint)); Changed(nameof(DetailsSummary)); }
            Changed(); NotifySelection();
        }
    }
    public VmDetails? Details { get; private set; }
    public ObservableCollection<VmDisk> Disks { get; } = [];
    public ObservableCollection<VmCheckpoint> Checkpoints { get; } = [];
    public ObservableCollection<string> BootOrder { get; } = [];
    public VmCheckpoint? SelectedCheckpoint { get; set; }
    public string EditName { get; set; } = "";
    public double EditCpuCount { get; set; } = 4;
    public double EditMemoryGiB { get; set; } = 8;
    public double ExpandedDiskGiB { get; set; } = 200;
    private string editSwitch = "Disconnected";
    public string EditSwitch { get => editSwitch; set { if (value is null || changingSwitches) return; editSwitch = value; Changed(); } }
    public string SelectedTitle => SelectedVm?.Name ?? "Select a virtual machine";
    public string SelectedSummary => SelectedVm is null ? "Choose a machine from the list." : $"{SelectedVm.State} · {SelectedVm.ProcessorCount} processors" + (Details is null ? "" : $" · {Details.StartupMemoryBytes / ResourcePolicy.GiB} GB static memory");
    public string OwnershipSummary => SelectedVm is null ? "" : SelectedVm.Managed ? "Managed by pome" : "External VM · read-only";
    public Microsoft.UI.Xaml.Visibility SelectionVisibility => SelectedVm is not null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility EmptySelectionVisibility => SelectedVm is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DetailsVisibility => Details is not null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility LoadDetailsVisibility => Details is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string LibraryCount => Host?.HyperVAvailable != true ? "Load Hyper-V to see your machines" : $"{VirtualMachines.Count} virtual {(VirtualMachines.Count == 1 ? "machine" : "machines")}";
    public string EmptyTitle => VirtualMachines.Count > 0 ? "No matching virtual machine." : "A place for your virtual Mac.";
    public string EmptyDescription => VirtualMachines.Count > 0 ? "Try another name in the search field, or clear it to return to your library." : "Create a macOS virtual machine, then manage its resources, recovery points and installation from one place.";
    public string SelectedCpu => SelectedVm is null ? "—" : $"{SelectedVm.ProcessorCount} virtual processors";
    public string SelectedMemory => Details is not null ? $"{Details.StartupMemoryBytes / ResourcePolicy.GiB} GB static memory" : "Load settings to see startup memory";
    public string SelectedUsage => SelectedVm is null ? "—" : $"{SelectedVm.CpuUsage}% CPU · {SelectedVm.MemoryAssigned / (double)ResourcePolicy.GiB:0.#} GB memory assigned";
    public string SelectedNetwork => Details is null ? "Load settings to see the connected switch" : Details.SwitchName ?? "Disconnected";
    public string DetailsSummary => Details is null ? "Load settings to inspect startup memory, disks, boot order and checkpoints." : $"{Details.CpuCount} processors · {Details.StartupMemoryBytes / ResourcePolicy.GiB} GB startup memory · {Details.SwitchName ?? "Disconnected"}\nSecure Boot: {Details.SecureBoot}\n{Details.ConfigurationLocation}";
    public bool CanInspectSelected => Idle && SelectedVm is not null;
    public bool CanStartSelected => Idle && SelectedVm?.CanStart == true;
    public bool CanShutdownSelected => Idle && SelectedVm?.CanShutdown == true;
    public bool CanPauseSelected => Idle && SelectedVm?.CanPause == true;
    public bool CanResumeSelected => Idle && SelectedVm?.CanResume == true;
    public bool CanSaveSelected => Idle && SelectedVm?.CanSave == true;
    public bool CanTurnOffSelected => Idle && SelectedVm?.CanTurnOff == true;
    public bool CanResetSelected => Idle && SelectedVm?.CanReset == true;
    public bool CanDeleteSelected => Idle && SelectedVm?.CanDelete == true;
    public bool CanOpenSelectedFolder => SelectedVm?.Managed == true;
    /// <summary>Hyper-V Administrators can list, power and connect without UAC; the token reflects membership after sign-in.</summary>
    public bool DirectHyperVAccess { get; } = HyperVAccess.CurrentUserIsMember();
    public Microsoft.UI.Xaml.Visibility GrantAccessVisibility => DirectHyperVAccess ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string AccessSummary => DirectHyperVAccess
        ? "You are in Hyper-V Administrators: listing, power actions and the console run without administrator prompts. Creation and disk tasks still ask for approval."
        : "Each Hyper-V action currently asks for administrator approval. Joining the built-in Hyper-V Administrators group lets pome list, start, stop and open your VMs without prompts. Members can control every VM on this computer.";
    /// <summary>The pome operation folder for the selected VM, from its ownership marker and the local journal.</summary>
    public string? SelectedVmFolder()
    {
        if (SelectedVm is not { Managed: true } vm || !Guid.TryParse(vm.Notes["pome:v1:".Length..], out var operation)) return null;
        var fromJournal = journal.ReadAll().Where(x => x.Id == operation && x.ResourceDirectory is not null).Select(x => x.ResourceDirectory!).FirstOrDefault(Directory.Exists);
        if (fromJournal is not null) return fromJournal;
        var fromDisk = Details?.VmId == vm.Id ? Details.Disks.Select(x => Path.GetDirectoryName(x.Path)).FirstOrDefault(x => x is not null && Directory.Exists(x)) : null;
        return fromDisk;
    }
    public bool CanManageSelected => Idle && SelectedVm?.CanConfigure == true;
    public bool CanEditSelected => CanManageSelected && Details?.VmId == SelectedVm?.Id;
    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(SelectedTitle), nameof(SelectedSummary), nameof(OwnershipSummary), nameof(CanInspectSelected), nameof(CanStartSelected), nameof(CanShutdownSelected), nameof(CanPauseSelected), nameof(CanResumeSelected), nameof(CanSaveSelected), nameof(CanTurnOffSelected), nameof(CanAddGpuPartition), nameof(CanRemoveGpuPartition), nameof(GpuPartitionSummary), nameof(CanResetSelected), nameof(CanDeleteSelected), nameof(CanOpenSelectedFolder), nameof(CanManageSelected), nameof(CanEditSelected), nameof(SelectionVisibility), nameof(EmptySelectionVisibility), nameof(DetailsVisibility), nameof(LoadDetailsVisibility), nameof(LibraryCount), nameof(SelectedCpu), nameof(SelectedMemory), nameof(SelectedUsage), nameof(SelectedNetwork), nameof(EmptyTitle), nameof(EmptyDescription) }) Changed(name);
    }
    private void FilterMachines()
    {
        var selection = SelectedVm?.Id;
        changingList = true;
        try
        {
            FilteredMachines.Clear();
            foreach (var vm in VirtualMachines.Where(x => x.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))) FilteredMachines.Add(vm);
        }
        finally { changingList = false; }
        SelectedVm = FilteredMachines.FirstOrDefault(x => x.Id == selection) ?? FilteredMachines.FirstOrDefault();
    }
    private void ReplaceMachines(IEnumerable<VmInfo> items)
    {
        VirtualMachines.Clear(); foreach (var vm in items) VirtualMachines.Add(vm);
        FilterMachines(); UpdateSuggestedName();
    }
    private void ApplyDetails(VmDetails details)
    {
        if (SelectedVm?.Id != details.VmId) return;
        Details = details;
        EditName = details.Name; EditCpuCount = details.CpuCount; EditMemoryGiB = details.StartupMemoryBytes / (double)ResourcePolicy.GiB; EditSwitch = details.SwitchName ?? "Disconnected";
        if (!Switches.Contains(EditSwitch)) Switches.Add(EditSwitch);
        Disks.Clear(); foreach (var disk in details.Disks) Disks.Add(disk);
        Checkpoints.Clear(); foreach (var checkpoint in details.Checkpoints) Checkpoints.Add(checkpoint);
        SelectedCheckpoint = null; Changed(nameof(SelectedCheckpoint));
        BootOrder.Clear(); foreach (var boot in details.BootOrder) BootOrder.Add(boot);
        PartitionableGpus.Clear(); foreach (var gpu in details.PartitionableGpus ?? []) PartitionableGpus.Add(gpu);
        SelectedPartitionGpu = PartitionableGpus.FirstOrDefault(); Changed(nameof(SelectedPartitionGpu));
        foreach (var name in new[] { nameof(EditName), nameof(EditCpuCount), nameof(EditMemoryGiB), nameof(EditSwitch), nameof(DetailsSummary) }) Changed(name);
        NotifySelection();
    }
    public ObservableCollection<string> Switches { get; } = ["Disconnected"];
    public ObservableCollection<PartitionableGpu> PartitionableGpus { get; } = [];
    public PartitionableGpu? SelectedPartitionGpu { get; set; }
    private bool SelectedIsTahoe => MacOsCatalog.Find(Details?.GuestMacOsVersion) is { Darwin: >= GpuPartitionPolicy.MinimumDarwin };
    public bool CanAddGpuPartition => CanEditSelected && SelectedIsTahoe && (Details?.GpuPartitions ?? []).Length == 0 && PartitionableGpus.Count > 0 && Details!.Checkpoints.Length == 0;
    public bool CanRemoveGpuPartition => CanEditSelected && (Details?.GpuPartitions ?? []).Length > 0;
    public string GpuPartitionSummary
    {
        get
        {
            if (Details is null) return "Load settings to see GPU partitions.";
            if ((Details.GpuPartitions ?? []).Length > 0)
                return "Attached: " + string.Join(", ", Details.GpuPartitions!.Select(path => Details.PartitionableGpus?.FirstOrDefault(g => string.Equals(g.InstancePath, path, StringComparison.OrdinalIgnoreCase))?.Name ?? path));
            var release = MacOsCatalog.Find(Details.GuestMacOsVersion);
            if (release is null) return "Unavailable: pome has no record of this VM's macOS version (VMs created before this release). Create a Tahoe VM to try GPU partitioning.";
            if (release.Darwin < GpuPartitionPolicy.MinimumDarwin) return $"Unavailable for {release.DisplayName}: GPU partitioning is limited to macOS 26 Tahoe VMs.";
            if (PartitionableGpus.Count == 0) return "This computer reports no partitionable GPU. GPU partitioning needs Windows 11 or Server 2022+ and a GPU driver that supports GPU-P.";
            return Details.Checkpoints.Length > 0 ? "Delete this VM's checkpoints before attaching a GPU partition." : "No GPU partition attached.";
        }
    }
    public ObservableCollection<string> AvailableNetworks { get; } = [];
    public ObservableCollection<string> Gpus { get; } = [];
    public ObservableCollection<JournalEntry> Activity { get; } = [];
    private readonly CreationDraft draft = new();
    public string VmName { get => draft.Name; set { draft.Name = value; Changed(); NotifyCreationReview(); } }
    private string storageRoot = DefaultStorageRoot();
    /// <summary>Documents is often synced by OneDrive, which is unsuitable for large virtual disks.</summary>
    public static string DefaultStorageRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var synced = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable).Where(x => !string.IsNullOrWhiteSpace(x))
            .Any(x => SafePaths.IsWithin(Path.GetFullPath(documents), Path.GetFullPath(x!)));
        return synced || string.IsNullOrWhiteSpace(documents)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Virtual Machines")
            : documents;
    }
    private void EnsureDefaultStorage()
    {
        var chosen = StorageRoot.Trim();
        if (string.Equals(chosen, DefaultStorageRoot(), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(chosen)) Directory.CreateDirectory(chosen);
    }
    public string StorageRoot { get => storageRoot; set { storageRoot = value; Changed(); } }
    public double CpuCount { get => draft.CpuCount; set { draft.CpuCount = value; Changed(); Changed(nameof(CpuHint)); NotifyCreationReview(); } }
    public string CpuHint => double.IsFinite(CpuCount) && !ResourcePolicy.IsPowerOfTwo((int)CpuCount)
        ? "Use 2, 4 or 8 virtual processors. macOS expects the processor count to divide evenly into cores, and odd counts are a known cause of early boot failures."
        : "macOS currently uses one active processor in compatibility mode; additional virtual processors stay idle until the multi-CPU driver fix ships. Hyper-V keeps one thread per core for macOS.";
    public double MemoryGiB { get => draft.MemoryGiB; set { draft.MemoryGiB = value; Changed(); NotifyCreationReview(); } }
    public double MaximumCpuCount => Math.Max(CpuCount, Host is null ? 32 : Math.Max(2, Math.Min(64, ResourcePolicy.Recommend(Host).MaximumCpuCount)));
    public double MaximumMemoryGiB => Math.Max(MemoryGiB, Host is null ? 64 : Math.Max(4, ResourcePolicy.Recommend(Host with { AvailableMemoryBytes = Host.TotalMemoryBytes }).MaximumMemoryGiB));
    private double diskGiB = 100;
    public double DiskGiB { get => diskGiB; set { diskGiB = value; Changed(); NotifyCreationReview(); } }
    public string CreationResources => $"{CpuCount:0} processors · {MemoryGiB:0} GB memory · {DiskGiB:0} GB disk";
    public string CreationMedia => AutomaticMedia
        ? $"{SelectedRelease.DisplayName} recovery from Apple · OpenCore ({SelectedRelease.SmbiosModel}){(GuestDriverPolicy.UsesUpdatedDriver(SelectedRelease) ? $" · MacHyperVSupport {UpstreamAssets.MacHyperVSupportVersion}" : "")} · boot logs enabled"
        : $"Your prepared VHDX boot media · {SelectedRelease.DisplayName}";
    public IReadOnlyList<MacOsRelease> Releases => MacOsCatalog.Releases;
    public MacOsRelease SelectedRelease
    {
        get => draft.Release;
        set
        {
            if (value is null || value == draft.Release) return;
            draft.Release = value;
            Changed(); Changed(nameof(VmName)); Changed(nameof(ReleaseGuidance)); Changed(nameof(CreationTitle)); NotifyCreationReview();
        }
    }
    public string CreationTitle => $"{SelectedRelease.DisplayName} · Hyper-V Generation 2";
    public string ReleaseGuidance
    {
        get
        {
            var release = SelectedRelease;
            var notes = new List<string> { release.EvidenceSummary + "." };
            if (GuestDriverPolicy.UsesUpdatedDriver(release)) notes.Add($"Pome replaces the pinned guest driver with MacHyperVSupport {UpstreamAssets.MacHyperVSupportVersion}, which carries the upstream macOS 26 PCI fix.");
            if (release.RequiresAvx2) notes.Add("Requires an AVX2 processor (Intel Haswell or newer).");
            if (release.Darwin < 19) notes.Add("Older recoveries are offered by Apple for this release only; the installer may require setting the guest clock if its certificate has expired.");
            if (release.LegacyBooter) notes.Add("Uses OpenCore's legacy boot quirks for OS X 10.7.");
            if (release.Darwin <= 14) notes.Add("Apple's recovery for this release uses an Apple Partition Map; OpenCore reads it through OpenPartitionDxe. Booting it from a disk has not been tested by pome yet.");
            if (Host is not null)
            {
                try { notes.Add(BootMedia.DetectIntelProfile(Host.CpuVendor, release) switch
                {
                    CpuProfile.NativeIntel => "This processor is used natively.",
                    CpuProfile.CometLakeEmulation => "This processor is presented as Comet Lake.",
                    CpuProfile.HaswellEmulation => "This processor is presented as Haswell.",
                    CpuProfile.AmdVanilla => "This AMD processor uses the AMD_Vanilla kernel patches, set to the VM's processor count. Use 2, 4 or 8 processors and keep the count fixed after creation.",
                    _ => "This processor is presented as Ivy Bridge."
                }); }
                catch (NotSupportedException e) { notes.Add(e.Message); }
            }
            return string.Join(" ", notes);
        }
    }
    public void NotifyCreationReview() { Changed(nameof(CreationResources)); Changed(nameof(CreationMedia)); }
    public string SelectedSwitch { get => draft.Network; set { if (value is null || changingSwitches) return; draft.Network = value; Changed(); } }
    private string efiSource = "", recoverySource = "";
    public string EfiSource { get => efiSource; set { if (efiSource == value) return; efiSource = value; EfiHash = ""; Changed(); } }
    private string efiHash = "", recoveryHash = "";
    public string EfiHash { get => efiHash; set { efiHash = value; Changed(); } }
    public string RecoverySource { get => recoverySource; set { if (recoverySource == value) return; recoverySource = value; RecoveryHash = ""; Changed(); } }
    public string RecoveryHash { get => recoveryHash; set { recoveryHash = value; Changed(); } }
    public bool StartAfterCreation { get; set; }
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
            NotifyCreationReview();
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
            if (Host!.HyperVAvailable == true) ReplaceMachines(await machines.ListAsync(timeout.Token));
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
        draft.ApplyHost(host);
        AvailableNetworks.Clear(); foreach (var name in host.Switches) AvailableNetworks.Add(name);
        changingSwitches = true;
        try
        {
            Switches.Clear(); Switches.Add("Disconnected"); foreach (var name in Host.Switches) Switches.Add(name);
            // Keep a stale, explicitly chosen switch visible so validation can explain it.
            if (!Switches.Contains(SelectedSwitch)) Switches.Add(SelectedSwitch);
            if (Details is not null && !Switches.Contains(EditSwitch)) Switches.Add(EditSwitch);
        }
        finally { changingSwitches = false; }
        NotifyDraft();
        var advice = ResourcePolicy.Recommend(Host); Advice = advice.Explanation; Changed(nameof(Advice)); Changed(nameof(ReleaseGuidance));
    }
    private void NotifyDraft()
    {
        Changed(nameof(CpuCount)); Changed(nameof(MemoryGiB)); Changed(nameof(SelectedSwitch));
        Changed(nameof(MaximumCpuCount)); Changed(nameof(MaximumMemoryGiB));
        NotifyCreationReview();
    }
    private void UpdateSuggestedName() { draft.ApplyMachines(VirtualMachines); Changed(nameof(VmName)); }
    public void UseRecommendedResources() { if (Host is not null) { draft.ApplyHost(Host, useRecommendations: true); NotifyDraft(); } }
    private async Task EnsureHostReadyAsync()
    {
        Status = "Checking current hardware, networks and VM names…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        ApplyHost(await discovery.DiscoverAsync(timeout.Token));
        if (Host!.HyperVAvailable != true)
        {
            Status = "Requesting administrator access to load Hyper-V networks…";
            var inspect = await broker.ExecuteAsync(new(OperationKind.Inspect));
            if (inspect.Host is not null) ApplyHost(inspect.Host);
            if (inspect.VirtualMachines is not null)
            {
                ReplaceMachines(inspect.VirtualMachines);
            }
            if (!inspect.Success && Host.HyperVAvailable != true)
                throw new InvalidOperationException(inspect.Message);
        }
        else
        {
            ReplaceMachines(await machines.ListAsync(timeout.Token));
        }
        UpdateSuggestedName();
    }
    public CreatePlan BuildPlan()
    {
        if (Host is null) throw new InvalidOperationException("Inspect this computer before creating a VM.");
        var plan = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), CreationDraft.WholeNumber(CpuCount, "processors"), CreationDraft.WholeNumber(MemoryGiB, "memory"), CreationDraft.WholeNumber(DiskGiB, "disk capacity"),
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, EfiSource.Trim(), EfiHash.Trim(),
            string.IsNullOrWhiteSpace(RecoverySource) ? null : RecoverySource.Trim(), RecoveryHash.Trim(), SelectedRelease.Version, AcceptLicense, AcceptFirmware);
        PlanValidator.RequireValid(plan, Host, preparing: true); return plan;
    }
    public async Task<CreatePlan> PreparePlanAsync()
    {
        if (activeRefresh is { IsCompleted: false }) await activeRefresh;
        if (Busy) throw new InvalidOperationException("Wait for the current operation to finish.");
        if (!AcceptLicense || !AcceptFirmware) throw new InvalidOperationException("Review the license and guest security choices before preparing media.");
        EnsureDefaultStorage();
        Busy = true;
        try
        {
            await EnsureHostReadyAsync();
        }
        finally
        {
            Busy = false;
        }
        if (!AutomaticMedia)
        {
            var manual = BuildPlan();
            CreationPreflight.RequireUniqueName(manual.Name, VirtualMachines);
            CreationPreflight.RequireMedia(manual);
            CreationPreflight.RequireStorage(manual, checked(new FileInfo(manual.EfiSource).Length + (manual.RecoverySource is null ? 0 : new FileInfo(manual.RecoverySource).Length)));
            return manual;
        }
        var preliminary = new CreatePlan(1, Guid.NewGuid(), VmName.Trim(), StorageRoot.Trim(), CreationDraft.WholeNumber(CpuCount, "processors"), CreationDraft.WholeNumber(MemoryGiB, "memory"), CreationDraft.WholeNumber(DiskGiB, "disk capacity"),
            SelectedSwitch == "Disconnected" ? null : SelectedSwitch, "pending", UpstreamAssets.HyperVEfi.Sha256, null, null, SelectedRelease.Version, true, true);
        PlanValidator.RequireValid(preliminary, Host, preparing: true);
        CreationPreflight.RequireUniqueName(preliminary.Name, VirtualMachines);
        // Conservative estimate; the elevated helper rechecks actual media sizes.
        CreationPreflight.RequireStorage(preliminary, 8L * (long)ResourcePolicy.GiB);
        if (preliminary.SwitchName is null) throw new InvalidOperationException("Choose a virtual switch with internet access for Apple recovery.");
        var release = SelectedRelease;
        BootMedia.DetectIntelProfile(Host!.CpuVendor, release);
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
            var media = await BootMedia.PrepareAsync(cache, Host!.CpuVendor, null, preparation.Token, progress, release);
            var validation = Path.Combine(cache, "validate-" + Guid.NewGuid().ToString("N"));
            try
            {
                await BootMedia.ConfigureAsync(media.Archive, validation, media.CpuProfile, MachineIdentity.Create(), preparation.Token, release, media.DriverArchive, media.AmdPatches, preliminary.CpuCount);
                await BootMedia.ValidateAsync(validation, preparation.Token);
            }
            finally { try { if (Directory.Exists(validation)) Directory.Delete(validation, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            CreationPreflight.RequireStorage(preliminary, checked(new FileInfo(media.Archive).Length + 2 * new FileInfo(media.RecoveryDmg).Length + 2 * RecoveryDisk.GetVirtualSize(media.RecoveryDmg) + 512L * 1024 * 1024));
            Status = "Recovery verified for disk-based boot and OpenCore validated. Review the creation plan.";
            journal.Write(new(preliminary.OperationId, "Create", "MediaPrepared", "Recovery verified and OpenCore configuration validated. Ready for administrator creation.", DateTimeOffset.UtcNow, false, resourceDir));
            return preliminary with { EfiSource = media.Archive, BuildEfiFromArchive = true, CpuProfile = media.CpuProfile, RecoveryDmg = media.RecoveryDmg, RecoveryDmgSha256 = media.DmgHash, RecoveryChunklist = media.Chunklist, RecoveryChunklistSha256 = media.ChunklistHash,
                HyperVDriverArchive = media.DriverArchive, HyperVDriverSha256 = media.DriverHash, AmdPatches = media.AmdPatches, AmdPatchesSha256 = media.AmdPatchesHash };
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
            var id = request.Plan?.OperationId ?? request.OperationId ?? Guid.NewGuid();
            request = request with { OperationId = id };
            var resourceDir = request.Plan is not null ? Path.Combine(Path.GetFullPath(request.Plan.StorageRoot), "pome-" + id.ToString("N"))
                : request.Kind is OperationKind.Export or OperationKind.ExportBootLogs && request.Destination is not null ? Path.Combine(Path.GetFullPath(request.Destination), "pome-" + id.ToString("N")) : null;
            var direct = DirectHyperVAccess && HyperVAccess.IsLifecycle(request.Kind);
            journal.Write(new(id, request.Kind.ToString(), "Requested", direct ? "Applying through Hyper-V Administrators access." : "Waiting for elevation and helper completion.", DateTimeOffset.UtcNow, false, resourceDir));
            Status = direct ? "Applying…" : "Waiting for Windows approval, then applying the reviewed operation…"; LoadActivity();
            try
            {
                var result = direct ? await HyperVAccess.RunLifecycleAsync(new PowerShellRunner(), request) : await broker.ExecuteAsync(request);
                if (HyperVAccess.IsLifecycle(request.Kind) || request.Kind == OperationKind.Create) lastAction = DateTimeOffset.UtcNow;
                outcome = result;
                Status = result.Message;
                if (result.Host is not null) ApplyHost(result.Host);
                if (result.VirtualMachines is not null)
                    ReplaceMachines(result.VirtualMachines);
                if (result.Details is not null) ApplyDetails(result.Details);
                else if (request.Kind is OperationKind.Configure or OperationKind.RestoreCheckpoint or OperationKind.ResizeDisk) { Details = null; Changed(nameof(DetailsSummary)); NotifySelection(); }
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
    private DateTimeOffset lastAction = DateTimeOffset.MinValue;
    private bool polling;
    /// <summary>
    /// Keeps VM state current without a manual refresh. Runs only when listing needs no elevation,
    /// and only while a guest is running/changing state or shortly after an action.
    /// </summary>
    public async Task PollAsync()
    {
        if (polling || Busy || Host?.HyperVAvailable != true) return;
        var active = VirtualMachines.Any(x => x.State != "Off" && x.State != "Saved") || DateTimeOffset.UtcNow - lastAction < TimeSpan.FromSeconds(60);
        if (!active) return;
        polling = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var items = await machines.ListAsync(timeout.Token);
            if (!Busy) ReplaceMachines(items);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException) { /* Polling is best effort; explicit refresh reports errors. */ }
        finally { polling = false; }
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
