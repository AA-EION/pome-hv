param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet'
    $env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
    dotnet restore Pome.sln -p:Platform=x64 --locked-mode
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build Pome.sln -c $Configuration -p:Platform=x64 --no-restore -m:1 -nr:false
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet run --project tests/Unit/Pome.Tests.csproj -c $Configuration --no-build
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    $output = Join-Path $root 'artifacts/app'
    New-Item -ItemType Directory -Force $output | Out-Null
    Copy-Item -Path "src/UI/bin/x64/$Configuration/net10.0-windows10.0.19041.0/win-x64/*" -Destination $output -Recurse -Force
    $helper = Join-Path $output 'helper'
    New-Item -ItemType Directory -Force $helper | Out-Null
    Copy-Item -Path "src/PrivilegedService/bin/$Configuration/net10.0-windows10.0.19041.0/*" -Destination $helper -Recurse -Force
    Write-Host "Built $output/Pome.UI.exe"
} finally { Pop-Location }
