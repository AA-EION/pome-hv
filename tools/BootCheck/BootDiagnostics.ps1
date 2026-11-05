param(
    [Parameter(Mandatory=$true)][Guid]$VmId,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [switch]$EnableLogging,
    [switch]$RestartFailedBoot,
    [switch]$StartAfterRepair,
    [string]$RecoveryVhd,
    [string]$RecoveryVhdSha256
)
# Run locally as administrator. Default mode only reads a powered-off EFI disk.
# RestartFailedBoot explicitly permits powering off this failed, managed guest.
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the diagnostic launcher as administrator.'
}
if ($RestartFailedBoot -and -not $EnableLogging) { throw 'Restart requires EnableLogging.' }
if ($StartAfterRepair -and -not $EnableLogging) { throw 'StartAfterRepair requires EnableLogging.' }
if ($RecoveryVhd -and -not $EnableLogging) { throw 'Recovery disk preparation requires EnableLogging.' }
Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id $VmId -ErrorAction Stop
if ($vm.Notes -notmatch '^pome:v1:([0-9a-fA-F-]{36})$') { throw 'This VM is not managed by pome.' }
$operationId = [Guid]$Matches[1]
$bootDisks = @(Get-VMHardDiskDrive -VM $vm | Where-Object {
    $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 0
})
if ($bootDisks.Count -ne 1) { throw 'Expected exactly one managed EFI disk at SCSI 0:0.' }
$efi = [IO.Path]::GetFullPath($bootDisks[0].Path)
$resource = Split-Path -Parent $efi
if ((Split-Path -Leaf $efi) -ne 'EFI.vhdx' -or (Split-Path -Leaf $resource) -ne ('pome-' + $operationId.ToString('N'))) {
    throw 'The EFI path does not match pome ownership.'
}
$journal = Get-Content -LiteralPath (Join-Path $resource 'operation.json') -Raw | ConvertFrom-Json
if ([Guid]$journal.vmId -ne $VmId -or [Guid]$journal.operationId -ne $operationId) { throw 'VM journal ownership mismatch.' }
if ($RecoveryVhd) {
    $RecoveryVhd = [IO.Path]::GetFullPath($RecoveryVhd)
    if ($RecoveryVhd.StartsWith('\\') -or -not $RecoveryVhd.EndsWith('.vhd', [StringComparison]::OrdinalIgnoreCase) -or
        $RecoveryVhdSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $RecoveryVhd -Algorithm SHA256).Hash -ne $RecoveryVhdSha256) {
        throw 'Recovery intermediate must be a local VHD with the reviewed SHA-256 digest.'
    }
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if ($evidence.StartsWith('\\') -or -not [IO.Directory]::Exists($evidence)) { throw 'An existing local evidence directory is required.' }
$capture = Join-Path $evidence ('boot-logs-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $capture -ErrorAction Stop | Out-Null

function Require-Off {
    $current = Get-VM -Id $VmId
    if ([string]$current.State -ne 'Off') { throw 'Power off the VM before mounting its EFI disk. No live disk will be mounted.' }
}
function With-Efi([bool]$writable, [scriptblock]$action) {
    Require-Off
    $info = Get-VHD -Path $efi
    if ($info.Attached -or $info.VhdType -eq 'Differencing') { throw 'Expected a detached, self-contained EFI disk.' }
    $mounted = $null
    try {
        $mounted = Mount-VHD -Path $efi -ReadOnly:(-not $writable) -Passthru -ErrorAction Stop
        if ($null -eq $mounted.DiskNumber) { throw 'The EFI mount did not return a disk number.' }
        $volumes = @(Get-Partition -DiskNumber $mounted.DiskNumber | Where-Object {
            $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
        } | Get-Volume | Where-Object {
            $_.FileSystemLabel -eq 'POME-BOOT' -and $_.FileSystem -eq 'FAT32'
        })
        if ($volumes.Count -ne 1 -or -not $volumes[0].Path) { throw 'Expected one FAT32 POME-BOOT volume.' }
        # Use the exact mounted volume GUID, never a guessed letter or host disk.
        & $action ([string]$volumes[0].Path)
    } finally {
        if ($mounted) { Dismount-VHD -Path $efi -ErrorAction Stop }
    }
}
function Copy-Logs([string]$volume) {
    $logs = @(Get-ChildItem -LiteralPath $volume -File | Where-Object { $_.Name -match '^(opencore-|panic-).+\.txt$' })
    foreach ($log in $logs) { Copy-Item -LiteralPath $log.FullName -Destination $capture }
    Copy-Item -LiteralPath (Join-Path $volume 'EFI\OC\config.plist') -Destination (Join-Path $capture 'config.plist')
    Write-Host ('Copied {0} boot/panic log(s) to {1}' -f $logs.Count, $capture)
}
function Install-RecoveryDisk {
    Require-Off
    if (@(Get-VMSnapshot -VM $vm).Count) { throw 'Recovery disk changes require a VM without checkpoints.' }
    $target = Join-Path $resource 'Recovery.vhdx'
    $receiptPath = Join-Path $resource 'Recovery.disk.json'
    $slot = @(Get-VMHardDiskDrive -VM $vm | Where-Object {
        $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 2
    })
    if ($slot.Count -gt 1 -or ($slot.Count -eq 1 -and [IO.Path]::GetFullPath($slot[0].Path) -ne $target)) {
        throw 'SCSI 0:2 is occupied by a different disk.'
    }
    if ([IO.File]::Exists($target)) {
        if (-not [IO.File]::Exists($receiptPath)) { throw 'Existing recovery disk has no ownership receipt; files were preserved.' }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ([Guid]$receipt.operationId -ne $operationId -or $receipt.sourceSha256 -ne $RecoveryVhdSha256) {
            throw 'Existing recovery disk does not match the reviewed source.'
        }
    } else {
        if ($slot.Count) { throw 'The registered recovery disk is missing.' }
        $sourceInfo = Get-VHD -Path $RecoveryVhd
        if ($sourceInfo.Attached -or $sourceInfo.VhdType -ne 'Fixed' -or $sourceInfo.VhdFormat -ne 'VHD') {
            throw 'Expected a detached fixed recovery VHD.'
        }
        Convert-VHD -Path $RecoveryVhd -DestinationPath $target -VHDType Dynamic -ErrorAction Stop
        @{ operationId=$operationId; sourceSha256=$RecoveryVhdSha256 } | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
    }
    $targetInfo = Get-VHD -Path $target
    if ($targetInfo.Attached -or $targetInfo.VhdType -eq 'Differencing' -or $targetInfo.VhdFormat -ne 'VHDX') { throw 'Expected a detached recovery VHDX.' }
    if (-not $slot.Count) { Add-VMHardDiskDrive -VM $vm -Path $target -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 2 -ErrorAction Stop }
    Write-Host 'Attached disk-based recovery at SCSI 0:2. The existing macOS install disk was not modified.'
}

if ($EnableLogging) {
    if ($RestartFailedBoot -and [string]$vm.State -ne 'Off') {
        if ([string]$vm.State -ne 'Running') { throw 'Only a running failed boot or powered-off VM is supported.' }
        Write-Host ('Powering off the failed boot of {0} ({1}).' -f $vm.Name, $VmId)
        Stop-VM -VM $vm -TurnOff -Force -ErrorAction Stop
    }
    if ($RecoveryVhd) { Install-RecoveryDisk }
    With-Efi $true {
        param($volume)
        Copy-Logs $volume
        $path = Join-Path $volume 'EFI\OC\config.plist'
        Copy-Item -LiteralPath $path -Destination (Join-Path $capture 'config.before.plist')
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Ignore
        $settings.XmlResolver = $null
        $config = [Xml.XmlDocument]::new()
        $config.XmlResolver = $null
        # XmlReader's filename overload interprets a volume GUID (\\?\Volume...)
        # as a URI. File streams retain Windows device-path semantics instead.
        $inputStream = [IO.File]::OpenRead($path)
        try {
            $reader = [Xml.XmlReader]::Create($inputStream, $settings)
            try { $config.Load($reader) } finally { $reader.Dispose() }
        } finally { $inputStream.Dispose() }
        $debugKey = $config.SelectNodes('/plist/dict/key[text()="Misc"]/following-sibling::*[1]/key[text()="Debug"]')
        if ($debugKey.Count -ne 1) { throw 'Expected the complete OpenCore Debug dictionary.' }
        $debug = $debugKey[0].NextSibling
        foreach ($entry in @(@('AppleDebug','true',$null), @('ApplePanic','true',$null), @('Target','integer','67'))) {
            $keys = $debug.SelectNodes('key[text()="' + $entry[0] + '"]')
            if ($keys.Count -ne 1) { throw ('Invalid OpenCore setting: ' + $entry[0]) }
            $replacement = $config.CreateElement($entry[1])
            if ($null -ne $entry[2]) { $replacement.InnerText = $entry[2] }
            $debug.ReplaceChild($replacement, $keys[0].NextSibling) | Out-Null
        }
        if ($RecoveryVhd) {
            $key = $config.SelectNodes('/plist/dict/key[text()="Misc"]/following-sibling::*[1]/key[text()="Security"]/following-sibling::*[1]/key[text()="DmgLoading"]')
            if ($key.Count -ne 1 -or $key[0].NextSibling.Name -ne 'string') { throw 'Invalid OpenCore DMG loading policy.' }
            # Boot the restored HFS+ disk; avoid allocating the DMG RAM disk.
            $key[0].NextSibling.InnerText = 'Disabled'
        }
        $temp = $path + '.pome-diagnostic.tmp'
        $writerSettings = [Xml.XmlWriterSettings]::new()
        $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
        $writerSettings.Indent = $true
        $outputStream = [IO.File]::Open($temp, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $writer = [Xml.XmlWriter]::Create($outputStream, $writerSettings)
            try { $config.Save($writer) } finally { $writer.Dispose() }
        } finally { $outputStream.Dispose() }
        # The original config is retained in the timestamped evidence folder.
        Move-Item -LiteralPath $temp -Destination $path -Force
        Write-Host 'Enabled normal file logging and Apple boot/panic diagnostics. RAM, CPUs, Secure Boot and SIP are unchanged.'
    }
    if ($RestartFailedBoot -or $StartAfterRepair) {
        $available = [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
        $startup = [long](Get-VMMemory -VM $vm).Startup
        if ($available -lt ($startup + 2GB)) { throw 'Not enough free RAM to restart with the existing 2 GB Windows reserve. Logs remain enabled.' }
        Start-VM -VM $vm -ErrorAction Stop
        # An interactive VM console is intentional: the user observes the failure.
        Start-Process -FilePath "$env:SystemRoot\System32\vmconnect.exe" -ArgumentList @('localhost','-G',$VmId.ToString())
        if ($StartAfterRepair -and -not $RestartFailedBoot) {
            Write-Host 'The VM is running. Observe the recovery disk boot in the console; this is not yet proof that Recovery works.'
            Write-Host ('Diagnostic evidence: ' + $capture)
            return
        }
        Write-Host 'Wait for the SAME failed boot to return to OpenCore. Do not begin installation during this capture.'
        Read-Host 'After the failure, return here and press Enter to power off this VM and collect its logs' | Out-Null
        $current = Get-VM -Id $VmId
        if ([string]$current.State -eq 'Running') { Stop-VM -VM $current -TurnOff -Force -ErrorAction Stop }
        With-Efi $false { param($volume) Copy-Logs $volume }
    }
} else {
    With-Efi $false { param($volume) Copy-Logs $volume }
}
Write-Host ('Diagnostic evidence: ' + $capture)
