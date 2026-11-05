# Deterministic cmdlet doubles exercise the real embedded programs. No Hyper-V
# module is loaded and no VM, disk, host feature or network is changed.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$management = Join-Path $root 'src/Infrastructure/Scripts/ManageVm.ps1'
$lifecycle = Join-Path $root 'src/Infrastructure/Scripts/Lifecycle.ps1'
$script:passed = 0
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Reset-Fixture {
    $script:fixture = [pscustomobject]@{ Id=[Guid]::NewGuid(); Name='Mac'; State='Off'; Notes='pome:v1:fixture'; ProcessorCount=4; MemoryStartup=8GB; Generation=2; CheckpointType='Production'; ConfigurationLocation='C:\fixture' }
    $script:adapter = [pscustomobject]@{ SwitchName='Default Switch' }
    $script:snapshots = @()
    $script:collision = $null
    $script:freeBytes = 12GB
    $script:checkpointFails = $false
    $script:targetDisk = $null
    $script:targetSize = 100GB
    $script:mutations = [Collections.Generic.List[string]]::new()
    $script:shutdownStatus = 'Ok'
}
function Test([string]$name, [scriptblock]$body) {
    Reset-Fixture
    & $body
    $script:passed++
    Write-Host "PASS $name"
}
function Reject([scriptblock]$body, [string]$contains) {
    try { & $body | Out-Null } catch {
        if ($_.Exception.Message.Contains($contains)) { return }
        throw
    }
    throw "Expected rejection containing '$contains'."
}
function Import-Module { param($Name, $ErrorAction) }
function Get-VM {
    [CmdletBinding()] param([Guid]$Id)
    if ($Id -and $Id -ne $script:fixture.Id) { throw 'VM does not exist' }
    $script:fixture
    if (-not $Id -and $script:collision) { $script:collision }
}
function Get-VMNetworkAdapter { param($VM) $script:adapter }
function Set-VMProcessor { param($VM, $Count, $HwThreadCountPerCore) $script:mutations.Add('Cpu'); $VM.ProcessorCount = $Count }
function Set-VMMemory { param($VM, $DynamicMemoryEnabled, $StartupBytes) $script:mutations.Add('Memory'); $VM.MemoryStartup = $StartupBytes }
function Connect-VMNetworkAdapter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject, $SwitchName)
    process { if ($SwitchName -eq 'Broken') { throw 'Injected network failure' }; $script:mutations.Add('Network'); $InputObject.SwitchName = $SwitchName }
}
function Disconnect-VMNetworkAdapter {
    [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject)
    process { $script:mutations.Add('Disconnect'); $InputObject.SwitchName = $null }
}
function Rename-VM { param($VM, $NewName) $script:mutations.Add('Rename'); $VM.Name = $NewName }
function Get-VMFirmware { param($VM) [pscustomobject]@{ SecureBoot='Off'; BootOrder=@([pscustomobject]@{ Description='EFI' }) } }
function Get-VMHardDiskDrive { param($VM) [pscustomobject]@{ Path='C:\fixture\EFI.vhdx'; ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=0 }; if ($script:targetDisk) { $script:targetDisk } }
function Get-VHD { [CmdletBinding()] param($Path) [pscustomobject]@{ Size=$(if ($Path -like '*macOS*') { $script:targetSize } else { 8GB }); FileSize=1GB; Attached=$false; VhdType='Dynamic' } }
function Resize-VHD { [CmdletBinding()] param($Path, $SizeBytes) $script:mutations.Add('Resize'); $script:targetSize = $SizeBytes }
function Set-VMFirmware { param($VM, $FirstBootDevice) $script:mutations.Add('Boot') }
function Get-VMSnapshot { param($VM, $SnapshotType) $script:snapshots }
function Set-VM { param($VM, $CheckpointType) $VM.CheckpointType = $CheckpointType }
function Checkpoint-VM { param($VM, $SnapshotName) if ($script:checkpointFails) { throw 'Injected checkpoint failure' }; $script:mutations.Add('Checkpoint') }
function Restore-VMSnapshot { param($VMSnapshot, $Confirm) $script:mutations.Add('Restore') }
function Remove-VMSnapshot { param($VMSnapshot, $Confirm) $script:mutations.Add('Delete') }
function Export-VM { [CmdletBinding()] param($VM, $Path) $script:mutations.Add('Export') }
function Get-CimInstance { param($ClassName) [pscustomobject]@{ FreePhysicalMemory=($script:freeBytes / 1024) } }
function Start-VM { [CmdletBinding()] param($VM) $script:mutations.Add('Start') }
function Stop-VM { [CmdletBinding()] param($VM, [switch]$Force, [switch]$TurnOff) $script:mutations.Add($(if ($TurnOff) { 'TurnOff' } else { 'Shutdown' })) }
function Restart-VM { [CmdletBinding()] param($VM, [switch]$Force) $script:mutations.Add('Reset') }
function Remove-VM { [CmdletBinding()] param($VM, [switch]$Force) $script:mutations.Add('RemoveVm') }
function Get-VMIntegrationService { param($VM) [pscustomobject]@{ Id=('Microsoft:' + $VM.Id + '\9F8233AC-BE49-4C79-8EE3-E7E1985B2077'); Enabled=$true; PrimaryOperationalStatus=$script:shutdownStatus } }
function Suspend-VM { [CmdletBinding()] param($VM) $script:mutations.Add('Pause') }
function Resume-VM { [CmdletBinding()] param($VM) $script:mutations.Add('Resume') }
function Save-VM { [CmdletBinding()] param($VM) $script:mutations.Add('Save') }
function Manage($request) { $p = $request; . $management | ConvertFrom-Json }
function Lifecycle($kind) { $p = @{ kind=$kind; vmId=$script:fixture.Id }; . $lifecycle | ConvertFrom-Json }

