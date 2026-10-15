$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$failed = $false
Get-ChildItem -LiteralPath (Join-Path $root 'src/Infrastructure/Scripts') -Filter '*.ps1' | ForEach-Object {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { $failed = $true; $errors | ForEach-Object { Write-Error $_.Message -ErrorAction Continue } }
    else { Write-Host "Parsed $($_.Name)" }
}
if ($failed) { exit 1 }
