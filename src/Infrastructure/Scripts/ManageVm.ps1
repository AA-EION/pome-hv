Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId) -ErrorAction Stop
if ($p.kind -ne 'InspectVm') {
    if (-not ([string]$vm.Notes).StartsWith('pome:v1:')) { throw 'VMs created outside pome are read-only.' }
    if ($vm.State -ne 'Off') { throw 'Shut down this VM before changing settings, checkpoints or backups.' }
}
$message = 'VM details loaded.'
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
        $checkpoint = @(Get-VMSnapshot -VM $vm -SnapshotType Standard | Where-Object { $_.Id -eq [Guid]$p.checkpointId })
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
    $checkpoints = @(Get-VMSnapshot -VM $vm -SnapshotType Standard | Sort-Object CreationTime -Descending | ForEach-Object {
        @{ id=$_.Id.ToString(); name=$_.Name; creationTime=$_.CreationTime.ToString('o') }
    })
    $network = @(Get-VMNetworkAdapter -VM $vm)
    $details = @{ vmId=$vm.Id.ToString(); name=$vm.Name; cpuCount=[int]$vm.ProcessorCount; startupMemoryBytes=[UInt64]$vm.MemoryStartup;
        switchName=$(if ($network.Count -eq 1) { $network[0].SwitchName } else { $null }); configurationLocation=[string]$vm.ConfigurationLocation;
        secureBoot=$(if ($firmware) { [string]$firmware.SecureBoot } else { 'Not applicable' }); disks=$disks; checkpoints=$checkpoints;
        bootOrder=@(if ($firmware) { $firmware.BootOrder | ForEach-Object { [string]$_.Description } }) }
} catch {
    if ($p.kind -eq 'InspectVm') { throw }
    $message += ' Details could not be refreshed: ' + $_.Exception.Message
}
@{ success=$true; message=$message; vmId=$vm.Id.ToString(); details=$details; resourceDirectory=$p.exportDirectory } | ConvertTo-Json -Depth 8 -Compress
