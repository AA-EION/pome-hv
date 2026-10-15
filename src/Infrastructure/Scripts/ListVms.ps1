Import-Module Hyper-V -ErrorAction Stop
$items = @(Get-VM -ErrorAction Stop | ForEach-Object {
    @{ id=$_.Id.ToString(); name=$_.Name; state=$_.State.ToString(); processorCount=[int]$_.ProcessorCount; memoryAssigned=[UInt64]$_.MemoryAssigned; cpuUsage=[int]$_.CPUUsage; notes=[string]$_.Notes }
})
ConvertTo-Json -InputObject $items -Depth 4 -Compress
