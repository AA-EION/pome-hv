# Exercise the real diagnostic script with Hyper-V/process cmdlet doubles.
# The administrator-token check alone is replaced for the unelevated fixture.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Get-Content -LiteralPath (Join-Path $root 'tools/BootCheck/BootDiagnostics.ps1') -Raw
$guard = '$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'
if (-not $source.Contains($guard)) { throw 'Administrator guard fixture is stale.' }
$program = [scriptblock]::Create($source.Replace($guard, '$true'))
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Reject([scriptblock]$action, [string]$message) {
    try { & $action | Out-Null } catch {
        if ($_.Exception.Message.Contains($message)) { return }
        throw
    }
    throw "Expected: $message"
}
function Import-Module { param($Name, $ErrorAction) }
function Get-VM { [CmdletBinding()] param($Id) $script:vm }
function Get-VMHardDiskDrive {
    param($VM)
    [pscustomobject]@{ ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=0; Path=$script:efi }
    if ($script:recoverySlot) { [pscustomobject]@{ ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=2; Path=$script:recoverySlot } }
}
function Get-VHD {
    param($Path)
    if ($Path.EndsWith('.vhd')) { [pscustomobject]@{ Attached=$false; VhdType='Fixed'; VhdFormat='VHD' } }
    else { [pscustomobject]@{ Attached=$false; VhdType='Dynamic'; VhdFormat='VHDX' } }
}
function Get-VMSnapshot { param($VM) $script:checkpoints }
function Convert-VHD {
    [CmdletBinding()] param($Path, $DestinationPath, $VHDType)
    $script:events.Add('Convert')
    [IO.File]::WriteAllText($DestinationPath, 'converted fixture')
}
function Add-VMHardDiskDrive {
    [CmdletBinding()] param($VM, $Path, $ControllerType, $ControllerNumber, $ControllerLocation)
    Assert ($ControllerType -eq 'SCSI' -and $ControllerNumber -eq 0 -and $ControllerLocation -eq 2) 'Recovery used the wrong controller slot'
    $script:events.Add('Attach')
    $script:recoverySlot = $Path
}
function Mount-VHD {
    [CmdletBinding()] param($Path, [switch]$ReadOnly, [switch]$Passthru)
    Assert ($Path -eq $script:efi) 'Only the EFI disk may be mounted'
    $script:events.Add('Mount:' + $ReadOnly.IsPresent)
    [pscustomobject]@{ DiskNumber=37 }
}
function Get-Partition {
    param($DiskNumber)
    Assert ($DiskNumber -eq 37) 'Wrong disk selected'
    [pscustomobject]@{ DiskNumber=37; GptType='{e3c9e316-0b5c-4db8-817d-f92df00215ae}' }
    [pscustomobject]@{ DiskNumber=37; GptType='{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' }
}
function Get-Volume {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject)
    process {
        Assert ($InputObject.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}') 'A reserved partition reached volume selection'
        [pscustomobject]@{ FileSystemLabel='POME-BOOT'; FileSystem='FAT32'; Path=$script:volume }
    }
}
function Dismount-VHD { [CmdletBinding()] param($Path) $script:events.Add('Dismount') }
function Stop-VM { [CmdletBinding()] param($VM, [switch]$TurnOff, [switch]$Force) $script:events.Add('Stop'); $VM.State='Off' }
function Get-CimInstance { param($ClassName) [pscustomobject]@{ FreePhysicalMemory=($script:available/1KB) } }
function Get-VMMemory { param($VM) [pscustomobject]@{ Startup=4GB } }
function Start-VM { [CmdletBinding()] param($VM) $script:events.Add('Start'); $VM.State='Running' }
function Start-Process { param($FilePath, $ArgumentList) $script:events.Add('Console') }
function Read-Host { param($Prompt) '' }
function Fixture {
    $script:fixtureRoot = Join-Path $root ('artifacts/tests/boot-diagnostic-' + [Guid]::NewGuid().ToString('N'))
    $operation = [Guid]::NewGuid()
    $script:vm = [pscustomobject]@{ Id=[Guid]::NewGuid(); Name='Fixture'; State='Off'; Notes=('pome:v1:' + $operation) }
    $resource = Join-Path $script:fixtureRoot ('pome-' + $operation.ToString('N'))
    $script:efi = Join-Path $resource 'EFI.vhdx'
    $script:volume = Join-Path $script:fixtureRoot 'mounted-volume'
    New-Item -ItemType Directory -Path $resource, (Join-Path $script:volume 'EFI/OC') -Force | Out-Null
    @{ vmId=$script:vm.Id; operationId=$operation } | ConvertTo-Json | Set-Content (Join-Path $resource 'operation.json')
    $script:config = Join-Path $script:volume 'EFI/OC/config.plist'
    $script:original = '<plist><dict><key>Misc</key><dict><key>Debug</key><dict><key>AppleDebug</key><false/><key>ApplePanic</key><false/><key>Target</key><integer>3</integer></dict></dict><key>preserve</key><string>-v cpus=1</string></dict></plist>'
    [IO.File]::WriteAllText($script:config, $script:original)
    [IO.File]::WriteAllText((Join-Path $script:volume 'opencore-fixture.txt'), 'boot failure')
    $script:events = [Collections.Generic.List[string]]::new()
    $script:available = 8GB
    $script:recoverySlot = $null
    $script:checkpoints = @()
    $script:original = $script:original.Replace('<key>Debug</key>', '<key>Security</key><dict><key>DmgLoading</key><string>Signed</string></dict><key>Debug</key>')
    [IO.File]::WriteAllText($script:config, $script:original)
    $script:recoverySource = Join-Path $script:fixtureRoot 'Recovery.vhd'
    [IO.File]::WriteAllText($script:recoverySource, 'source fixture')
    $script:recoveryHash = (Get-FileHash $script:recoverySource -Algorithm SHA256).Hash
}
Fixture
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot
Assert (($script:events -join ',') -eq 'Mount:True,Dismount') 'Collection must mount read-only and dismount'
Assert ([IO.File]::ReadAllText($script:config) -eq $script:original) 'Read-only collection changed the config'
Assert (@(Get-ChildItem $script:fixtureRoot -Recurse -Filter opencore-fixture.txt).Count -eq 2) 'The log was not collected'
Write-Host 'PASS read-only collection copies existing logs without changing config'

