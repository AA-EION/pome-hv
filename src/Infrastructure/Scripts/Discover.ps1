$warnings = [Collections.Generic.List[string]]::new()
$system = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$cpus = @(Get-CimInstance Win32_Processor)
$bios = Get-CimInstance Win32_BIOS
$edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
$switches = @()
$hyperV = $null
$featureEnabled = $null
$serviceState = $null
$moduleAvailable = $false
$managementError = $null
# Feature state, service state and management authorization are independent signals.
# HypervisorPresent alone may mean VBS, not the Hyper-V management feature.
try { $serviceState = [string](Get-Service -Name vmms -ErrorAction Stop).Status } catch {}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
        if ($feature.State -eq 'Enabled') { $featureEnabled = $true }
        elseif ($feature.State -eq 'Disabled' -or $feature.State -eq 'DisabledWithPayloadRemoved') { $featureEnabled = $false }
        else { $warnings.Add('Hyper-V feature state: ' + [string]$feature.State + '. A restart may be pending.') }
    } catch { $warnings.Add('Windows feature state could not be read; service and management checks are reported separately.') }
}
try {
    Import-Module Hyper-V -ErrorAction Stop
    $moduleAvailable = $true
    $switches = @(Get-VMSwitch -ErrorAction Stop | Select-Object -ExpandProperty Name)
    $hyperV = $true
} catch {
    $hyperV = $false
    $managementError = $_.Exception.Message
    $warnings.Add('Virtual machine management could not be queried. Inspect with administrator access to load machines and networks.')
}
$gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object {
    $location = $null
    try { $location = [string](@(Get-PnpDeviceProperty -InstanceId $_.PNPDeviceID -KeyName 'DEVPKEY_Device_LocationPaths' -ErrorAction Stop).Data | Select-Object -First 1) } catch {}
    @{ name=$_.Name; pnpId=$_.PNPDeviceID; driverVersion=$_.DriverVersion; locationPath=$location; drivesDisplay=$null; barBytes=$null }
})
@{
    manufacturer=$system.Manufacturer; model=$system.Model; bios=$bios.SMBIOSBIOSVersion
    os=$os.Caption; edition=$edition; build=[int]$os.BuildNumber; isServer=($os.ProductType -ne 1)
    cpu=($cpus.Name -join ', '); cpuVendor=($cpus.Manufacturer -join ', ')
    physicalCores=[int](($cpus | Measure-Object NumberOfCores -Sum).Sum)
    logicalProcessors=[int](($cpus | Measure-Object NumberOfLogicalProcessors -Sum).Sum)
    totalMemoryBytes=[UInt64]$system.TotalPhysicalMemory; availableMemoryBytes=([UInt64]$os.FreePhysicalMemory * 1024)
    virtualizationFirmware=$cpus[0].VirtualizationFirmwareEnabled; slat=$cpus[0].SecondLevelAddressTranslationExtensions
    hypervisorPresent=$system.HypervisorPresent; hyperVAvailable=$hyperV; switches=[string[]]@($switches); gpus=@($gpus); warnings=@($warnings.ToArray())
    hyperV=@{ featureEnabled=$featureEnabled; moduleAvailable=$moduleAvailable; serviceState=$serviceState; managementError=$managementError }
} | ConvertTo-Json -Depth 6 -Compress
