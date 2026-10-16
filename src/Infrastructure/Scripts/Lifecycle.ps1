Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId)
if (-not $vm.Notes.StartsWith('pome:v1:')) { throw 'This VM is not managed by pome. Existing third-party VMs are read-only.' }
switch ($p.kind) {
    'Start' {
        if ($vm.State -ne 'Off') { throw 'Only an off VM can be started by this action. Refresh its current state.' }
        $free = [UInt64](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
        if ($free -lt ([UInt64]$vm.MemoryStartup + 2GB)) { throw 'Not enough free memory to start this VM while keeping 2 GB available for Windows. Close memory-intensive apps, then retry.' }
        Start-VM -VM $vm -ErrorAction Stop
    }
    'Shutdown' { Stop-VM -VM $vm -ErrorAction Stop }
    default { throw 'Unsupported lifecycle action.' }
}
@{ success=$true; message='Hyper-V accepted the request. Refresh to inspect the resulting state.'; vmId=$vm.Id.ToString() } | ConvertTo-Json -Compress
