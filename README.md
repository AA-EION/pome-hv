# pome

A native Windows home for macOS virtual machines on Hyper-V.

**Engineering preview, not a production release.** Pome builds as a WinUI 3 application, discovers the real host, prepares and validates OpenCore configuration, and implements a narrowly scoped Hyper-V creation/lifecycle backend. No macOS guest boot, GPU assignment or Metal workload has been validated by this project yet.

## Build and run

On Windows x64 with .NET SDK 10.0.401:

```powershell
./tools/Build/Build.ps1
./artifacts/app/Pome.UI.exe
```

The script restores locked dependencies, builds every project, runs the executable test suite, and assembles the app and elevated helper. Windows App SDK is included locally; .NET 10 runtime is required. No Windows feature or VM is changed during the build.

## What is implemented

- Native Library, Create, This computer and Activity screens; async host assessment and resource advice.
- CIM hardware discovery, GPU inventory, existing Hyper-V switches and VM listing, explicit unknown capability states.
- Automatic media path for the reviewed Intel CPU matrix: pinned OSX-Hyper-V archive, Apple recovery via its authenticated `macrecovery.py`, unique identity, `ocvalidate`, GPT/FAT32 EFI VHDX construction and Generation 2 VM configuration. Requires an installed Python 3 executable. Recovery acquisition and VHDX/guest boot paths still require integration validation.
- Custom EFI/recovery VHDX input with SHA-256 checks; static RAM, CPU, disk, network and boot order.
- User-reviewed UAC helper, current-user named pipe with mutual process-ID checks, embedded PowerShell programs and structured data arguments.
- Start, graceful shutdown and VMConnect; external VMs are read-only.
- SQLite activity records, per-operation recovery markers, conservative rollback preserving disks, allowlisted diagnostic export.
- Separate opt-in NullMoth 2.0.2 download with integrity verification. No NullMoth code or binaries are bundled, installed or run.
- MMIO allocation planning, kext dependency checks, two isolated MacHyperVSupport source patches, and automated tests.

## Boundaries

Windows client DDA and macOS GPU-P remain disabled. A detected NVIDIA GPU is not proof of acceleration. The PCI patches are uncompiled research candidates, not a working driver. Host display ownership, MUX, BAR sizes, IOMMU isolation and power dependencies remain unknown.

Automatic AMD profiles, validated Tahoe installation, automatic update/rollback, device reassignment, unattended recovery, guest services, cloning and signed production installers remain future work. See [feature status](docs/FEATURE_STATUS.md), [GPU investigation](docs/PCI_RESEARCH.md), and [test evidence](TESTING.md).

Apple's macOS license restricts virtualization to qualifying Apple-branded hardware. Technical operation does not establish permission. The UI links the applicable license and requires acknowledgement. Separate download does not remove NullMoth's noncommercial restrictions.

Start with [BUILDING](BUILDING.md), [USER_GUIDE](USER_GUIDE.md), [ARCHITECTURE](ARCHITECTURE.md), [SECURITY](SECURITY.md), and [upstream research](docs/UPSTREAM_RESEARCH.md).
