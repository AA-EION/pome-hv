Import-Module Hyper-V -ErrorAction Stop
$plan = $p.plan
$root = [string]$p.resourceDirectory
$journal = Join-Path $root 'operation.json'
$vm = $null
function Initialize-NewMacOsDisk([string]$path) {
    $mounted = $null
    try {
        $mounted = Mount-VHD -Path $path -Passthru -ErrorAction Stop
        if ($null -eq $mounted.DiskNumber) { throw 'The new macOS VHDX did not expose a disk number.' }
        $disk = Get-Disk -Number $mounted.DiskNumber -ErrorAction Stop
        if ($disk.PartitionStyle -ne 'RAW') { throw 'Refusing to initialize a macOS disk that is not blank.' }
        # Called only immediately after New-VHD creates this operation's target.
        # Never discover a target by drive letter, name or physical-disk ordering.
        $partition = $disk | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize
        $partition | Format-Volume -FileSystem exFAT -NewFileSystemLabel macOS -Confirm:$false -Force | Out-Null
    } finally {
        if ($mounted) { Dismount-VHD -Path $path -ErrorAction Stop }
    }
}
function Save-Stage([string]$stage, [string]$detail, [bool]$terminal) {
    $record = @{ operationId=$plan.operationId; stage=$stage; detail=$detail; terminal=$terminal; vmId=$(if ($vm) {$vm.Id.ToString()} else {$null}); updatedAt=[DateTime]::UtcNow.ToString('o') }
    $temp = Join-Path $root 'operation.json.tmp'
    [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $journal -Force
}
try {
    if (Get-VM -ErrorAction Stop | Where-Object { $_.Name -eq $plan.name }) { throw 'A VM with this name already exists.' }
    $efi = Join-Path $root 'EFI.vhdx'
    $target = Join-Path $root 'macOS.vhdx'
    if ($plan.buildEfiFromArchive) {
        Save-Stage 'BuildingEfi' 'Building a new GPT/FAT32 EFI virtual disk from the verified archive.' $false
        New-VHD -Path $efi -Dynamic -SizeBytes 8GB | Out-Null
        $mounted = $null
        $partition = $null
        # Mount to a private folder: no free drive letter is required and Explorer does not open the volume.
        $mountRoot = Join-Path $root 'efi-mount'
        try {
            $mounted = Mount-VHD -Path $efi -Passthru -ErrorAction Stop
            if ($null -eq $mounted.DiskNumber) { throw 'The new VHDX did not expose a disk number.' }
            # Only the disk number returned by mounting this newly created VHDX may be initialized.
            $disk = Get-Disk -Number $mounted.DiskNumber -ErrorAction Stop
            if ($disk.PartitionStyle -ne 'RAW') { throw 'Refusing to initialize a disk that is not blank.' }
            $partition = $disk | Initialize-Disk -PartitionStyle GPT -PassThru | New-Partition -UseMaximumSize
            $partition | Format-Volume -FileSystem FAT32 -NewFileSystemLabel POME-BOOT -Confirm:$false -Force | Out-Null
            New-Item -ItemType Directory -Path $mountRoot -ErrorAction Stop | Out-Null
            Add-PartitionAccessPath -DiskNumber $mounted.DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath ($mountRoot + '\') -ErrorAction Stop
            Copy-Item -LiteralPath (Join-Path $root 'BootFiles/EFI') -Destination $mountRoot -Recurse
            # Guest daemons and the framebuffer installer used by Scripts/post-install.sh inside macOS.
            foreach ($extra in 'Scripts', 'Tools') {
                $source = Join-Path $root ('BootFiles/' + $extra)
                if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $mountRoot -Recurse }
            }
        } finally {
            if ($mounted -and $partition) {
                try { Remove-PartitionAccessPath -DiskNumber $mounted.DiskNumber -PartitionNumber $partition.PartitionNumber -AccessPath ($mountRoot + '\') -ErrorAction Stop } catch { }
            }
            if ($mounted) { Dismount-VHD -Path $efi -ErrorAction Stop }
            # Non-recursive: an unexpectedly non-empty folder is left for inspection.
            try { if ([IO.Directory]::Exists($mountRoot)) { [IO.Directory]::Delete($mountRoot) } } catch { }
        }
    }
    $hasRecovery = $plan.buildEfiFromArchive -or [bool]$plan.recoverySource
    if ($plan.buildEfiFromArchive) {
        Save-Stage 'BuildingRecovery' 'Converting restored Apple recovery to a separate VHDX.' $false
        $recoveryPath = Join-Path $root 'Recovery.vhdx'
        if (Test-Path -LiteralPath $recoveryPath) { throw 'The recovery destination already exists.' }
        $sourceRecovery = Get-VHD -Path (Join-Path $root 'Recovery.vhd') -ErrorAction Stop
        if ($sourceRecovery.Attached -or $sourceRecovery.VhdType -ne 'Fixed' -or $sourceRecovery.VhdFormat -ne 'VHD') { throw 'Expected a detached fixed recovery disk.' }
        Convert-VHD -Path (Join-Path $root 'Recovery.vhd') -DestinationPath $recoveryPath -VHDType Dynamic -ErrorAction Stop
    }
    $efiInfo = Get-VHD -Path $efi
    if ($efiInfo.VhdType -eq 'Differencing' -or $efiInfo.Attached) { throw 'EFI must be a detached, self-contained VHDX.' }
    if ($hasRecovery) {
        $recovery = Join-Path $root 'Recovery.vhdx'
        $recoveryInfo = Get-VHD -Path $recovery
        if ($recoveryInfo.VhdType -eq 'Differencing' -or $recoveryInfo.Attached) { throw 'Recovery must be a detached, self-contained VHDX.' }
    }
    Save-Stage 'CreatingDisk' 'Creating a blank dynamically expanding VHDX.' $false
    New-VHD -Path $target -Dynamic -SizeBytes ([long]$plan.diskGiB * 1GB) | Out-Null
    Initialize-NewMacOsDisk $target
    Save-Stage 'CreatingVm' 'Registering a Generation 2 VM.' $false
    $vm = New-VM -Name $plan.name -Generation 2 -Path $root -NoVHD -MemoryStartupBytes ([long]$plan.memoryGiB * 1GB)
    Save-Stage 'ConfiguringVm' 'Setting static memory, processors and guest boot order.' $false
    Set-VM -VM $vm -Notes ('pome:v1:' + $plan.operationId) -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown
    Set-VMProcessor -VM $vm -Count $plan.cpuCount -HwThreadCountPerCore 1
    Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes ([long]$plan.memoryGiB * 1GB)
    if ($plan.switchName) { Get-VMNetworkAdapter -VM $vm | Connect-VMNetworkAdapter -SwitchName $plan.switchName }
    Add-VMHardDiskDrive -VM $vm -Path $efi -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0
    Add-VMHardDiskDrive -VM $vm -Path $target -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 1
    if ($hasRecovery) { Add-VMHardDiskDrive -VM $vm -Path $recovery -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 2 }
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
