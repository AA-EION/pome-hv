# -WithVirtualGpu also builds pvg-host and its Venus back end (gpu/host/build-windows.ps1; needs MSYS2
# UCRT64 gcc and Visual Studio C++) into <output>/pvg-host, where pome looks for it.
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputDirectory = 'artifacts/app', [switch]$WithVirtualGpu)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/dotnet'
    $env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
    dotnet restore Pome.sln -p:Platform=x64 --locked-mode -m:1 -nr:false
    if ($LASTEXITCODE) { throw 'Restore failed.' }
    dotnet build Pome.sln -c $Configuration -p:Platform=x64 --no-restore -m:1 -nr:false
    if ($LASTEXITCODE) { throw 'Build failed.' }
    dotnet run --project tests/Unit/Pome.Tests.csproj -c $Configuration --no-build
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    & (Join-Path $root 'tools/Validation/Validate-PowerShell.ps1')
    if ($LASTEXITCODE) { throw 'PowerShell validation failed.' }
    & (Join-Path $root 'tests/Unit/Test-ManagementScripts.ps1')
    if ($LASTEXITCODE) { throw 'VM management script tests failed.' }
    & (Join-Path $root 'tests/Unit/Test-BootDiagnostics.ps1')
    if ($LASTEXITCODE) { throw 'Boot diagnostic script tests failed.' }
    & (Join-Path $root 'tests/Unit/Test-CreationScript.ps1')
    if ($LASTEXITCODE) { throw 'Creation workflow script tests failed.' }
    $output = [IO.Path]::GetFullPath($OutputDirectory, $root)
    New-Item -ItemType Directory -Force $output | Out-Null
    Copy-Item -Path "src/UI/bin/x64/$Configuration/net10.0-windows10.0.19041.0/win-x64/*" -Destination $output -Recurse -Force
    $helper = Join-Path $output 'helper'
    New-Item -ItemType Directory -Force $helper | Out-Null
    Copy-Item -Path "src/PrivilegedService/bin/$Configuration/net10.0-windows10.0.19041.0/*" -Destination $helper -Recurse -Force

    # Bundle self-contained Python 3.12.10 runtime and isolated venv into artifacts/app/python
    $pyCacheDir = Join-Path $root '.tools/python'
    New-Item -ItemType Directory -Force $pyCacheDir | Out-Null
    $pyZip = Join-Path $pyCacheDir 'python-3.12.10-embed-amd64.zip'
    $expectedHash = '4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3'
    if (-not (Test-Path $pyZip) -or ((Get-FileHash $pyZip -Algorithm SHA256).Hash -ne $expectedHash)) {
        if (Test-Path "$env:TEMP\py312-embed.zip") {
            Copy-Item "$env:TEMP\py312-embed.zip" $pyZip -Force
        } else {
            Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $pyZip -UseBasicParsing
        }
        if ((Get-FileHash $pyZip -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'Bundled Python archive SHA-256 verification failed.'
        }
    }
    $pyOut = Join-Path $output 'python'
    $venvDir = Join-Path $pyOut 'venv'
    $venvScripts = Join-Path $venvDir 'Scripts'
    New-Item -ItemType Directory -Force $pyOut | Out-Null
    New-Item -ItemType Directory -Force $venvScripts | Out-Null
    Expand-Archive -Path $pyZip -DestinationPath $pyOut -Force
    Copy-Item -Path "$pyOut\*" -Destination $venvScripts -Include '*.exe','*.dll','*.pyd','*.zip','*._pth','*.cat' -Force
    Set-Content -Path (Join-Path $venvDir 'pyvenv.cfg') -Value "home = $venvScripts`ninclude-system-site-packages = false`nversion = 3.12.10`nexecutable = $(Join-Path $venvScripts 'python.exe')" -Encoding UTF8

    if ($WithVirtualGpu) {
        $pvg = Join-Path $output 'pvg-host'
        & (Join-Path $root 'gpu/host/build-windows.ps1') -Out $pvg
        if (-not (Test-Path (Join-Path $pvg 'pvg-host.exe'))) { throw 'pvg-host build failed.' }
        Remove-Item (Join-Path $pvg 'pvg-host-tests.exe') -ErrorAction SilentlyContinue
    }

    Write-Host "Built $output/Pome.UI.exe (with bundled Python venv at $venvScripts/python.exe)"
} finally { Pop-Location }
