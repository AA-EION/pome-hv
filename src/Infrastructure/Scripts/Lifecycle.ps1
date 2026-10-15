Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId)
if (-not $vm.Notes.StartsWith('pome:v1:')) { throw 'This VM is not managed by pome. Existing third-party VMs are read-only.' }
switch ($p.kind) {
    'Start' { Start-VM -VM $vm -ErrorAction Stop }
    'Shutdown' { Stop-VM -VM $vm -ErrorAction Stop }
    default { throw 'Unsupported lifecycle action.' }
}
@{ success=$true; message='Hyper-V accepted the request. Refresh to inspect the resulting state.'; vmId=$vm.Id.ToString() } | ConvertTo-Json -Compress
