$warnings = [Collections.Generic.List[string]]::new()
$system = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$cpus = @(Get-CimInstance Win32_Processor)
$bios = Get-CimInstance Win32_BIOS
$edition = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').EditionID
$switches = @()
$hyperV = $null
try {
    Import-Module Hyper-V -ErrorAction Stop
    $switches = @(Get-VMSwitch -ErrorAction Stop | Select-Object -ExpandProperty Name)
    $hyperV = $true
} catch { $warnings.Add('Hyper-V management unavailable or access denied. Enable the feature, or use an account with Hyper-V permissions.'); }
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
} | ConvertTo-Json -Depth 6 -Compress