Fixture
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging
[xml]$changed = Get-Content $script:config -Raw
Assert ($changed.SelectSingleNode('//key[text()="Target"]').NextSibling.InnerText -eq '67') 'File logging was not enabled'
Assert ($changed.SelectSingleNode('//key[text()="AppleDebug"]').NextSibling.Name -eq 'true') 'Apple boot logging missing'
Assert ($changed.SelectSingleNode('//key[text()="ApplePanic"]').NextSibling.Name -eq 'true') 'Apple panic logging missing'
Assert ($changed.SelectSingleNode('//key[text()="preserve"]').NextSibling.InnerText -eq '-v cpus=1') 'Unrelated settings changed'
Assert (($script:events -join ',') -eq 'Mount:False,Dismount') 'Enable must write only the EFI mount'
Write-Host 'PASS diagnostic changes are confined to three settings and retain the original config'

Fixture
# Query the fixture drive's existing GUID with mountvol /L (read-only). This
# reproduces the actual mounted-volume path without mounting any disk or VM.
$driveRoot = [IO.Path]::GetPathRoot($script:volume)
$guidRoot = @(& "$env:SystemRoot\System32\mountvol.exe" $driveRoot /L | Where-Object { $_.Trim().StartsWith('\\?\Volume{') })
Assert ($LASTEXITCODE -eq 0 -and $guidRoot.Count -eq 1) 'Could not query the fixture volume GUID'
$script:volume = $guidRoot[0].Trim() + $script:volume.Substring($driveRoot.Length)
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging
[xml]$extendedConfig = [IO.File]::ReadAllText($script:config)
Assert ($extendedConfig.SelectSingleNode('//key[text()="Target"]').NextSibling.InnerText -eq '67') 'Extended-path configuration was not saved'
Assert (($script:events -join ',') -eq 'Mount:False,Dismount') 'Extended-path mount was not released'
Write-Host 'PASS Windows device paths use file streams rather than XML URI parsing'

Fixture
$script:vm.Notes = 'external'
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RestartFailedBoot } 'not managed'
Assert ($script:events.Count -eq 0) 'An external VM reached mutations'
Write-Host 'PASS unmanaged VMs are rejected before mounting or stopping'

Fixture
$script:vm.State = 'Running'
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot } 'Power off'
Assert ($script:events.Count -eq 0) 'A live disk reached mounting'
Write-Host 'PASS default collection refuses a live disk'

Fixture
$script:vm.State = 'Running'
$script:available = 5GB
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RestartFailedBoot } 'Not enough free RAM'
Assert (-not $script:events.Contains('Start')) 'The RAM reserve was bypassed'
Assert ($script:events.Contains('Dismount')) 'EFI disk was left mounted'
Write-Host 'PASS diagnostic restart retains the existing RAM reserve'

Fixture
$script:vm.State = 'Running'
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RestartFailedBoot
Assert (($script:events -join ',') -eq 'Stop,Mount:False,Dismount,Start,Console,Stop,Mount:True,Dismount') 'Unexpected restart/capture sequence'
Assert ($script:vm.State -eq 'Off') 'Capture should leave the guest powered off'
$backup = @(Get-ChildItem $script:fixtureRoot -Recurse -Filter config.before.plist)
Assert ($backup.Count -eq 1 -and [IO.File]::ReadAllText($backup[0].FullName) -eq $script:original) 'Collection overwrote the original configuration backup'
Write-Host 'PASS explicit failed-boot capture restarts once and collects from an offline disk'

