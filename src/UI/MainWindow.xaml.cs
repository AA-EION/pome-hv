using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pome.Core;
using Pome.Infrastructure;

namespace Pome.UI;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    public MainWindow()
    {
        InitializeComponent(); Root.DataContext = ViewModel;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 850));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 800;
        Root.Loaded += async (_, _) => { if (ViewModel.Host is null && ViewModel.Idle) await ViewModel.RefreshAsync(); };
    }
    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (LibraryPanel is null) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        LibraryPanel.Visibility = tag == "Library" ? Visibility.Visible : Visibility.Collapsed;
        CreatePanel.Visibility = tag == "Create" ? Visibility.Visible : Visibility.Collapsed;
        ComputerPanel.Visibility = tag == "Computer" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = tag == "Activity" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void GoCreate(object sender, RoutedEventArgs e) => Navigation.SelectedItem = Navigation.MenuItems[1];
    private async void Inspect(object sender, RoutedEventArgs e) { Navigation.SelectedItem = Navigation.MenuItems[2]; await ViewModel.RefreshAsync(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();
    private async void InspectAsAdministrator(object sender, RoutedEventArgs e) => await ViewModel.ExecuteAsync(new(OperationKind.Inspect));
    private void CancelPreparation(object sender, RoutedEventArgs e) => ViewModel.CancelPreparation();
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
                if (!await Confirm(
                    "Review license & guest firmware settings",
                    "Before preparing and creating this virtual machine, please confirm:\n\n• You have reviewed the applicable Apple macOS software license (https://www.apple.com/legal/sla/) and have permission for this use.\n• You allow Pome to turn off UEFI Secure Boot for this new guest VM and configure OpenCore guest SIP settings (0x03).",
                    "Accept & continue"))
                {
                    return;
                }
                ViewModel.AcceptLicense = true;
                ViewModel.AcceptFirmware = true;
            }
            var plan = await ViewModel.PreparePlanAsync();
            if (await Confirm("Create this virtual machine?", $"{plan.Name}\n{plan.CpuCount} processors · {plan.MemoryGiB} GB static RAM · {plan.DiskGiB} GB disk\nNetwork: {plan.SwitchName ?? "Disconnected"}\nStorage: {plan.StorageRoot}\n\nPome will copy the verified media, create a new disk and a Generation 2 VM, and disable guest Secure Boot. Windows will request administrator approval. The VM will remain off.", "Create"))
            {
                var result = await ViewModel.ExecuteAsync(new(OperationKind.Create, plan));
                if (result is { Success: true })
                {
                    Navigation.SelectedItem = Navigation.MenuItems[0];
                    if (result.VmId is Guid id)
                    {
                        VmList.SelectedItem = ViewModel.VirtualMachines.FirstOrDefault(v => v.Id == id);
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
    private async Task VmAction(OperationKind kind)
    {
        if (VmList.SelectedItem is not VmInfo vm) { ViewModel.Report("Select a virtual machine first."); await ShowNoticeAsync("Select a virtual machine", "Choose a virtual machine from your library first."); return; }
        if (!vm.Managed) { ViewModel.Report("This VM is read-only because it was created outside pome."); await ShowNoticeAsync("Read-only virtual machine", "This VM is read-only because it was created outside pome."); return; }
        if (await Confirm($"{kind} {vm.Name}?", kind == OperationKind.Shutdown ? "Request a graceful guest shutdown. If the guest cannot respond, pome will report the failure; it will not force the VM off." : "Start the selected VM using its current boot media and resource configuration.", kind.ToString()))
        {
            var result = await ViewModel.ExecuteAsync(new(kind, VmId: vm.Id));
            if (result is { Success: false }) await ShowNoticeAsync($"Could not {kind.ToString().ToLowerInvariant()} {vm.Name}", result.Message);
        }
    }
    private void OpenConsole(object sender, RoutedEventArgs e)
    {
        if (VmList.SelectedItem is not VmInfo vm) { ViewModel.Report("Select a virtual machine first."); return; }
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe")) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("localhost"); start.ArgumentList.Add("-G"); start.ArgumentList.Add(vm.Id.ToString()); Process.Start(start);
        }
        catch (Exception ex) { ViewModel.Report(ex.Message); }
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
