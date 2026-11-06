Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id ([Guid]$p.vmId) -ErrorAction Stop
if (-not ([string]$vm.Notes).StartsWith('pome:v1:')) { throw 'This VM is not managed by pome. Existing third-party VMs are read-only.' }
$message = 'Hyper-V accepted the request.'
switch ($p.kind) {
    'Start' {
        if ($vm.State -notin @('Off', 'Saved')) { throw 'Only an off or saved VM can be started. Refresh its current state.' }
        $free = [UInt64](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1024
        # skipMemoryReserve: developer runs (Pome.BootCheck start --skip-memory-reserve) let Windows page
        # out to make room instead of keeping 2 GB free; Hyper-V still refuses if it cannot back the VM.
        if (-not $p.skipMemoryReserve -and $free -lt ([UInt64]$vm.MemoryStartup + 2GB)) { throw 'Not enough free memory to start this VM while keeping 2 GB available for Windows. Close memory-intensive apps, then retry.' }
        Start-VM -VM $vm -ErrorAction Stop
        $message = 'VM started.'
    }
    'Shutdown' {
        if ($vm.State -ne 'Running') { throw 'Only a running VM can be shut down gracefully.' }
        # The shutdown integration service is answered by hvshutdownd inside macOS. Installers and
        # guests without pome's guest tools never answer, so explain the choice instead of hanging.
        $service = @(Get-VMIntegrationService -VM $vm | Where-Object { ([string]$_.Id).EndsWith('\9F8233AC-BE49-4C79-8EE3-E7E1985B2077', [StringComparison]::OrdinalIgnoreCase) })
        if ($service.Count -ne 1 -or -not $service[0].Enabled -or [string]$service[0].PrimaryOperationalStatus -ne 'Ok') {
            throw 'macOS is not answering Hyper-V shutdown requests. Shut down from the Apple menu, or install the guest tools (run Scripts/post-install.sh from the POME-BOOT disk inside macOS). Use Turn off to power the VM off immediately; unsaved guest data is lost.'
        }
        Stop-VM -VM $vm -Force -ErrorAction Stop
        $message = 'macOS was asked to shut down.'
    }
    'TurnOff' {
        if ($vm.State -in @('Off', 'Saved')) { throw 'This VM is already off.' }
        Stop-VM -VM $vm -TurnOff -Force -ErrorAction Stop
        $message = 'VM turned off. Like unplugging a computer, unsaved guest data is lost.'
    }
    'Reset' {
        if ($vm.State -ne 'Running') { throw 'Only a running VM can be reset.' }
        Restart-VM -VM $vm -Force -ErrorAction Stop
        $message = 'VM reset. OpenCore starts again; unsaved guest data is lost.'
    }
    'Pause' {
        if ($vm.State -ne 'Running') { throw 'Only a running VM can be paused.' }
        Suspend-VM -VM $vm -ErrorAction Stop
        $message = 'VM paused.'
    }
    'Resume' {
        if ($vm.State -ne 'Paused') { throw 'Only a paused VM can be resumed.' }
        Resume-VM -VM $vm -ErrorAction Stop
        $message = 'VM resumed.'
    }
    'Save' {
        if ($vm.State -notin @('Running', 'Paused')) { throw 'Only a running or paused VM can be saved.' }
        Save-VM -VM $vm -ErrorAction Stop
        $message = 'VM state saved.'
    }
    default { throw 'Unsupported lifecycle action.' }
}
@{ success=$true; message=$message; vmId=$vm.Id.ToString() } | ConvertTo-Json -Compress
