Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId) -ErrorAction Stop
if ($vm.State -ne 'Off') { throw 'Shut down the VM before collecting boot logs. Live guest disks will not be mounted.' }
if ($vm.Notes -notmatch '^pome:v1:([0-9a-fA-F-]{36})$') { throw 'Boot log collection is limited to pome VMs.' }
$operation = [Guid]$Matches[1]
if (@(Get-VMSnapshot -VM $vm).Count) { throw 'Boot log collection currently requires a VM without checkpoints.' }
$boot = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0 })
if ($boot.Count -ne 1) { throw 'The original EFI disk could not be identified.' }
$efi = [IO.Path]::GetFullPath($boot[0].Path)
$resource = Split-Path -Parent $efi
if ((Split-Path -Leaf $efi) -ne 'EFI.vhdx' -or (Split-Path -Leaf $resource) -ne ('pome-' + $operation.ToString('N'))) { throw 'The EFI disk does not match pome ownership.' }
$journal = Get-Content -LiteralPath (Join-Path $resource 'operation.json') -Raw | ConvertFrom-Json
if ([Guid]$journal.vmId -ne $vm.Id -or [Guid]$journal.operationId -ne $operation) { throw 'EFI operation journal ownership mismatch.' }
$disk = Get-VHD -Path $efi -ErrorAction Stop
if ($disk.Attached -or $disk.VhdType -eq 'Differencing') { throw 'The EFI disk must be detached and self-contained.' }
$mounted = $null
$count = 0
$diagnosis = ''
try {
    $mounted = Mount-VHD -Path $efi -ReadOnly -Passthru -ErrorAction Stop
    if ($null -eq $mounted.DiskNumber) { throw 'The EFI disk did not expose a disk number.' }
    $volumes = @(Get-Partition -DiskNumber $mounted.DiskNumber | Where-Object {
        $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
    } | Get-Volume | Where-Object { $_.FileSystemLabel -eq 'POME-BOOT' -and $_.FileSystem -eq 'FAT32' })
    if ($volumes.Count -ne 1 -or -not $volumes[0].Path) { throw 'Expected one FAT32 POME-BOOT volume.' }
    $volume = [string]$volumes[0].Path
    $logs = @(Get-ChildItem -LiteralPath $volume -File | Where-Object { $_.Name -match '^(opencore-|panic-).+\.txt$' } | Sort-Object LastWriteTime -Descending | Select-Object -First 20)
    foreach ($log in $logs) {
        if ($log.Length -gt 4MB) { throw 'A boot log exceeds the 4 MB export limit. Earlier copied files were preserved.' }
        # File APIs support volume GUID paths; XML/URI overloads do not.
        $text = [IO.File]::ReadAllText($log.FullName).TrimEnd([char]0)
        [IO.File]::WriteAllText((Join-Path $p.outputDirectory $log.Name), $text, [Text.UTF8Encoding]::new($false))
        if ($count -eq 0 -and $text.Contains('EB.MM.AKM') -and $text.Contains('__LINKEDIT') -and $text.Contains('0x16')) {
            $diagnosis = ' The newest log shows a kernel memory-allocation failure. Automatic media now uses disk-based recovery to avoid the observed 4 GB RAM-disk collision.'
        }
        $count++
    }
    Copy-Item -LiteralPath (Join-Path $volume 'EFI\OC\config.plist') -Destination (Join-Path $p.outputDirectory 'config.plist') -ErrorAction Stop
} finally {
    if ($mounted) { Dismount-VHD -Path $efi -ErrorAction Stop }
}
$message = "$count boot/panic log(s) and the OpenCore configuration exported locally." + $diagnosis
if ($count -eq 0) { $message += ' No boot log files were present. Older media may have file logging disabled; automatically prepared new media enables it.' }
@{ success=$true; message=$message; vmId=$vm.Id.ToString(); resourceDirectory=$p.outputDirectory; stage='Exported' } | ConvertTo-Json -Compress