Fixture
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RecoveryVhd $script:recoverySource -RecoveryVhdSha256 $script:recoveryHash -StartAfterRepair
Assert (($script:events -join ',') -eq 'Convert,Attach,Mount:False,Dismount,Start,Console') 'Repair should attach recovery and start only once'
Assert ($script:vm.State -eq 'Running') 'Repair should leave Recovery available to the user'
[xml]$repaired = Get-Content $script:config -Raw
Assert ($repaired.SelectSingleNode('//key[text()="DmgLoading"]').NextSibling.InnerText -eq 'Disabled') 'The RAM-disk recovery entry remained enabled'
$script:vm.State = 'Off'
$script:events.Clear()
& $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RecoveryVhd $script:recoverySource -RecoveryVhdSha256 $script:recoveryHash
Assert (-not $script:events.Contains('Convert') -and -not $script:events.Contains('Attach')) 'Retry duplicated recovery conversion or attachment'
Write-Host 'PASS verified disk recovery attaches once, disables RAM disk and preserves the running guest after repair'

Fixture
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RecoveryVhd $script:recoverySource -RecoveryVhdSha256 ('0' * 64) } 'reviewed SHA-256'
Assert ($script:events.Count -eq 0) 'Unverified recovery reached mutations'
Write-Host 'PASS recovery digest mismatch is rejected before changes'

Fixture
$script:recoverySlot = Join-Path $script:fixtureRoot 'unrelated.vhdx'
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RecoveryVhd $script:recoverySource -RecoveryVhdSha256 $script:recoveryHash } 'occupied'
Assert ($script:events.Count -eq 0) 'Occupied slot reached mutations'
Write-Host 'PASS existing unrelated recovery attachments are preserved'

Fixture
$script:checkpoints = @([pscustomobject]@{ Name='Existing' })
Reject { & $program -VmId $script:vm.Id -EvidenceDirectory $script:fixtureRoot -EnableLogging -RecoveryVhd $script:recoverySource -RecoveryVhdSha256 $script:recoveryHash } 'without checkpoints'
Assert ($script:events.Count -eq 0) 'A VM with checkpoints reached disk changes'
Write-Host 'PASS checkpoints block recovery disk changes'

$collector = [scriptblock]::Create([IO.File]::ReadAllText((Join-Path $root 'src/Infrastructure/Scripts/CollectBootLogs.ps1')))
Fixture
$script:p = @{ vmId=$script:vm.Id; outputDirectory=(Join-Path $script:fixtureRoot 'export') }
New-Item -ItemType Directory $script:p.outputDirectory | Out-Null
$text = 'EB.MM.AKM __LINKEDIT STOP 0x16' + [char]0 + [char]0
[IO.File]::WriteAllText((Join-Path $script:volume 'opencore-fixture.txt'), $text)
$collected = & $collector | ConvertFrom-Json
Assert ($collected.success -and $collected.message.Contains('kernel memory-allocation failure')) 'Boot evidence was not diagnosed'
Assert ([IO.File]::ReadAllText((Join-Path $script:p.outputDirectory 'opencore-fixture.txt')) -eq $text.TrimEnd([char]0)) 'NUL padding was not removed'
Assert ([IO.File]::ReadAllText($script:config) -eq $script:original -and ($script:events -join ',') -eq 'Mount:True,Dismount') 'Boot export changed or left mounted the EFI disk'
Write-Host 'PASS app boot export reads offline EFI, strips preallocation padding and identifies the captured allocation failure'

Fixture
$script:p = @{ vmId=$script:vm.Id; outputDirectory=(Join-Path $script:fixtureRoot 'export') }
$script:vm.State = 'Running'
Reject { & $collector } 'Shut down'
$script:vm.State = 'Off'; $script:vm.Notes = 'external'
Reject { & $collector } 'limited to pome'
Assert ($script:events.Count -eq 0) 'Running or external VM reached boot collection'
Write-Host 'PASS app boot export rejects running and external VMs before mounting'

Fixture
$script:p = @{ vmId=$script:vm.Id; outputDirectory=(Join-Path $script:fixtureRoot 'export') }
New-Item -ItemType Directory $script:p.outputDirectory | Out-Null
$oversized = [IO.File]::OpenWrite((Join-Path $script:volume 'opencore-fixture.txt'))
try { $oversized.SetLength(4MB + 1) } finally { $oversized.Dispose() }
Reject { & $collector } '4 MB export limit'
Assert (($script:events -join ',') -eq 'Mount:True,Dismount') 'An export failure left the EFI disk mounted'
Write-Host 'PASS app boot export bounds input size and dismounts on failure'
Write-Host '14/14 boot diagnostic/repair/export scenarios passed; no real VM or host disk was used.'
