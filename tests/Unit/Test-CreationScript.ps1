# Execute the real creation program against cmdlet doubles; no real VM/disk.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$program = [scriptblock]::Create([IO.File]::ReadAllText((Join-Path $repo 'src/Infrastructure/Scripts/CreateVm.ps1')))
function Assert([bool]$value, [string]$message) { if (-not $value) { throw $message } }
function Import-Module { param($Name,$ErrorAction) }
function Get-VM { [CmdletBinding()] param() if ($script:created) { $script:created } }
function New-VHD {
    param($Path,[switch]$Dynamic,$SizeBytes)
    Assert ($Path.StartsWith($script:fixtureRoot)) 'Disk escaped fixture'
    if ([IO.File]::Exists($Path)) { throw 'Existing disk would be overwritten' }
    [IO.File]::WriteAllText($Path,'fixture disk')
}
function Get-VHD {
    [CmdletBinding()] param($Path)
    Assert ($Path.StartsWith($script:fixtureRoot)) 'Disk query escaped fixture'
    if ($Path.EndsWith('Recovery.vhd')) { [pscustomobject]@{ Attached=$false; VhdType=$(if($script:badSource){'Differencing'}else{'Fixed'}); VhdFormat='VHD' } }
    else { [pscustomobject]@{ Attached=$false; VhdType='Dynamic'; VhdFormat='VHDX' } }
}
function Mount-VHD { [CmdletBinding()] param($Path,[switch]$Passthru) $script:events.Add('Mount:' + [IO.Path]::GetFileName($Path)); [pscustomobject]@{ DiskNumber=731 } }
function Dismount-VHD { [CmdletBinding()] param($Path) $script:events.Add('Dismount:' + [IO.Path]::GetFileName($Path)) }
function Get-Disk { [CmdletBinding()] param($Number) Assert ($Number -eq 731) 'Wrong disk number'; [pscustomobject]@{ Number=731; PartitionStyle='RAW' } }
function Initialize-Disk { param([Parameter(ValueFromPipeline)]$InputObject,$PartitionStyle,[switch]$PassThru) process { $InputObject } }
function New-Partition { param([Parameter(ValueFromPipeline)]$InputObject,[switch]$UseMaximumSize,[switch]$AssignDriveLetter) process { Assert (-not $AssignDriveLetter) 'EFI creation requested a drive letter'; [pscustomobject]@{ Number=$InputObject.Number; PartitionNumber=2 } } }
function Add-PartitionAccessPath { [CmdletBinding()] param($DiskNumber,$PartitionNumber,$AccessPath) Assert ($DiskNumber -eq 731 -and $PartitionNumber -eq 2 -and $AccessPath.StartsWith($script:fixtureRoot)) 'EFI mounted outside its operation folder'; $script:events.Add('MountFolder') }
function Remove-PartitionAccessPath { [CmdletBinding()] param($DiskNumber,$PartitionNumber,$AccessPath) $script:events.Add('UnmountFolder') }
function Format-Volume {
    [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$InputObject,$FileSystem,$NewFileSystemLabel,[switch]$Force)
    process { Assert ($InputObject.Number -eq 731) 'Formatting the wrong disk'; [pscustomobject]@{DriveLetter=$null} }
}
function Copy-Item { param($LiteralPath,$Destination,[switch]$Recurse) $script:copies.Add($LiteralPath) }
function Convert-VHD {
    [CmdletBinding()] param($Path,$DestinationPath,$VHDType)
    Assert (-not $script:created) 'Converted recovery after registering the VM'
    Assert ($Path -eq (Join-Path $script:fixtureRoot 'Recovery.vhd') -and $DestinationPath -eq (Join-Path $script:fixtureRoot 'Recovery.vhdx')) 'Wrong recovery conversion paths'
    $script:events.Add('Convert'); [IO.File]::WriteAllText($DestinationPath,'converted fixture')
}
function New-VM { param($Name,$Generation,$Path,[switch]$NoVHD,$MemoryStartupBytes) $script:created=[pscustomobject]@{Id=[Guid]::NewGuid();Name=$Name}; $script:created }
function Set-VM { param($VM,$Notes,$AutomaticCheckpointsEnabled,$AutomaticStartAction,$AutomaticStopAction) }
function Set-VMProcessor { param($VM,$Count,$HwThreadCountPerCore) Assert ($HwThreadCountPerCore -eq 1) 'Guest thread topology changed' }
function Set-VMMemory { param($VM,$DynamicMemoryEnabled,$StartupBytes) Assert (-not $DynamicMemoryEnabled) 'Guest memory became dynamic' }
function Get-VMNetworkAdapter { param($VM) [pscustomobject]@{} }
function Connect-VMNetworkAdapter { param([Parameter(ValueFromPipeline)]$InputObject,$SwitchName) process {} }
function Add-VMHardDiskDrive { param($VM,$Path,$ControllerType,$ControllerNumber,$ControllerLocation) $script:attachments.Add([pscustomobject]@{Path=$Path;Slot=$ControllerLocation}) }
function Get-VMHardDiskDrive { param($VM) $script:attachments }
function Set-VMFirmware { param($VM,$EnableSecureBoot,$FirstBootDevice) Assert ($FirstBootDevice.Path -eq (Join-Path $script:fixtureRoot 'EFI.vhdx') -and $EnableSecureBoot -eq 'Off') 'Unexpected guest firmware' }
function Remove-VM { [CmdletBinding()] param($VM,[switch]$Force) $script:events.Add('RollbackVm') }
function Fixture([bool]$automatic) {
    $script:fixtureRoot=Join-Path $repo ('artifacts/tests/create-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $script:fixtureRoot | Out-Null
    $script:events=[Collections.Generic.List[string]]::new(); $script:copies=[Collections.Generic.List[string]]::new()
    $script:attachments=[Collections.Generic.List[object]]::new(); $script:created=$null; $script:badSource=$false
    $script:p=@{ resourceDirectory=$script:fixtureRoot; plan=@{ operationId=[Guid]::NewGuid(); name='Fixture'; buildEfiFromArchive=$automatic; cpuCount=4; memoryGiB=4; diskGiB=64; switchName='Default Switch'; recoverySource=$null } }
    if (-not $automatic) { [IO.File]::WriteAllText((Join-Path $script:fixtureRoot 'EFI.vhdx'),'custom EFI') }
    else { foreach ($folder in 'EFI','Scripts','Tools','com.apple.recovery.boot') { New-Item -ItemType Directory (Join-Path $script:fixtureRoot ('BootFiles/' + $folder)) | Out-Null } }
}
Fixture $true
$result = & $program | ConvertFrom-Json
Assert $result.success 'Automatic creation failed'
Assert (($script:attachments.Slot -join ',') -eq '0,1,2') 'Automatic VM did not receive EFI, destination and recovery disks'
$copied = @($script:copies | ForEach-Object { Split-Path $_ -Leaf })
Assert ($script:events.Contains('Convert') -and ($copied -join ',') -eq 'EFI,Scripts,Tools') 'Automatic creation copied RAM-disk recovery, skipped guest tools or skipped conversion'
Assert ($script:events.Contains('MountFolder') -and $script:events.Contains('UnmountFolder') -and $script:events.IndexOf('UnmountFolder') -lt $script:events.IndexOf('Dismount:EFI.vhdx')) 'EFI folder mount was not released before dismount'
Assert ((Get-Content (Join-Path $script:fixtureRoot 'operation.json') -Raw | ConvertFrom-Json).stage -eq 'Created') 'Creation journal did not reach Created'
Write-Host 'PASS automatic creation converts recovery before registration, attaches three disks, mounts EFI to a private folder and copies EFI plus guest tools (never RAM-disk recovery)'
Fixture $true
$script:badSource=$true
$result = & $program | ConvertFrom-Json
Assert (-not $result.success -and -not $script:created -and $script:attachments.Count -eq 0) 'Invalid recovery reached VM registration'
Assert ([IO.File]::Exists((Join-Path $script:fixtureRoot 'EFI.vhdx'))) 'Failed preparation removed evidence'
Write-Host 'PASS invalid recovery preserves evidence and prevents VM registration'
Fixture $false
$result = & $program | ConvertFrom-Json
Assert ($result.success -and $script:attachments.Count -eq 2 -and -not $script:events.Contains('Convert') -and $script:copies.Count -eq 0) 'Manual creation altered custom boot media'
Write-Host 'PASS manual media remains unchanged and optional recovery is not invented'
Write-Host '3/3 creation workflow scenarios passed; all Hyper-V and disk cmdlets were simulated.'
