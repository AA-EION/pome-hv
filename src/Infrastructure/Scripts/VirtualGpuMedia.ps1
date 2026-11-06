# Reads or replaces EFI\OC\config.plist on a stopped pome VM's EFI disk for the virtual GPU, and
# optionally copies the guest driver package to POME-BOOT\PomeGPU. The helper edits the plist (C#).
Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId) -ErrorAction Stop
if ($vm.State -ne 'Off') { throw 'Shut down the VM first. Live guest disks are never mounted.' }
if ($vm.Notes -notmatch '^pome:v1:([0-9a-fA-F-]{36})$') { throw 'The virtual GPU is limited to pome VMs.' }
$operation = [Guid]$Matches[1]
if (@(Get-VMSnapshot -VM $vm).Count) { throw 'Delete this VM''s checkpoints first. The EFI disk must not have differencing children.' }
$boot = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0 })
if ($boot.Count -ne 1) { throw 'The original EFI disk could not be identified.' }
$efi = [IO.Path]::GetFullPath($boot[0].Path)
$resource = Split-Path -Parent $efi
if ((Split-Path -Leaf $efi) -ne 'EFI.vhdx' -or (Split-Path -Leaf $resource) -ne ('pome-' + $operation.ToString('N'))) { throw 'The EFI disk does not match pome ownership.' }
$journal = Get-Content -LiteralPath (Join-Path $resource 'operation.json') -Raw | ConvertFrom-Json
if ([Guid]$journal.vmId -ne $vm.Id -or [Guid]$journal.operationId -ne $operation) { throw 'EFI operation journal ownership mismatch.' }
$local = [IO.Path]::GetFullPath([string]$p.configPath)
if (-not ([IO.Path]::GetDirectoryName($local)).Equals($resource, [StringComparison]::OrdinalIgnoreCase)) { throw 'The working copy must be in the VM''s pome folder.' }
$disk = Get-VHD -Path $efi -ErrorAction Stop
if ($disk.Attached -or $disk.VhdType -eq 'Differencing') { throw 'The EFI disk must be detached and self-contained.' }
$write = $p.mode -eq 'write'
$mounted = $null
try {
    $mounted = Mount-VHD -Path $efi -ReadOnly:(-not $write) -Passthru -ErrorAction Stop
    if ($null -eq $mounted.DiskNumber) { throw 'The EFI disk did not expose a disk number.' }
    $volumes = @(Get-Partition -DiskNumber $mounted.DiskNumber | Where-Object {
        $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
    } | Get-Volume | Where-Object { $_.FileSystemLabel -eq 'POME-BOOT' -and $_.FileSystem -eq 'FAT32' })
    if ($volumes.Count -ne 1 -or -not $volumes[0].Path) { throw 'Expected one FAT32 POME-BOOT volume.' }
    $volume = [string]$volumes[0].Path
    $config = Join-Path $volume 'EFI\OC\config.plist'
    if (-not $write) {
        Copy-Item -LiteralPath $config -Destination $local -Force -ErrorAction Stop
        $message = 'OpenCore configuration read.'
    } else {
        # Keep the first original next to the VM; later writes never replace it.
        $backup = Join-Path $resource 'config.plist.before-virtual-gpu'
        if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $config -Destination $backup -ErrorAction Stop }
        $staged = $config + '.pome-new'
        Copy-Item -LiteralPath $local -Destination $staged -Force -ErrorAction Stop
        Move-Item -LiteralPath $staged -Destination $config -Force -ErrorAction Stop
        $message = 'OpenCore configuration updated.'
        $package = [string]$p.packageDirectory
        $target = Join-Path $volume 'PomeGPU'
        if ($p.removePackage -and (Test-Path -LiteralPath $target)) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
        if ($package -and (Test-Path -LiteralPath (Join-Path $package 'install.sh'))) {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
            Copy-Item -LiteralPath $package -Destination $target -Recurse -ErrorAction Stop
            $message += ' The guest driver package was copied to the POME-BOOT volume (PomeGPU).'
        }
    }
} finally {
    if ($mounted) { Dismount-VHD -Path $efi -ErrorAction Stop }
}
@{ success=$true; message=$message; vmId=$vm.Id.ToString() } | ConvertTo-Json -Compress
