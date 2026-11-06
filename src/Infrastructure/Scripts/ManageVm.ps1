Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId) -ErrorAction Stop
if ($p.kind -ne 'InspectVm') {
    if (-not ([string]$vm.Notes).StartsWith('pome:v1:')) { throw 'VMs created outside pome are read-only.' }
    if ($vm.State -ne 'Off') { throw 'Shut down this VM before changing settings, checkpoints or backups.' }
}
$message = 'VM details loaded.'
function Get-PartitionableGpus {
    # Windows 11 / Server 2022 name the cmdlet Get-VMHostPartitionableGpu; Windows 10 uses Get-VMPartitionableGpu.
    $command = @('Get-VMHostPartitionableGpu', 'Get-VMPartitionableGpu') | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if (-not $command) { return @() }
    $controllers = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)
    @(& $command -ErrorAction Stop | ForEach-Object {
        $path = [string]$_.Name
        $trimmed = $(if ($path.StartsWith('\\?\')) { $path.Substring(4) } else { $path })
        $parts = $trimmed.Split('#')
        $pnp = $(if ($parts.Count -ge 3) { $parts[0..2] -join '\' } else { '' })
        $match = @($controllers | Where-Object { [string]$_.PNPDeviceID -ieq $pnp })
        @{ instancePath=$path; name=$(if ($match.Count -gt 0) { [string]$match[0].Name } else { 'Partitionable GPU' }) }
    })
}
function Get-GpuPartitionPaths($machine) {
    if (-not (Get-Command Get-VMGpuPartitionAdapter -ErrorAction SilentlyContinue)) { return @() }
    @(Get-VMGpuPartitionAdapter -VM $machine -ErrorAction Stop | ForEach-Object { [string]$_.InstancePath })
}
switch ($p.kind) {
    'InspectVm' { }
    'Configure' {
        $settings = $p.configuration
        if (Get-VM -ErrorAction Stop | Where-Object { $_.Id -ne $vm.Id -and $_.Name -eq $settings.name }) { throw 'A VM with this name already exists.' }
        $adapter = @(Get-VMNetworkAdapter -VM $vm)
        if ($adapter.Count -ne 1) { throw 'Settings editing requires exactly one network adapter.' }
        $original = @{ name=$vm.Name; cpu=$vm.ProcessorCount; memory=$vm.MemoryStartup; network=$adapter[0].SwitchName }
        try {
            Set-VMProcessor -VM $vm -Count ([int]$settings.cpuCount)
            Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes ([long]$settings.memoryGiB * 1GB)
            if ($settings.switchName) { $adapter[0] | Connect-VMNetworkAdapter -SwitchName $settings.switchName }
            else { $adapter[0] | Disconnect-VMNetworkAdapter }
            if ($settings.name -ne $vm.Name) { Rename-VM -VM $vm -NewName $settings.name }
        } catch {
            $failure = $_.Exception.Message
            # Best-effort rollback of each independent setting; never delete a VM or disk.
            try { Set-VMProcessor -VM $vm -Count $original.cpu } catch { $failure += ' CPU rollback failed: ' + $_.Exception.Message }
            try { Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes $original.memory } catch { $failure += ' Memory rollback failed: ' + $_.Exception.Message }
            try {
                if ($original.network) { $adapter[0] | Connect-VMNetworkAdapter -SwitchName $original.network }
                else { $adapter[0] | Disconnect-VMNetworkAdapter }
            } catch { $failure += ' Network rollback failed: ' + $_.Exception.Message }
            try { if ($vm.Name -ne $original.name) { Rename-VM -VM $vm -NewName $original.name } } catch { $failure += ' Name rollback failed: ' + $_.Exception.Message }
            throw $failure
        }
        $message = 'Name, processors, static memory and network updated. Disk files retain their existing names.'
    }
    'RepairBoot' {
        $boot = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0 })
        if ($boot.Count -ne 1) { throw 'The original EFI disk could not be identified at SCSI 0:0. No firmware was changed.' }
        Set-VMFirmware -VM $vm -FirstBootDevice $boot[0]
        $message = 'The original EFI disk is first in the boot order. Secure Boot settings were preserved.'
    }
    'Checkpoint' {
        $previousType = $vm.CheckpointType
        try {
            Set-VM -VM $vm -CheckpointType Standard
            Checkpoint-VM -VM $vm -SnapshotName ('pome ' + [DateTime]::Now.ToString('yyyy-MM-dd HHmmss')) | Out-Null
        } finally { Set-VM -VM $vm -CheckpointType $previousType }
        $message = 'Stopped-VM checkpoint created. Checkpoints depend on the original disks; export a separate backup for recovery.'
    }
    { $_ -in 'RestoreCheckpoint', 'DeleteCheckpoint' } {
        $checkpoint = @(Get-VMSnapshot -VM $vm | Where-Object { [string]$_.SnapshotType -eq 'Standard' -and $_.Id -eq [Guid]$p.checkpointId })
        if ($checkpoint.Count -ne 1) { throw 'This checkpoint no longer exists on the selected VM.' }
        if ($p.kind -eq 'RestoreCheckpoint') {
            Restore-VMSnapshot -VMSnapshot $checkpoint[0] -Confirm:$false
            $message = 'Checkpoint restored. Changes made after this checkpoint are no longer in the active guest state.'
        } else {
            Remove-VMSnapshot -VMSnapshot $checkpoint[0] -Confirm:$false
            $message = 'Checkpoint removed. Hyper-V will merge its disk changes; the active guest disk is preserved.'
        }
    }
    'Export' {
        Export-VM -VM $vm -Path ([string]$p.exportDirectory) -ErrorAction Stop
        $message = 'VM exported. Keep the entire export folder together, including all virtual disks and configuration files.'
    }
    'ResizeDisk' {
        if (@(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'Remove all checkpoints before expanding the macOS disk.' }
        $target = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 1 -and $_.Path -eq $p.systemDisk })
        if ($target.Count -ne 1) { throw 'The original macOS disk changed. Refresh before retrying.' }
        $diskInfo = Get-VHD -Path $target[0].Path -ErrorAction Stop
        $newSize = [long]$p.diskGiB * 1GB
        if ($diskInfo.Attached -or $diskInfo.VhdType -eq 'Differencing' -or $newSize -le $diskInfo.Size) { throw 'Only a detached, self-contained disk can grow. Shrinking and unchanged sizes are unavailable.' }
        Resize-VHD -Path $target[0].Path -SizeBytes $newSize -ErrorAction Stop
        $message = 'Virtual disk expanded. Start macOS and use Disk Utility to expand the APFS partition/container into the new space. Guest partitions were not modified.'
    }
    'AddGpuPartition' {
        # The helper verified ownership, the Tahoe guest record and the operation folder.
        if (@(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'Delete this VM''s checkpoints first. Hyper-V cannot checkpoint or save a VM with a GPU partition.' }
        if (@(Get-GpuPartitionPaths $vm).Count -ne 0) { throw 'This VM already has a GPU partition.' }
        $gpu = @(Get-PartitionableGpus | Where-Object { $_.instancePath -ieq [string]$p.gpuInstancePath })
        if ($gpu.Count -ne 1) { throw 'The selected GPU is no longer partitionable on this computer. Refresh the VM''s settings.' }
        $record = Join-Path ([string]$p.operationFolder) 'gpu-partition.json'
        $original = @{ guestControlledCacheTypes=[bool]$vm.GuestControlledCacheTypes; lowMmioBytes=[UInt64]$vm.LowMemoryMappedIoSpace;
            highMmioBytes=[UInt64]$vm.HighMemoryMappedIoSpace; checkpointType=[string]$vm.CheckpointType; instancePath=[string]$p.gpuInstancePath }
        # Keep the first recorded settings if an earlier attempt left the file behind.
        if (-not (Test-Path -LiteralPath $record)) { [IO.File]::WriteAllText($record, ($original | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false)) }
        try {
            Set-VM -VM $vm -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace ([UInt64]$p.lowMmioBytes) -HighMemoryMappedIoSpace ([UInt64]$p.highMmioBytes) -CheckpointType Disabled -ErrorAction Stop
            Add-VMGpuPartitionAdapter -VM $vm -InstancePath ([string]$p.gpuInstancePath) -ErrorAction Stop
        } catch {
            $failure = $_.Exception.Message
            try {
                Set-VM -VM $vm -GuestControlledCacheTypes $original.guestControlledCacheTypes -LowMemoryMappedIoSpace $original.lowMmioBytes -HighMemoryMappedIoSpace $original.highMmioBytes -CheckpointType $original.checkpointType -ErrorAction Stop
            } catch { $failure += ' Settings rollback failed: ' + $_.Exception.Message }
            throw $failure
        }
        $message = 'GPU partition attached. macOS has no GPU-PV driver yet, so this does not accelerate graphics; the guest can now see virtual PCI device 1414:008E for driver development. Checkpoints are disabled while it is attached.'
    }
    'RemoveGpuPartition' {
        $adapters = @(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop)
        if ($adapters.Count -eq 0) { throw 'This VM has no GPU partition.' }
        $adapters | Remove-VMGpuPartitionAdapter -ErrorAction Stop
        $record = Join-Path ([string]$p.operationFolder) 'gpu-partition.json'
        $message = 'GPU partition removed.'
        if (Test-Path -LiteralPath $record) {
            $original = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
            Set-VM -VM $vm -GuestControlledCacheTypes ([bool]$original.guestControlledCacheTypes) -LowMemoryMappedIoSpace ([UInt64]$original.lowMmioBytes) -HighMemoryMappedIoSpace ([UInt64]$original.highMmioBytes) -CheckpointType ([string]$original.checkpointType) -ErrorAction Stop
            Remove-Item -LiteralPath $record -ErrorAction Stop
            $message += ' Memory-mapped I/O, cache and checkpoint settings were restored.'
        }
    }
    'DeleteVm' {
        if (@(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'Delete this VM''s checkpoints before removing it.' }
        # The helper verified this folder; recheck that every disk still lives in it before unregistering.
        $folder = [IO.Path]::GetFullPath([string]$p.operationFolder).TrimEnd('\')
        foreach ($disk in @(Get-VMHardDiskDrive -VM $vm)) {
            if (-not ([IO.Path]::GetDirectoryName([string]$disk.Path)).Equals($folder, [StringComparison]::OrdinalIgnoreCase)) { throw 'A disk changed since it was checked. Refresh before retrying; nothing was removed.' }
        }
        Remove-VM -VM $vm -Force -ErrorAction Stop
        @{ success=$true; message='VM removed from Hyper-V.'; vmId=$vm.Id.ToString(); resourceDirectory=$folder } | ConvertTo-Json -Compress
        return
    }
    default { throw 'Unsupported VM management action.' }
}
$details = $null
try {
    $vm = Get-VM -Id $vm.Id
    $firmware = $(if ($vm.Generation -eq 2) { Get-VMFirmware -VM $vm } else { $null })
    $disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object {
        $disk = $_
        $capacity = [UInt64]0
        try { $capacity = [UInt64](Get-VHD -Path $disk.Path -ErrorAction Stop).Size } catch { }
        @{ path=[string]$disk.Path; capacityBytes=$capacity; controller=([string]$disk.ControllerType + ' ' + $disk.ControllerNumber + ':' + $disk.ControllerLocation) }
    })
    # Get-VMSnapshot -SnapshotType throws when a VM has none (Windows 11 build 26300); filter instead.
    $checkpoints = @(Get-VMSnapshot -VM $vm | Where-Object { [string]$_.SnapshotType -eq 'Standard' } | Sort-Object CreationTime -Descending | ForEach-Object {
        @{ id=$_.Id.ToString(); name=$_.Name; creationTime=$_.CreationTime.ToString('o') }
    })
    $network = @(Get-VMNetworkAdapter -VM $vm)
    $details = @{ vmId=$vm.Id.ToString(); name=$vm.Name; cpuCount=[int]$vm.ProcessorCount; startupMemoryBytes=[UInt64]$vm.MemoryStartup;
        switchName=$(if ($network.Count -eq 1) { $network[0].SwitchName } else { $null }); configurationLocation=[string]$vm.ConfigurationLocation;
        secureBoot=$(if ($firmware) { [string]$firmware.SecureBoot } else { 'Not applicable' }); disks=$disks; checkpoints=$checkpoints;
        bootOrder=@(if ($firmware) { $firmware.BootOrder | ForEach-Object { [string]$_.Description } });
        gpuPartitions=@(try { Get-GpuPartitionPaths $vm } catch { });
        partitionableGpus=@(try { Get-PartitionableGpus } catch { }) }
} catch {
    if ($p.kind -eq 'InspectVm') { throw }
    $message += ' Details could not be refreshed: ' + $_.Exception.Message
}
@{ success=$true; message=$message; vmId=$vm.Id.ToString(); details=$details; resourceDirectory=$p.exportDirectory } | ConvertTo-Json -Depth 8 -Compress
