using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pome.Core;
using Pome.Infrastructure;

namespace Pome.UI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private bool ready;
    private static string ThemePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "theme.txt");
    public MainWindow()
    {
        InitializeComponent(); Root.DataContext = ViewModel;
        ViewModel.SetElevationOwner(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true; SetTitleBar(AppTitleBar);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min(1180, area.Width - 48); var height = Math.Min(850, area.Height - 48);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        Root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 800;
        Root.Loaded += async (_, _) => { if (ViewModel.Host is null && ViewModel.Idle) await ViewModel.RefreshAsync(); };
        // Keep VM state current after power actions and while guests run (no UAC: listing only).
        var poll = DispatcherQueue.CreateTimer();
        poll.Interval = TimeSpan.FromSeconds(5);
        poll.Tick += async (_, _) => await ViewModel.PollAsync();
        poll.Start();
        Closed += (_, _) => poll.Stop();
        Root.SizeChanged += (_, _) =>
        {
            var narrow = Root.ActualWidth < 900;
            LibraryWorkspace.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(232);
            LibraryWorkspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            LibraryWorkspace.RowDefinitions[0].Height = narrow ? new GridLength(172) : new GridLength(1, GridUnitType.Star);
            LibraryWorkspace.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(MachineDetailPane, narrow ? 0 : 1); Grid.SetRow(MachineDetailPane, narrow ? 1 : 0);
        };
        try
        {
            var theme = File.Exists(ThemePath) ? File.ReadAllText(ThemePath).Trim() : "Default";
            ThemeChoice.SelectedIndex = theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            Root.RequestedTheme = ThemeChoice.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.Report("Could not load appearance preference. Using the Windows theme."); }
        ready = true;
        ShowCreateStep(0);
        UpdateCaptionTheme();
    }
    private void UpdateCaptionTheme()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (LibraryPanel is null) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        LibraryPanel.Visibility = tag == "Library" ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = tag == "Create" ? Visibility.Visible : Visibility.Collapsed;
        ComputerPanel.Visibility = tag == "Computer" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = tag == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        NetworksPanel.Visibility = tag == "Networks" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ChangeTheme(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        Root.RequestedTheme = ThemeChoice.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        try { Directory.CreateDirectory(Path.GetDirectoryName(ThemePath)!); File.WriteAllText(ThemePath, Root.RequestedTheme.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.Report("Theme changed for this session; the preference could not be saved."); }
    }
    private void GoCreate(object sender, RoutedEventArgs e) => Navigation.SelectedItem = Navigation.MenuItems[1];
    private void ChangeCreateStep(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralStep is not null) ShowCreateStep(CreateSteps.SelectedIndex);
    }
    private void ShowCreateStep(int step)
    {
        GeneralStep.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResourcesStep.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        MediaStep.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReviewStep.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PreviousStepButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextStepButton.Visibility = step < 3 ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.NotifyCreationReview();
    }
    private async void NextCreateStep(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CreateSteps.SelectedIndex == 0)
            {
                if (!PlanValidator.IsValidName(ViewModel.VmName.Trim())) throw new InvalidDataException("Use 1–64 letters, digits, spaces, dots, underscores or hyphens for the VM name.");
                CreationPreflight.RequireUniqueName(ViewModel.VmName.Trim(), ViewModel.VirtualMachines);
                var folder = SafePaths.RequireLocal(ViewModel.StorageRoot.Trim(), false);
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder for the VM.");
            }
            else if (CreateSteps.SelectedIndex == 1)
            {
                var cpu = CreationDraft.WholeNumber(ViewModel.CpuCount, "processors");
                var ram = CreationDraft.WholeNumber(ViewModel.MemoryGiB, "memory");
                var disk = CreationDraft.WholeNumber(ViewModel.DiskGiB, "disk capacity");
                if (cpu is < 2 or > 64 || ram is < 4 or > 128 || disk is < 64 or > 2048) throw new InvalidDataException("Choose 2–64 processors, 4–128 GB RAM and 64–2048 GB disk capacity.");
                if (ViewModel.Host is { } host)
                {
                    var budget = ResourcePolicy.Recommend(host with { AvailableMemoryBytes = host.TotalMemoryBytes });
                    if (cpu > budget.MaximumCpuCount || ram > budget.MaximumMemoryGiB) throw new InvalidDataException("These resources exceed this computer's safe budget. Use recommended resources or reduce the allocation.");
                }
            }
            if (CreateSteps.SelectedIndex < 3) CreateSteps.SelectedIndex++;
        }
        catch (Exception ex) { await ShowNoticeAsync("Check this step", ex.Message); }
    }
    private void PreviousCreateStep(object sender, RoutedEventArgs e) { if (CreateSteps.SelectedIndex > 0) CreateSteps.SelectedIndex--; }
    private async void Inspect(object sender, RoutedEventArgs e) { Navigation.SelectedItem = Navigation.MenuItems[2]; await ViewModel.RefreshAsync(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void InspectAsAdministrator(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.ExecuteAsync(new(OperationKind.Inspect));
        if (result is { Success: false }) await ShowNoticeAsync("Hyper-V inspection needs attention", result.Message);
    }
    private void CancelPreparation(object sender, RoutedEventArgs e) => ViewModel.CancelPreparation();
    private void UseRecommendedResources(object sender, RoutedEventArgs e) => ViewModel.UseRecommendedResources();
    private async void BrowseStorage(object sender, RoutedEventArgs e)
    {
        await ViewModel.Guard(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) ViewModel.StorageRoot = SafePaths.RequireLocal(folder.Path);
        });
    }
    private async void BrowseEfi(object sender, RoutedEventArgs e) => await BrowseMediaAsync(recovery: false);
    private async void BrowseRecovery(object sender, RoutedEventArgs e) => await BrowseMediaAsync(recovery: true);
    private Task BrowseMediaAsync(bool recovery) => ViewModel.Guard(async () =>
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".vhdx");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var path = SafePaths.RequireLocal(file.Path);
        if (recovery) ViewModel.RecoverySource = path; else ViewModel.EfiSource = path;
    });
    private async void CalculateEfiHash(object sender, RoutedEventArgs e) => await CalculateHashAsync(recovery: false);
    private async void CalculateRecoveryHash(object sender, RoutedEventArgs e) => await CalculateHashAsync(recovery: true);
    private Task CalculateHashAsync(bool recovery) => ViewModel.Guard(async () =>
    {
        var path = SafePaths.RequireLocal(recovery ? ViewModel.RecoverySource : ViewModel.EfiSource);
        ViewModel.Report("Calculating SHA-256 of the selected disk…");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (recovery) ViewModel.RecoveryHash = hash; else ViewModel.EfiHash = hash;
        ViewModel.Report("Digest calculated. Compare it with your trusted build record before creating the VM.");
    });
    private async Task<bool> Confirm(string title, string detail, string action)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async Task ShowNoticeAsync(string title, string detail)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close };
        await dialog.ShowAsync();
    }
    private async void CreateVm(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.AcceptLicense || !ViewModel.AcceptFirmware)
            {
                CreateSteps.SelectedIndex = 3;
                CreationError.Message = "Review the license and guest firmware choices, then check both boxes to continue.";
                CreationError.IsOpen = true;
                return;
            }
            CreationError.IsOpen = false;
            var plan = await ViewModel.PreparePlanAsync();
            var startAfterCreation = ViewModel.StartAfterCreation;
            var afterCreation = startAfterCreation ? "Pome will then request to start the VM and open its console." : "The VM will remain off.";
            if (await Confirm("Create this virtual machine?", $"{plan.Name}\n{plan.CpuCount} processors · {plan.MemoryGiB} GB static RAM · {plan.DiskGiB} GB disk\nNetwork: {plan.SwitchName ?? "Disconnected"}\nStorage: {plan.StorageRoot}\n\nPome will copy the verified media, create a new disk and a Generation 2 VM, and disable guest Secure Boot. Windows will request administrator approval. {afterCreation}", "Create"))
            {
                var result = await ViewModel.ExecuteAsync(new(OperationKind.Create, plan));
                if (result is { Success: true })
                {
                    Navigation.SelectedItem = Navigation.MenuItems[0];
                    VmTabs.SelectedIndex = 4;
                    if (result.VmId is Guid id)
                    {
                        ViewModel.SearchText = "";
                        ViewModel.SelectedVm = ViewModel.FilteredMachines.FirstOrDefault(v => v.Id == id) ?? ViewModel.SelectedVm;
                        if (startAfterCreation)
                        {
                            var started = await ViewModel.ExecuteAsync(new(OperationKind.Start, VmId: id));
                            if (started is { Success: true }) await LaunchConsoleAsync(id);
                            else await ShowNoticeAsync("VM created; start needs attention", started?.Message ?? "Select the VM in Library and retry Start & connect. Its files are preserved.");
                            return;
                        }
                    }
                    await ShowNoticeAsync("Virtual machine created", result.Message);
                }
                else if (result is { Success: false })
                {
                    await ShowNoticeAsync("Could not create virtual machine", result.Message);
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.Report(ex.Message);
            await ShowNoticeAsync("Could not prepare virtual machine", ex.Message);
        }
    }
    private async void EnableHyperV(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Host is not null && HostCompatibility.HyperVEnabled(ViewModel.Host)) { ViewModel.Report("Hyper-V is already enabled. Use administrator inspection to load its virtual machines and networks."); return; }
        if (await Confirm("Enable Hyper-V?", "Windows will enable its Hyper-V feature and may require a restart. Pome will not restart Windows or change host security protections. Administrator approval is required.", "Enable feature")) await ViewModel.ExecuteAsync(new(OperationKind.EnableHyperV));
    }
    private async void StartVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Start);
    private async void ShutdownVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Shutdown);
    private async void PauseVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Pause);
    private async void ResumeVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Resume);
    private async void SaveVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Save);
    private async void LoadVmDetails(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is { } vm) await ViewModel.ExecuteAsync(new(OperationKind.InspectVm, VmId: vm.Id));
    }
    private async void ExpandDisk(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanEditSelected) return;
        try
        {
            var size = CreationDraft.WholeNumber(ViewModel.ExpandedDiskGiB, "disk capacity");
            if (!await Confirm("Expand the macOS disk?", $"{vm.Name}\nNew capacity: {size} GB\n\nExport a backup before proceeding. This grows only the original macOS.vhdx; shrinking is unavailable. The VM must be off without checkpoints, and the drive must have space for the full capacity. Expand the APFS partition/container inside macOS afterward.", "Expand disk")) return;
            var result = await ViewModel.ExecuteAsync(new(OperationKind.ResizeDisk, VmId: vm.Id, DiskGiB: size));
            if (result is not null) await ShowNoticeAsync(result.Success ? "Disk expanded" : "Disk expansion needs attention", result.Message);
        }
        catch (Exception ex) { await ShowNoticeAsync("Check disk capacity", ex.Message); }
    }
    private async void ApplyVmSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanEditSelected) return;
        try
        {
            var settings = new VmConfiguration(ViewModel.EditName.Trim(), CreationDraft.WholeNumber(ViewModel.EditCpuCount, "processors"), CreationDraft.WholeNumber(ViewModel.EditMemoryGiB, "memory"), ViewModel.EditSwitch == "Disconnected" ? null : ViewModel.EditSwitch);
            if (!await Confirm("Apply VM settings?", $"{vm.Name} → {settings.Name}\n{settings.CpuCount} processors · {settings.MemoryGiB} GB static memory\nNetwork: {settings.SwitchName ?? "Disconnected"}\n\nThe VM must remain off. Administrator approval is required.", "Apply")) return;
            var result = await ViewModel.ExecuteAsync(new(OperationKind.Configure, VmId: vm.Id, Configuration: settings));
            if (result is { Success: false }) await ShowNoticeAsync("Settings need attention", result.Message);
        }
        catch (Exception ex) { await ShowNoticeAsync("Check VM settings", ex.Message); }
    }
    private async void RepairBoot(object sender, RoutedEventArgs e) => await ManageSelectedAsync(OperationKind.RepairBoot, "Put EFI disk first?", "Pome will put the original EFI disk at SCSI 0:0 first in the firmware boot order. Choose the macOS installer or installed system inside OpenCore. Guest security settings are preserved.", "Apply boot order");
    private async void CreateCheckpoint(object sender, RoutedEventArgs e) => await ManageSelectedAsync(OperationKind.Checkpoint, "Create a checkpoint?", "Create a standard checkpoint of this stopped VM before installing or changing macOS. Checkpoints use additional storage and depend on the original VM disks. Export a separate backup for protection against disk loss.", "Create checkpoint");
    private async void RestoreCheckpoint(object sender, RoutedEventArgs e) => await CheckpointActionAsync(OperationKind.RestoreCheckpoint);
    private async void DeleteCheckpoint(object sender, RoutedEventArgs e) => await CheckpointActionAsync(OperationKind.DeleteCheckpoint);
    private async Task CheckpointActionAsync(OperationKind kind)
    {
        if (ViewModel.SelectedCheckpoint is not { } checkpoint) { ViewModel.Report("Load settings and select a checkpoint first."); return; }
        var restore = kind == OperationKind.RestoreCheckpoint;
        await ManageSelectedAsync(kind, restore ? "Restore this checkpoint?" : "Delete this checkpoint?",
            checkpoint.Summary + (restore ? "\n\nChanges made after this checkpoint will be replaced in the active VM state. Export a backup first if you need to preserve them. The VM must be off." : "\n\nHyper-V will remove this recovery point and merge its disk changes. The active guest state is preserved. The VM must be off."), restore ? "Restore checkpoint" : "Delete checkpoint", checkpoint.Id);
    }
    private async Task ManageSelectedAsync(OperationKind kind, string title, string detail, string action, Guid? checkpoint = null)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanManageSelected) return;
        if (!await Confirm(title, vm.Name + "\n\n" + detail, action)) return;
        var result = await ViewModel.ExecuteAsync(new(kind, VmId: vm.Id, CheckpointId: checkpoint));
        if (result is { Success: false }) await ShowNoticeAsync("Action needs attention", result.Message);
    }
    private async void ExportVm(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanManageSelected) return;
        string? destination = null;
        await ViewModel.Guard(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) destination = SafePaths.RequireLocal(folder.Path);
        });
        if (destination is null || !await Confirm("Export VM backup?", $"{vm.Name}\nDestination: {destination}\n\nPome will create a new backup folder containing the configuration, disks and checkpoints. The VM must remain off. Large exports can take several minutes. Partial files are preserved on failure.", "Export")) return;
        var result = await ViewModel.ExecuteAsync(new(OperationKind.Export, VmId: vm.Id, Destination: destination));
        if (result is not null) await ShowNoticeAsync(result.Success ? "VM exported" : "Export needs attention", result.Message + "\n" + result.ResourceDirectory);
    }
    private async Task VmAction(OperationKind kind)
    {
        if (ViewModel.SelectedVm is not VmInfo vm) { ViewModel.Report("Select a virtual machine first."); await ShowNoticeAsync("Select a virtual machine", "Choose a virtual machine from your library first."); return; }
        if (!vm.Managed) { ViewModel.Report("This VM is read-only because it was created outside pome."); await ShowNoticeAsync("Read-only virtual machine", "This VM is read-only because it was created outside pome."); return; }
        var detail = kind switch
        {
            OperationKind.Shutdown => "Ask macOS to shut down. This needs pome's guest tools inside macOS; the installer and fresh installs cannot answer, so shut down from the Apple menu or use Turn off instead.",
            OperationKind.TurnOff => "Power the VM off immediately, like unplugging a computer. Unsaved work in macOS is lost and the disk may need repair. Use this when the guest is stuck or cannot answer Shut down.",
            OperationKind.Reset => "Restart the VM immediately without shutting macOS down. Unsaved work is lost. OpenCore starts again, so you can pick another boot entry.",
            OperationKind.Save => "Save the VM's current memory and device state to disk, then release its host resources. Use Start & connect to restore the saved state.",
            OperationKind.Pause => "Pause guest execution while retaining its assigned memory. Resume continues from the same point.",
            OperationKind.Resume => "Continue guest execution from the paused state.",
            _ => "Start the selected VM using its current boot media and resource configuration, then open its console."
        };
        var verb = kind switch { OperationKind.TurnOff => "Turn off", OperationKind.Shutdown => "Shut down", OperationKind.Save => "Save state", _ => kind.ToString() };
        if (kind is OperationKind.Pause or OperationKind.Resume || await Confirm($"{verb} {vm.Name}?", detail, verb))
        {
            var result = await ViewModel.ExecuteAsync(new(kind, VmId: vm.Id));
            if (result is { Success: true } && kind == OperationKind.Start) await LaunchConsoleAsync(vm.Id);
            if (result is { Success: false }) await ShowNoticeAsync($"Could not {verb.ToLowerInvariant()} {vm.Name}", result.Message);
        }
    }
    private async void TurnOffVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.TurnOff);
    private async void ResetVm(object sender, RoutedEventArgs e) => await VmAction(OperationKind.Reset);
    private void OpenVmFolder(object sender, RoutedEventArgs e)
    {
        var folder = ViewModel.SelectedVmFolder();
        if (folder is null) { ViewModel.Report("The VM folder is not recorded on this computer. Load full settings to see its disk paths."); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); }
        catch (Exception ex) { ViewModel.Report(ex.Message); }
    }
    private async void DeleteVm(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { Managed: true } vm || !ViewModel.CanDeleteSelected) { ViewModel.Report("Shut down the VM before removing it. VMs created outside pome are read-only."); return; }
        var deleteFiles = new CheckBox { Content = "Also delete its disks, boot media and logs" };
        var confirmName = new TextBox { Header = $"Type {vm.Name} to delete its files", Visibility = Visibility.Collapsed };
        deleteFiles.Checked += (_, _) => confirmName.Visibility = Visibility.Visible;
        deleteFiles.Unchecked += (_, _) => confirmName.Visibility = Visibility.Collapsed;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = $"Remove {vm.Name}?", PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Pome unregisters the VM from Hyper-V. By default its folder and macOS disk are kept, so you can attach them again or back them up. Checkpoints must be deleted first.", TextWrapping = TextWrapping.Wrap },
                    deleteFiles, confirmName,
                    new TextBlock { Text = "Deleting files is permanent and does not use the Recycle Bin. Only the VM's own pome folder is removed, and only if every attached disk is inside it.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }
                }
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var removeFiles = deleteFiles.IsChecked == true;
        if (removeFiles && !string.Equals(confirmName.Text.Trim(), vm.Name, StringComparison.Ordinal))
        { await ShowNoticeAsync("Files were not deleted", "The typed name did not match, so nothing was removed. Try again to remove the VM."); return; }
        var result = await ViewModel.ExecuteAsync(new(OperationKind.DeleteVm, VmId: vm.Id, DeleteFiles: removeFiles));
        if (result is not null) await ShowNoticeAsync(result.Success ? "Virtual machine removed" : $"Could not remove {vm.Name}", result.Message);
    }
    private async void AddGpuPartition(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || ViewModel.SelectedPartitionGpu is not { } gpu || !ViewModel.CanAddGpuPartition) return;
        if (!await Confirm("Attach a GPU partition?", $"{gpu.Name} will be partitioned for {vm.Name}. Pome sets guest-controlled caching, a 1 GB low and 32 GB high memory-mapped I/O gap, and disables checkpoints, saving the previous values so Remove restores them.\n\nThis is experimental: macOS has no GPU-PV driver yet, so graphics stay on the synthetic display. The host GPU keeps working for Windows.", "Attach")) return;
        var result = await ViewModel.ExecuteAsync(new(OperationKind.AddGpuPartition, VmId: vm.Id, GpuInstancePath: gpu.InstancePath));
        if (result is not null) await ShowNoticeAsync(result.Success ? "GPU partition attached" : "Could not attach the GPU partition", result.Message);
    }
    private async void RemoveGpuPartition(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanRemoveGpuPartition) return;
        if (!await Confirm("Remove the GPU partition?", $"Pome removes the GPU partition from {vm.Name} and restores the memory-mapped I/O, cache and checkpoint settings it saved when attaching.", "Remove")) return;
        var result = await ViewModel.ExecuteAsync(new(OperationKind.RemoveGpuPartition, VmId: vm.Id));
        if (result is not null) await ShowNoticeAsync(result.Success ? "GPU partition removed" : "Could not remove the GPU partition", result.Message);
    }
    private async void GrantHyperVAccess(object sender, RoutedEventArgs e)
    {
        if (await Confirm("Join Hyper-V Administrators?", "Windows adds your account to the built-in Hyper-V Administrators group. Members can create, change and control every virtual machine on this computer without an administrator prompt. Pome still asks for approval for creation, disks and files. Sign out and back in for the change to take effect. You can remove yourself later in Computer Management → Local Users and Groups.", "Join group"))
        {
            var result = await ViewModel.ExecuteAsync(new(OperationKind.GrantHyperVAccess));
            if (result is not null) await ShowNoticeAsync(result.Success ? "Hyper-V access updated" : "Could not update Hyper-V access", result.Message);
        }
    }
    private async void OpenConsole(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not VmInfo vm) { ViewModel.Report("Select a virtual machine first."); return; }
        await LaunchConsoleAsync(vm.Id);
    }
    private Task LaunchConsoleAsync(Guid id) => ViewModel.Guard(async () =>
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe")) { UseShellExecute = true, Verb = ViewModel.DirectHyperVAccess ? "" : "runas", ErrorDialogParentHandle = WinRT.Interop.WindowNative.GetWindowHandle(this) };
            start.ArgumentList.Add("localhost"); start.ArgumentList.Add("-G"); start.ArgumentList.Add(id.ToString());
            ViewModel.Report(ViewModel.DirectHyperVAccess ? "Opening the VM console." : "Opening the VM console. Approve Windows administrator access if requested.");
            await Task.Run(() => Process.Start(start));
        }
        catch (Exception ex) { ViewModel.Report(ex.Message); }
    });
    private async void ExportBootLogs(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedVm is not { } vm || !ViewModel.CanManageSelected) return;
        string? destination = null;
        await ViewModel.Guard(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) destination = SafePaths.RequireLocal(folder.Path);
        });
        if (destination is null) return;
        var result = await ViewModel.ExecuteAsync(new(OperationKind.ExportBootLogs, VmId: vm.Id, Destination: destination));
        if (result is not null) await ShowNoticeAsync(result.Success ? "Boot logs exported" : "Boot logs need attention", result.Message + "\n\n" + result.ResourceDirectory);
    }
    private async void DownloadNullMoth(object sender, RoutedEventArgs e)
    {
        if (!await Confirm("Download NullMoth 2.0.2?", "This separate 361 MB archive is provided under PolyForm Noncommercial 1.0.0. Review its linked license first. Downloading does not grant commercial rights. Pome verifies the pinned SHA-256, saves the file locally, and does not install or execute it. Hyper-V acceleration remains unverified.", "Accept terms and download")) return;
        await ViewModel.Guard(async () =>
        {
            ViewModel.Report("Downloading NullMoth from its upstream release…");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "downloads"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "nullmoth-nvidia-2.0.2.tar.gz");
            using var client = VerifiedDownloader.CreateClient();
            await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.NullMoth, path);
            ViewModel.Report($"Verified download saved to {path}. No driver was installed.");
        });
    }
    private async void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        await ViewModel.Guard(async () =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "diagnostics"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"pome-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            // Allowlist export: no machine IDs, serials, PNP paths, usernames, VM names or raw journal text.
            var report = new { schemaVersion = 1, applicationVersion = "0.1.0", osBuild = ViewModel.Host?.Build, edition = ViewModel.Host?.Edition, capabilities = ViewModel.Capabilities, operations = ViewModel.Activity.Select(x => new { x.Kind, x.Stage, x.Terminal, x.UpdatedAt }) };
            await File.WriteAllTextAsync(path, Json.Write(report)); ViewModel.Report($"Diagnostics exported to {path}");
        });
    }
}
