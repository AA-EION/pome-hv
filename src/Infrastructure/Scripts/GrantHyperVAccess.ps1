# Adds the signed-in user (resolved by the helper from its own token, never from the UI) to the
# built-in Hyper-V Administrators group so VM listing, power actions and the console need no UAC.
$group = [Security.Principal.SecurityIdentifier]'S-1-5-32-578'
$user = ([Security.Principal.SecurityIdentifier]([string]$p.userSid)).Value
# Get-LocalGroupMember can fail on groups with cloud or orphaned members, so add directly.
try {
    Add-LocalGroupMember -SID $group -Member $user -ErrorAction Stop
    $message = 'Added to Hyper-V Administrators. Sign out of Windows and back in, then pome can list, start, stop and open your VMs without administrator prompts. Creation and disk tasks still ask for approval.'
} catch [Microsoft.PowerShell.Commands.MemberExistsException] {
    $message = 'You are already in Hyper-V Administrators. If pome still asks for approval, sign out of Windows and back in.'
}
@{ success=$true; message=$message } | ConvertTo-Json -Compress
