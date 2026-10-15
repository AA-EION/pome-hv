# Building

Requirements: Windows 10 build 19041 or later, x64; .NET SDK 10.0.401; NuGet access for the first restore. The WinUI compiler and Windows SDK references arrive through pinned NuGet packages. Visual Studio is optional.

```powershell
./tools/Build/Build.ps1 -Configuration Release
```

Artifacts are assembled in `artifacts/app`. Run `Pome.UI.exe` from that complete folder, preserving its `.pri`, native DLLs, and `helper` subdirectory. Do not copy only the executable. The helper uses the ordinary .NET 10 runtime; Windows App SDK native runtime files are included with the GUI.

Individual commands:

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.tools/dotnet"
$env:NUGET_PACKAGES = "$PWD/.tools/packages"
dotnet restore Pome.sln -p:Platform=x64 --locked-mode
dotnet build Pome.sln -c Release -p:Platform=x64 --no-restore -m:1 -nr:false
dotnet run --project tests/Unit/Pome.Tests.csproj -c Release --no-build
```

The solution maps platform-neutral core/helper/test projects to Any CPU and the UI to x64. Do not pass `Platform=x64` to an individual `dotnet run --no-build` test command after building the solution.

For packaging, install Inno Setup 6 and compile `installer/pome.iss` after assembling a Release build. Signing credentials are intentionally absent. Production distribution requires Authenticode signing, protected installation paths, installer QA and the release checklist in [DEVELOPMENT](DEVELOPMENT.md).
