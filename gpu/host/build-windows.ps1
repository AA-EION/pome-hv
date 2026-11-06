# Builds pvg-host and its Venus back end (libvirglrenderer-1.dll) for Windows x64.
# Copyright 2026 pome contributors. SPDX-License-Identifier: MIT
#
# Needs: Visual Studio 2022+ with the C++ workload and CMake, MSYS2 with mingw-w64-ucrt-x86_64-gcc,
# Python 3.10+, git. Everything else (meson, ninja, mako, Vulkan headers, virglrenderer and
# venus-protocol) is fetched at pinned versions into -Work.
#
#   powershell -ExecutionPolicy Bypass -File gpu\host\build-windows.ps1 [-Work <dir>] [-Out <dir>] [-SkipVirgl]
#
# Output (-Out, default gpu\host\out): pvg-host.exe, pvg-host-tests.exe, libvirglrenderer-1.dll,
# libwinpthread-1.dll and THIRD_PARTY.txt. pome copies the folder next to pome.exe.
param(
    [string]$Work = (Join-Path $env:TEMP 'pvg-build'),
    [string]$Out = (Join-Path $PSScriptRoot 'out'),
    [string]$Msys = 'C:\msys64',
    [switch]$SkipVirgl,
    [switch]$NoVenusSelfTest   # CI runners have no Vulkan GPU
)
$ErrorActionPreference = 'Stop'

$VirglRepo = 'https://gitlab.freedesktop.org/virgl/virglrenderer.git'
$VirglRev = '244fc1a13ac85f462b20fd21dcc6e3c37f83249c'   # 2026-11-05, Venus protocol 1.1.3
$VkHeadersRepo = 'https://github.com/KhronosGroup/Vulkan-Headers.git'
$VkHeadersTag = 'v1.4.367'

function Step($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Run($exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $($args -join ' ') failed ($LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force $Work, $Out | Out-Null
$ucrt = Join-Path $Msys 'ucrt64\bin'

# --- Python tools for meson (isolated venv) -------------------------------------------------
$venv = Join-Path $Work 'venv'
if (-not (Test-Path "$venv\Scripts\python.exe")) {
    Step 'Python venv with meson, ninja and mako'
    Run python -m venv $venv
    Run "$venv\Scripts\python.exe" -m pip install --quiet 'meson==1.12.1' 'ninja==1.13.2' 'mako==1.4.3'
}
# meson's generators run "python3"; the venv only ships python.exe.
if (-not (Test-Path "$venv\Scripts\python3.exe")) { Copy-Item "$venv\Scripts\python.exe" "$venv\Scripts\python3.exe" }

# --- Vulkan headers --------------------------------------------------------------------------
$vkh = Join-Path $Work 'Vulkan-Headers'
if (-not (Test-Path "$vkh\include\vulkan\vulkan.h")) {
    Step "Vulkan-Headers $VkHeadersTag"
    Run git clone --quiet --depth 1 --branch $VkHeadersTag $VkHeadersRepo $vkh
}

# --- virglrenderer (Venus only, MinGW-w64 UCRT) ----------------------------------------------
if (-not $SkipVirgl) {
    if (-not (Test-Path "$ucrt\gcc.exe")) { throw "MSYS2 UCRT64 gcc not found at $ucrt (pacman -S mingw-w64-ucrt-x86_64-gcc)" }
    $src = Join-Path $Work 'virglrenderer'
    if (-not (Test-Path "$src\.git")) {
        Step "virglrenderer $($VirglRev.Substring(0,12))"
        Run git clone --quiet $VirglRepo $src
    }
    Run git -C $src fetch --quiet origin $VirglRev
    Run git -C $src checkout --quiet --force $VirglRev
    git -C $src clean --quiet -fdx --exclude=subprojects
    if ($LASTEXITCODE -ne 0) { throw 'git clean failed' }
    Step 'Port virglrenderer to Windows'
    Run "$venv\Scripts\python.exe" (Join-Path $PSScriptRoot 'virglrenderer\apply_windows.py') $src

    $bld = Join-Path $Work 'virgl-build'
    $savedPath = $env:PATH
    try {
        # venv first (python3 for mako), then MinGW; MSYS2's own python must not win.
        $env:PATH = "$venv\Scripts;$ucrt;$env:PATH"
        $env:CC = 'gcc'
        $inc = ((Join-Path $src 'src\pvg_win32_compat.h') -replace '\\', '/')
        $idir = ((Join-Path $src 'pvg-win32-include') -replace '\\', '/')
        if (Test-Path $bld) { Remove-Item -Recurse -Force $bld }
        Step 'Configure virglrenderer'
        Run "$venv\Scripts\meson.exe" setup $bld $src --buildtype=release -Dvenus=true -Dvrend=false -Dvideo=false `
            -Dtests=false -Dvulkan-dload=true -Dplatforms= -Ddrm-renderers= "-Dc_args=-include $inc -I$idir"
        Step 'Build virglrenderer'
        Run "$venv\Scripts\ninja.exe" -C $bld
    } finally { $env:PATH = $savedPath; Remove-Item Env:CC -ErrorAction SilentlyContinue }
    Copy-Item "$bld\src\libvirglrenderer-1.dll" $Out -Force
    Copy-Item "$ucrt\libwinpthread-1.dll" $Out -Force
}

# --- pvg-host (MSVC) -------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Visual Studio not found (vswhere.exe missing)' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ x64 tools not found' }
$hostBuild = Join-Path $Work 'pvg-host-build'
Step 'Build pvg-host'
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
$cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cmake -S `"$PSScriptRoot`" -B `"$hostBuild`" -DPVG_VULKAN_HEADERS=`"$vkh\include`" && cmake --build `"$hostBuild`" --config Release"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'pvg-host build failed' }
Copy-Item "$hostBuild\Release\pvg-host.exe", "$hostBuild\Release\pvg-host-tests.exe" $Out -Force

@"
pvg-host bundles:
- virglrenderer $VirglRev (MIT), $VirglRepo, ported by gpu/host/virglrenderer/apply_windows.py
- venus-protocol 1.1.3 (MIT), https://gitlab.freedesktop.org/virgl/venus-protocol
- Mesa util code inside virglrenderer (MIT)
- winpthreads from MinGW-w64 (MIT and BSD-3-Clause), libwinpthread-1.dll
- Vulkan-Headers $VkHeadersTag (Apache-2.0 OR MIT), headers only
"@ | Set-Content -Encoding utf8 (Join-Path $Out 'THIRD_PARTY.txt')

Step 'Self-test'
& (Join-Path $Out 'pvg-host-tests.exe') | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'pvg-host unit tests failed' }
if (-not $SkipVirgl -and -not $NoVenusSelfTest) {
    & (Join-Path $Out 'pvg-host.exe') --selftest
    if ($LASTEXITCODE -ne 0) { throw 'Venus self-test failed (is a Vulkan 1.3 GPU driver installed?)' }
}
Write-Host "pvg-host ready in $Out"
