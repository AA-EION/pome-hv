Import-Module Hyper-V -ErrorAction Stop
$plan = $p.plan
$root = [string]$p.resourceDirectory
$journal = Join-Path $root 'operation.json'
$vm = $null
function Save-Stage([string]$stage, [string]$detail, [bool]$terminal) {
    $record = @{ operationId=$plan.operationId; stage=$stage; detail=$detail; terminal=$terminal; vmId=$(if ($vm) {$vm.Id.ToString()} else {$null}); updatedAt=[DateTime]::UtcNow.ToString('o') }
    $temp = Join-Path $root 'operation.json.tmp'
    [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $journal -Force
}
try {
    if (Get-VM -Name $plan.name -ErrorAction SilentlyContinue) { throw 'A VM with this name already exists.' }
    $efi = Join-Path $root 'EFI.vhdx'
    $target = Join-Path $root 'macOS.vhdx'
    if ($plan.buildEfiFromArchive) {
        Save-Stage 'BuildingEfi' 'Building a new GPT/FAT32 EFI virtual disk from the verified archive.' $false
        New-VHD -Path $efi -Dynamic -SizeBytes 8GB | Out-Null
        $mounted = $null
        try {
            $mounted = Mount-VHD -Path $efi -Passthru -ErrorAction Stop
            if ($null -eq $mounted.DiskNumber) { throw 'The new VHDX did not expose a disk number.' }
            # Only the disk number returned by mounting this newly created VHDX may be initialized.
            $disk = Get-Disk -Number $mounted.DiskNumber -ErrorAction Stop
            if ($disk.PartitionStyle -ne 'RAW') { throw 'Refusing to initialize a disk that is not blank.' }
            $partition = $disk | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize -AssignDriveLetter
            $volume = $partition | Format-Volume -FileSystem FAT32 -NewFileSystemLabel POME-BOOT -Confirm:$false -Force
            if (-not $volume.DriveLetter) { throw 'The EFI volume has no mount point.' }
            $mountRoot = [string]$volume.DriveLetter + ':\'
            Copy-Item -LiteralPath (Join-Path $root 'BootFiles/EFI') -Destination $mountRoot -Recurse
            Copy-Item -LiteralPath (Join-Path $root 'BootFiles/com.apple.recovery.boot') -Destination $mountRoot -Recurse
        } finally {
            if ($mounted) { Dismount-VHD -Path $efi -ErrorAction Stop }
        }
    }
    $efiInfo = Get-VHD -Path $efi
    if ($efiInfo.VhdType -eq 'Differencing' -or $efiInfo.Attached) { throw 'EFI must be a detached, self-contained VHDX.' }
    if ($plan.recoverySource) {
        $recovery = Join-Path $root 'Recovery.vhdx'
        $recoveryInfo = Get-VHD -Path $recovery
        if ($recoveryInfo.VhdType -eq 'Differencing' -or $recoveryInfo.Attached) { throw 'Recovery must be a detached, self-contained VHDX.' }
    }
    Save-Stage 'CreatingDisk' 'Creating a blank dynamically expanding VHDX.' $false
    New-VHD -Path $target -Dynamic -SizeBytes ([long]$plan.diskGiB * 1GB) | Out-Null
    Save-Stage 'CreatingVm' 'Registering a Generation 2 VM.' $false
    $vm = New-VM -Name $plan.name -Generation 2 -Path $root -NoVHD -MemoryStartupBytes ([long]$plan.memoryGiB * 1GB)
    Save-Stage 'ConfiguringVm' 'Setting static memory, processors and guest boot order.' $false
    Set-VM -VM $vm -Notes ('pome:v1:' + $plan.operationId) -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown
    Set-VMProcessor -VM $vm -Count $plan.cpuCount
    Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes ([long]$plan.memoryGiB * 1GB)
    if ($plan.switchName) { Get-VMNetworkAdapter -VM $vm | Connect-VMNetworkAdapter -SwitchName $plan.switchName }
    Add-VMHardDiskDrive -VM $vm -Path $efi -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
    Add-VMHardDiskDrive -VM $vm -Path $target -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 1
    if ($plan.recoverySource) { Add-VMHardDiskDrive -VM $vm -Path $recovery -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 2 }
    $boot = Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path -eq $efi }
    Set-VMFirmware -VM $vm -EnableSecureBoot Off -FirstBootDevice $boot
    Save-Stage 'Created' 'VM configured. Guest boot and macOS installation have not been verified.' $true
    @{ success=$true; message='VM created. Start it and open the console to validate the prepared boot media.'; vmId=$vm.Id.ToString(); resourceDirectory=$root; stage='Created' } | ConvertTo-Json -Compress
} catch {
    $failure = $_.Exception.Message
    # The new VM has never been started. Remove only this exact registration, and preserve every disk.
    if ($vm) {
        try { Remove-VM -VM $vm -Force -ErrorAction Stop } catch { $failure += ' VM registration rollback failed: ' + $_.Exception.Message }
    }
    Save-Stage 'NeedsAttention' $failure $false
    @{ success=$false; message=($failure + ' Files were preserved; inspect operation.json before retrying.'); resourceDirectory=$root; stage='NeedsAttention' } | ConvertTo-Json -Compress
}