Test 'External VMs cannot reach management mutations' {
    $script:fixture.Notes = 'external'
    foreach ($kind in @('Configure','RepairBoot','Checkpoint','RestoreCheckpoint','DeleteCheckpoint','Export','ResizeDisk')) {
        Reject { Manage @{ kind=$kind; vmId=$script:fixture.Id } } 'read-only'
    }
    Assert ($script:mutations.Count -eq 0) 'An external VM was modified.'
}
Test 'Management rechecks powered-off state before any mutation' {
    foreach ($state in @('Running','Paused','Saved','Starting')) {
        $script:fixture.State = $state
        foreach ($kind in @('Configure','RepairBoot','Checkpoint','RestoreCheckpoint','DeleteCheckpoint','Export','ResizeDisk')) {
            Reject { Manage @{ kind=$kind; vmId=$script:fixture.Id } } 'Shut down'
        }
    }
    Assert ($script:mutations.Count -eq 0) 'A busy VM was modified.'
}
Test 'Settings rollback restores CPU, memory and network after a partial failure' {
    Reject { Manage @{ kind='Configure'; vmId=$script:fixture.Id; configuration=@{ name='Renamed'; cpuCount=6; memoryGiB=10; switchName='Broken' } } } 'Injected network failure'
    Assert ($script:fixture.ProcessorCount -eq 4 -and $script:fixture.MemoryStartup -eq 8GB -and $script:adapter.SwitchName -eq 'Default Switch' -and $script:fixture.Name -eq 'Mac') 'Rollback did not restore the original settings.'
}
Test 'Duplicate rename is rejected before settings change' {
    $script:collision = [pscustomobject]@{ Id=[Guid]::NewGuid(); Name='Other' }
    Reject { Manage @{ kind='Configure'; vmId=$script:fixture.Id; configuration=@{ name='other'; cpuCount=6; memoryGiB=10; switchName='Default Switch' } } } 'already exists'
    Assert ($script:mutations.Count -eq 0) 'Duplicate detection ran too late.'
}
Test 'Checkpoint failure restores the original checkpoint policy' {
    $script:checkpointFails = $true
    Reject { Manage @{ kind='Checkpoint'; vmId=$script:fixture.Id } } 'Injected checkpoint failure'
    Assert ($script:fixture.CheckpointType -eq 'Production') 'Checkpoint policy was changed permanently.'
}
Test 'Restore rejects a checkpoint that is not on this VM' {
    $script:snapshots = @([pscustomobject]@{ Id=[Guid]::NewGuid(); Name='Before install'; CreationTime=[DateTime]::Now })
    Reject { Manage @{ kind='RestoreCheckpoint'; vmId=$script:fixture.Id; checkpointId=[Guid]::NewGuid() } } 'no longer exists'
    Assert (-not $script:mutations.Contains('Restore')) 'An unrelated checkpoint was restored.'
}
Test 'Inspect exposes real settings without mutating external VMs' {
    $script:fixture.Notes = 'external'
    $result = Manage @{ kind='InspectVm'; vmId=$script:fixture.Id }
    Assert ($result.success -and $result.details.startupMemoryBytes -eq 8GB -and $result.details.disks.Count -eq 1) 'Incomplete settings response.'
    Assert ($script:mutations.Count -eq 0) 'Inspection changed a VM.'
}
Test 'Start retains Windows free-memory reserve and supports saved guests' {
    $script:fixture.State = 'Saved'; $script:freeBytes = 9GB
    Reject { Lifecycle 'Start' } 'Not enough free memory'
    Assert (-not $script:mutations.Contains('Start')) 'Start bypassed the memory reserve.'
    $script:freeBytes = 10GB
    $result = Lifecycle 'Start'
    Assert ($result.success -and $script:mutations.Contains('Start')) 'Saved guest could not start.'
}
Test 'Pause and resume reject stale state without invoking cmdlets' {
    Reject { Lifecycle 'Pause' } 'Only a running'
    Reject { Lifecycle 'Resume' } 'Only a paused'
    $script:fixture.State = 'Paused'; $result = Lifecycle 'Resume'
    Assert ($result.success -and $script:mutations.Count -eq 1 -and $script:mutations[0] -eq 'Resume') 'Unexpected lifecycle mutation.'
}
Test 'Settings can disconnect a stopped VM without changing disk or firmware' {
    $result = Manage @{ kind='Configure'; vmId=$script:fixture.Id; configuration=@{ name='Renamed'; cpuCount=6; memoryGiB=10; switchName=$null } }
    Assert ($result.success -and $result.details.name -eq 'Renamed' -and $result.details.startupMemoryBytes -eq 10GB -and -not $script:adapter.SwitchName) 'Settings did not match the reviewed request.'
    Assert (-not $script:mutations.Contains('Boot') -and -not $script:mutations.Contains('Resize')) 'Unrequested settings changed.'
}
Test 'Disk expansion rejects checkpoints and shrinking before invoking Resize-VHD' {
    $script:targetDisk = [pscustomobject]@{ Path='C:\fixture\macOS.vhdx'; ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=1 }
    $request = @{ kind='ResizeDisk'; vmId=$script:fixture.Id; diskGiB=200; systemDisk=$script:targetDisk.Path }
    $script:snapshots = @([pscustomobject]@{ Id=[Guid]::NewGuid() })
    Reject { Manage $request } 'Remove all checkpoints'
    $script:snapshots = @(); $request.diskGiB = 99
    Reject { Manage $request } 'Shrinking'
    Assert (-not $script:mutations.Contains('Resize')) 'Disk resize bypassed guards.'
}
Test 'Disk expansion rechecks the exact attached disk and grows only that disk' {
    $script:targetDisk = [pscustomobject]@{ Path='C:\fixture\macOS.vhdx'; ControllerType='SCSI'; ControllerNumber=0; ControllerLocation=1 }
    $request = @{ kind='ResizeDisk'; vmId=$script:fixture.Id; diskGiB=200; systemDisk='C:\other\macOS.vhdx' }
    Reject { Manage $request } 'original macOS disk changed'
    $request.systemDisk = $script:targetDisk.Path
    $result = Manage $request
    Assert ($result.success -and $script:targetSize -eq 200GB -and $script:mutations.Count -eq 1 -and $script:mutations[0] -eq 'Resize') 'An unrelated disk or setting changed.'
}
Test 'Shutdown explains a guest without tools instead of hanging, and turn off always works' {
    $script:fixture.State = 'Running'; $script:shutdownStatus = 'NoContact'
    Reject { Lifecycle 'Shutdown' } 'Turn off'
    Assert ($script:mutations.Count -eq 0) 'Shutdown was sent to a guest that cannot answer.'
    $script:shutdownStatus = 'Ok'
    $result = Lifecycle 'Shutdown'
    Assert ($result.success -and $script:mutations[0] -eq 'Shutdown') 'Graceful shutdown was not requested.'
    $result = Lifecycle 'TurnOff'
    Assert ($result.success -and $script:mutations[1] -eq 'TurnOff') 'Turn off did not use -TurnOff.'
}
Test 'Turn off and reset reject stopped guests' {
    foreach ($state in @('Off','Saved')) { $script:fixture.State = $state; Reject { Lifecycle 'TurnOff' } 'already off' }
    $script:fixture.State = 'Paused'; Reject { Lifecycle 'Reset' } 'Only a running'
    $script:fixture.State = 'Running'; $result = Lifecycle 'Reset'
    Assert ($result.success -and $script:mutations.Count -eq 1 -and $script:mutations[0] -eq 'Reset') 'Reset did not run exactly once.'
}
Test 'Removal rechecks disks and checkpoints before unregistering' {
    $request = @{ kind='DeleteVm'; vmId=$script:fixture.Id; operationFolder='C:\fixture' }
    $script:snapshots = @([pscustomobject]@{ Id=[Guid]::NewGuid() })
    Reject { Manage $request } 'checkpoints'
    $script:snapshots = @(); $request.operationFolder = 'C:\other'
    Reject { Manage $request } 'disk changed'
    Assert (-not $script:mutations.Contains('RemoveVm')) 'A VM was removed despite failed checks.'
    $request.operationFolder = 'C:\fixture'
    $result = Manage $request
    Assert ($result.success -and $script:mutations.Count -eq 1 -and $script:mutations[0] -eq 'RemoveVm' -and $result.resourceDirectory -eq 'C:\fixture') 'Removal did not unregister exactly this VM.'
    $script:fixture.State = 'Running'; $script:mutations.Clear()
    Reject { Manage $request } 'Shut down'
    $script:fixture.State = 'Off'; $script:fixture.Notes = 'external'
    Reject { Manage $request } 'read-only'
    Assert ($script:mutations.Count -eq 0) 'A running or external VM was removed.'
}
Write-Host "$script:passed/$script:passed management script scenarios passed. No real Hyper-V operations ran."
Test 'New destination initialization uses only the disk returned by mounting its exact VHDX' {
    $script:mountedPath = $null; $script:unmountedPath = $null; $script:formatted = $false; $script:diskStyle = 'RAW'
    function Mount-VHD { [CmdletBinding()] param($Path, [switch]$Passthru) $script:mountedPath=$Path; [pscustomobject]@{DiskNumber=731} }
    function Get-Disk { [CmdletBinding()] param($Number) Assert ($Number -eq 731) 'Used an unmounted disk number'; [pscustomobject]@{PartitionStyle=$script:diskStyle;Number=$Number} }
    function Initialize-Disk { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject,$PartitionStyle,[switch]$PassThru) process { Assert ($InputObject.Number -eq 731 -and $PartitionStyle -eq 'GPT') 'Wrong initialization target'; $InputObject } }
    function New-Partition { [CmdletBinding()] param([Parameter(ValueFromPipeline)]$InputObject,[switch]$UseMaximumSize) process { $InputObject } }
    function Format-Volume { [CmdletBinding(SupportsShouldProcess)] param([Parameter(ValueFromPipeline)]$InputObject,$FileSystem,$NewFileSystemLabel,[switch]$Force) process { Assert ($InputObject.Number -eq 731 -and $FileSystem -eq 'exFAT') 'Wrong format target'; $script:formatted=$true } }
    function Dismount-VHD { [CmdletBinding()] param($Path) $script:unmountedPath=$Path }
    $tokens=$null; $parseErrors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'src/Infrastructure/Scripts/CreateVm.ps1'),[ref]$tokens,[ref]$parseErrors)
    $functionAst=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-NewMacOsDisk'},$true)
    . ([scriptblock]::Create($functionAst.Extent.Text))
    Initialize-NewMacOsDisk 'C:\fixture\new-macOS.vhdx'
    Assert ($script:formatted -and $script:mountedPath -eq $script:unmountedPath) 'New disk was not formatted and dismounted'
    $script:formatted=$false; $script:diskStyle='GPT'; $script:unmountedPath=$null
    Reject { Initialize-NewMacOsDisk 'C:\fixture\existing.vhdx' } 'not blank'
    Assert (-not $script:formatted -and $script:unmountedPath -eq 'C:\fixture\existing.vhdx') 'Existing disk was changed or left mounted'
}
Write-Host "$script:passed/$script:passed total management/creation scenarios passed."
