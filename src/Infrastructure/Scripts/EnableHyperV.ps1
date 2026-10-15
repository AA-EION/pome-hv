$result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All -NoRestart -ErrorAction Stop
@{ success=$true; message=$(if ($result.RestartNeeded) { 'Hyper-V was enabled. Save your work and restart Windows when ready, then refresh pome.' } else { 'Hyper-V feature request completed. Refresh the computer assessment.' }) } | ConvertTo-Json -Compress
